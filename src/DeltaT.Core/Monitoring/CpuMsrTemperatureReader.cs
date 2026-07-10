using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace DeltaT.Core.Monitoring;

/// <summary>One direct read of the CPU's own thermal registers.</summary>
public readonly record struct CpuMsrReading(double TemperatureC, double? TjMaxC, bool Throttling);

/// <summary>Fast, precise CPU die temperature for CPUs the bundled LibreHardwareMonitor
/// build does not recognise. LHM is deliberately pinned to 0.9.4 (0.9.5+ regressed
/// Raptor Lake mobile to all-null temps — see DeltaT.Core.csproj), and 0.9.4 refuses to
/// create temperature sensors for any Intel model it can't name (MicroArchitecture.Unknown)
/// — which is every Arrow/Lunar/Panther-Lake part and everything after. Those machines
/// used to limp along on the ACPI thermal-zone fallback: EC-paced (readings 10–20 s
/// stale) and coarse. This reader restores full-rate, full-precision temperatures for
/// them by reading the silicon directly:
///
///  - Intel: IA32_THERM_STATUS (0x19C) per logical processor and IA32_PACKAGE_THERM_STATUS
///    (0x1B1), decoded against TjMax from MSR_TEMPERATURE_TARGET (0x1A2). These are
///    architectural MSRs — part of the x86 contract since Core 2, model-independent —
///    so any future Intel part reads correctly without a library update.
///  - AMD family 17h+ (Zen): SMN register THM_TCON_CUR_TMP (0x00059800) through the
///    northbridge index/data pair, unchanged across every Zen generation to date.
///    (0.9.4 already handles families 17h/19h/1Ah natively; this covers whatever
///    comes after.)
///
/// Access goes through LHM's already-loaded kernel driver. Ring0 is internal to LHM, so
/// it is bound by reflection against the pinned 0.9.4 assembly; every entry point is
/// defensive — if the binding or a read fails, the reader reports null and the caller
/// falls back to the ACPI zone. MSRs are only ever issued to the matching vendor
/// (reading undefined MSRs can fault in the kernel), so the vendor/family check gates
/// everything.</summary>
public sealed class CpuMsrTemperatureReader
{
    private const uint Ia32ThermStatus = 0x19C;
    private const uint Ia32PackageThermStatus = 0x1B1;
    private const uint MsrTemperatureTarget = 0x1A2;
    private const uint AmdThmTconCurTmp = 0x00059800;
    private const uint AmdPciIndexReg = 0x60;
    private const uint AmdPciDataReg = 0x64;

    private enum CpuVendor { Unknown, Intel, Amd }

    private bool _bound;
    private bool _broken;
    private int _consecutiveFailures;
    private const int GiveUpAfter = 5;

    private MethodInfo? _isOpen;
    private MethodInfo? _readMsrAffinity;
    private MethodInfo? _readPci;
    private MethodInfo? _writePci;

    private CpuVendor _vendor = CpuVendor.Unknown;
    private int _amdFamily = -1;
    private bool _identityResolved;
    private double? _tjMax;
    private GroupAffinity[]? _processors;

    /// <summary>Reads the current CPU temperature straight from the silicon, or null when
    /// this machine can't be read this way (unknown vendor, driver not loaded, binding
    /// failed). <paramref name="hardwareName"/> is the LHM CPU node's name — the CPUID
    /// brand string — used as the primary vendor signal.</summary>
    public CpuMsrReading? TryRead(string hardwareName)
    {
        if (_broken)
            return null;
        try
        {
            if (!EnsureBound() || !RingIsOpen())
                return null;
            ResolveIdentity(hardwareName);

            CpuMsrReading? reading = _vendor switch
            {
                CpuVendor.Intel => ReadIntel(),
                CpuVendor.Amd when _amdFamily >= 0x17 => ReadAmdSmn(),
                _ => null,
            };
            if (reading is not null)
                _consecutiveFailures = 0;
            return reading;
        }
        catch
        {
            if (++_consecutiveFailures >= GiveUpAfter)
                _broken = true; // the reflection surface or driver is unusable — stop probing
            return null;
        }
    }

    // ------------------------------------------------------------------ Intel

    private CpuMsrReading? ReadIntel()
    {
        double tjMax = _tjMax ??= ReadIntelTjMax();

        double? hottest = null;
        bool throttling = false;
        foreach (GroupAffinity lp in Processors())
        {
            if (!ReadMsr(Ia32ThermStatus, lp, out uint eax))
                continue;
            if ((eax & 0x8000_0000) == 0)
                continue; // reading-valid bit clear
            uint readout = (eax >> 16) & 0x7F; // degrees below TjMax
            double temp = tjMax - readout;
            if (hottest is not { } h || temp > h)
                hottest = temp;
            throttling |= (eax & 1) != 0 || readout == 0;
        }

        // Package sensor covers uncore heat the cores can miss. Only meaningful once a
        // core read proved this is a live modern die (avoids issuing 0x1B1 blindly).
        if (hottest is not null && ReadMsr(Ia32PackageThermStatus, Processors()[0], out uint pkg))
        {
            uint readout = (pkg >> 16) & 0x7F;
            if (readout > 0)
            {
                double temp = tjMax - readout;
                if (temp > hottest)
                    hottest = temp;
            }
            throttling |= (pkg & 1) != 0;
        }

        if (hottest is not { } value || value is <= 1 or >= 119)
            return null;
        return new CpuMsrReading(Math.Round(value, 1), tjMax, throttling);
    }

    private double ReadIntelTjMax()
    {
        if (ReadMsr(MsrTemperatureTarget, Processors()[0], out uint eax))
        {
            uint target = (eax >> 16) & 0xFF;
            if (target is >= 50 and <= 115)
                return target;
        }
        return 100; // the modern Intel default when the register is unreadable
    }

    // -------------------------------------------------------------------- AMD

    private CpuMsrReading? ReadAmdSmn()
    {
        // The SMN index/data pair is shared machine-wide; every monitoring tool
        // serialises on this named mutex (the LHM/OpenHardwareMonitor convention).
        Mutex? mutex = null;
        bool owned = false;
        try
        {
            try
            {
                mutex = new Mutex(false, @"Global\Access_PCI");
                try { owned = mutex.WaitOne(60); }
                catch (AbandonedMutexException) { owned = true; }
            }
            catch
            {
                // Can't create/open the mutex — proceed unserialised; the read is
                // idempotent and LHM itself isn't touching SMN for an unknown CPU.
            }

            object[] wArgs = { 0u, AmdPciIndexReg, AmdThmTconCurTmp };
            if (_writePci?.Invoke(null, wArgs) is not true)
                return null;
            object[] rArgs = { 0u, AmdPciDataReg, 0u };
            if (_readPci?.Invoke(null, rArgs) is not true)
                return null;
            uint raw = (uint)rArgs[2];

            double temp = ((raw >> 21) & 0x7FF) * 0.125;
            if ((raw & (1u << 19)) != 0)
                temp -= 49; // CUR_TEMP_RANGE_SEL: reading is offset into the -49..206 range
            if (temp is <= 1 or >= 119)
                return null;
            return new CpuMsrReading(Math.Round(temp, 1), null, Throttling: false);
        }
        finally
        {
            if (owned)
            {
                try { mutex!.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
        }
    }

    // -------------------------------------------------------------- identity

    private void ResolveIdentity(string hardwareName)
    {
        if (_identityResolved)
            return;
        _identityResolved = true;

        // The LHM node name is the CPUID brand string — the cheapest reliable signal.
        if (hardwareName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            _vendor = CpuVendor.Intel;
        else if (hardwareName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                 || hardwareName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
            _vendor = CpuVendor.Amd;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Caption FROM Win32_Processor");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    string manufacturer = mo["Manufacturer"] as string ?? "";
                    if (manufacturer.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase))
                        _vendor = CpuVendor.Intel;
                    else if (manufacturer.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase))
                        _vendor = CpuVendor.Amd;

                    // "AMD64 Family 26 Model 68 Stepping 0" — the family gates SMN access.
                    if (mo["Caption"] is string caption
                        && Regex.Match(caption, @"Family (\d+)") is { Success: true } m
                        && int.TryParse(m.Groups[1].Value, out int family))
                    {
                        _amdFamily = family;
                    }
                    break;
                }
            }
        }
        catch
        {
            // WMI unavailable — the brand-string vendor (if any) still stands, but
            // without a family number the AMD SMN path stays closed (vendor-gated
            // register access must never guess).
        }
    }

    // ------------------------------------------------------------- plumbing

    private bool EnsureBound()
    {
        if (_bound)
            return _isOpen is not null;
        _bound = true;
        try
        {
            Type? ring0 = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Ring0");
            if (ring0 is null)
                return false;
            _isOpen = ring0.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            _readMsrAffinity = ring0.GetMethod("ReadMsr", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(uint), typeof(uint).MakeByRefType(), typeof(uint).MakeByRefType(), typeof(GroupAffinity) }, null);
            _readPci = ring0.GetMethod("ReadPciConfig", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(uint), typeof(uint), typeof(uint).MakeByRefType() }, null);
            _writePci = ring0.GetMethod("WritePciConfig", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(uint), typeof(uint), typeof(uint) }, null);
            if (_isOpen is null || _readMsrAffinity is null)
                _isOpen = null; // binding incomplete — treat as unavailable
            return _isOpen is not null;
        }
        catch
        {
            _isOpen = null;
            return false;
        }
    }

    private bool RingIsOpen() => _isOpen?.Invoke(null, null) is true;

    private bool ReadMsr(uint index, GroupAffinity affinity, out uint eax)
    {
        object[] args = { index, 0u, 0u, affinity };
        bool ok = _readMsrAffinity?.Invoke(null, args) is true;
        eax = ok ? (uint)args[1] : 0;
        return ok;
    }

    /// <summary>One GroupAffinity per logical processor, across all processor groups
    /// (machines past 64 threads span several). Reading every LP and taking the max is
    /// simpler and safer than reconstructing the core topology — hyperthread siblings
    /// just report the same sensor twice.</summary>
    private GroupAffinity[] Processors()
    {
        if (_processors is not null)
            return _processors;
        var list = new List<GroupAffinity>();
        try
        {
            ushort groups = GetActiveProcessorGroupCount();
            for (ushort g = 0; g < groups; g++)
            {
                uint count = Math.Min(64, GetActiveProcessorCount(g));
                for (int i = 0; i < count; i++)
                    list.Add(GroupAffinity.Single(g, i));
            }
        }
        catch
        {
            // Group APIs unavailable (very old Windows) — fall back to group 0.
        }
        if (list.Count == 0)
        {
            int count = Math.Min(64, Environment.ProcessorCount);
            for (int i = 0; i < count; i++)
                list.Add(GroupAffinity.Single(0, i));
        }
        return _processors = list.ToArray();
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}

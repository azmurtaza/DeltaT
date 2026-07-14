using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;

namespace DeltaT.Core.Monitoring;

/// <summary>One direct read of the CPU's own thermal registers.</summary>
public readonly record struct CpuMsrReading(double TemperatureC, double? TjMaxC, bool Throttling);

/// <summary>Fast, precise CPU die temperature, read straight from the silicon. This is the
/// primary CPU temperature path (LHM's own CPU update costs ~122 ms; this costs ~1 ms), and
/// it is the ONLY path for CPUs LibreHardwareMonitor can't name — it refuses to create
/// temperature sensors for an Intel model it doesn't recognise, which is every part newer
/// than the pinned build. Those machines would otherwise limp along on the ACPI thermal
/// zone: EC-paced (10–20 s stale) and coarse.
///
///  - Intel: IA32_THERM_STATUS (0x19C) per logical processor and IA32_PACKAGE_THERM_STATUS
///    (0x1B1), decoded against TjMax from MSR_TEMPERATURE_TARGET (0x1A2). These are
///    architectural MSRs — part of the x86 contract since Core 2, model-independent —
///    so any future Intel part reads correctly without a library update.
///  - AMD family 17h+ (Zen): SMN register THM_TCON_CUR_TMP (0x00059800), unchanged across
///    every Zen generation to date.
///
/// Kernel access is PawnIO, through LHM's <see cref="IntelMsr"/> / <see cref="AmdFamily17"/>
/// modules, which are public API (0.9.6). The previous build reflected into LHM's internal
/// Ring0 class, which no longer exists: 0.9.5 replaced WinRing0 with PawnIO precisely
/// because WinRing0's ioctl was a kernel-wide privilege-escalation primitive that anti-cheat
/// blocks. PawnIO runs only signed modules, so nothing here can read outside what the module
/// implements.
///
/// Every entry point stays defensive: no PawnIO installed, a module that won't load, or a
/// failed read all report null, and the caller falls back to the ACPI zone. Note that a read
/// against an absent driver returns SUCCESS with a zeroed value, so a "true" is never trusted
/// on its own — validity bits and plausibility ranges gate every reading. MSRs are only ever
/// issued to the matching vendor (reading undefined MSRs can fault in the kernel), so the
/// vendor/family check gates everything.</summary>
public sealed class CpuMsrTemperatureReader : IDisposable
{
    private const uint Ia32ThermStatus = 0x19C;
    private const uint Ia32PackageThermStatus = 0x1B1;
    private const uint MsrTemperatureTarget = 0x1A2;
    private const uint AmdThmTconCurTmp = 0x00059800;

    private enum CpuVendor { Unknown, Intel, Amd }

    private bool _bound;
    private bool _broken;
    private int _consecutiveFailures;
    private const int GiveUpAfter = 5;

    private IntelMsr? _intelMsr;
    private AmdFamily17? _amdSmn;

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
            ResolveIdentity(hardwareName);
            if (!EnsureBound())
                return null;

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

    /// <summary>Zen's thermal register, read through PawnIO's AMD module. The module owns the
    /// SMN index/data pair (and the serialisation the old PCI-config path had to do by hand
    /// with the Global\Access_PCI mutex), so this is now one call.</summary>
    private CpuMsrReading? ReadAmdSmn()
    {
        if (_amdSmn is not { } smn)
            return null;
        uint raw = smn.ReadSmn(AmdThmTconCurTmp);

        double temp = ((raw >> 21) & 0x7FF) * 0.125;
        if ((raw & (1u << 19)) != 0)
            temp -= 49; // CUR_TEMP_RANGE_SEL: reading is offset into the -49..206 range
        if (temp is <= 1 or >= 119)
            return null;
        return new CpuMsrReading(Math.Round(temp, 1), null, Throttling: false);
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

    /// <summary>Load this vendor's PawnIO module, once. Nothing is loaded for a vendor we
    /// won't read (an unknown CPU, or an AMD family older than Zen), so an unsupported
    /// machine never opens a kernel handle at all.</summary>
    private bool EnsureBound()
    {
        if (_bound)
            return _intelMsr is not null || _amdSmn is not null;
        _bound = true;
        if (!PawnIoStatus.IsInstalled)
            return false; // no kernel driver on this machine — caller falls back to the ACPI zone
        try
        {
            switch (_vendor)
            {
                case CpuVendor.Intel:
                    _intelMsr = new IntelMsr();
                    break;
                case CpuVendor.Amd when _amdFamily >= 0x17:
                    _amdSmn = new AmdFamily17();
                    break;
            }
        }
        catch
        {
            // Module missing from this LHM build, or PawnIO refused to load it.
            _intelMsr = null;
            _amdSmn = null;
        }
        return _intelMsr is not null || _amdSmn is not null;
    }

    private bool ReadMsr(uint index, GroupAffinity affinity, out uint eax)
    {
        eax = 0;
        return _intelMsr is { } msr && msr.ReadMsr(index, out eax, out _, affinity);
    }

    public void Dispose()
    {
        try { _intelMsr?.Close(); } catch { /* driver already gone */ }
        try { _amdSmn?.Close(); } catch { /* driver already gone */ }
        _intelMsr = null;
        _amdSmn = null;
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

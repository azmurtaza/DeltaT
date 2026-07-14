using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads fan RPM from HP's BIOS sensor interface
/// (<c>HPBIOS_BIOSNumericSensor</c>) — the same firmware sensors the Linux
/// <c>hp-wmi-sensors</c> hwmon driver reads.
///
/// The safest probe of the four vendors: it invokes no method at all, it only SELECTs
/// from a sensor class, so there is no setter anywhere near this code path.
///
/// Protocol: each instance is one firmware sensor. <c>BaseUnits</c> = 19 means RPM
/// (the CIM numeric-sensor unit enum); <c>CurrentReading</c> is the value, scaled by
/// <c>UnitModifier</c> as a power of ten. <c>Name</c> labels it ("CPU Fan", "CPU0 Fan",
/// "GPU Fan"), which is how the CPU-side and GPU-side fans are told apart.
///
/// Coverage caveat, and it is a real one: this interface is a **business-class** HP
/// feature (EliteBook / ProBook / Z). HP's consumer gaming line (Omen, Victus) does not
/// expose it — their fan RPM is only reachable through raw Embedded-Controller port I/O,
/// which DeltaT deliberately does not do (model-specific register maps, EC-lockup risk).
/// So an Omen stays dark here, by design, until the EC path is taken on.
///
/// NOT YET VERIFIED ON REAL HP HARDWARE. Self-gating (see <see cref="AcerWmiFanReader"/>)
/// keeps that safe: absent class, absent namespace, or no RPM-flavoured sensor and the
/// probe latches dark, leaving DeltaT exactly where it is today rather than inventing
/// numbers. Not thread-safe by design: call it from the monitor thread.</summary>
public sealed class HpWmiFanReader : ILaptopFanProbe
{
    /// <summary>CIM numeric-sensor BaseUnits code for revolutions per minute.</summary>
    private const int BaseUnitsRpm = 19;

    // HP has shipped this class under both namespaces across BIOS generations.
    private static readonly string[] Namespaces = { @"root\HP\InstrumentedBIOS", @"root\WMI" };

    private const int GiveUpAfterMisses = 30;

    public string Vendor => "HP";
    public bool IsDead => _dead;

    private string? _namespace;
    private bool _initTried;
    private bool _dead;
    private bool _everAnswered;
    private int _consecutiveMisses;

    public LaptopFanSample Read()
    {
        if (_dead)
            return default;
        if (!_initTried)
            Init();
        if (_namespace is null)
            return default;

        double? cpu = null, gpu = null;
        bool anyAnswered = false;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                _namespace, "SELECT Name, CurrentReading, BaseUnits, UnitModifier FROM HPBIOS_BIOSNumericSensor");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (!IsRpmSensor(mo))
                        continue;
                    // A fan sensor exists and answered: right vendor, right interface,
                    // even if the fan is parked at 0.
                    anyAnswered = true;
                    double? rpm = PlausibleRpm(ReadValue(mo));
                    if (rpm is null)
                        continue;
                    if (IsGpuFan(mo["Name"] as string))
                        gpu ??= rpm;
                    else
                        cpu ??= rpm;
                }
            }
        }
        catch
        {
            // WMI hiccup: counts as a miss, handled below.
        }

        if (anyAnswered)
        {
            _everAnswered = true;
            _consecutiveMisses = 0;
        }
        else if (++_consecutiveMisses >= GiveUpAfterMisses && !_everAnswered)
        {
            _dead = true;
        }
        return new LaptopFanSample(cpu, gpu);
    }

    private void Init()
    {
        _initTried = true;
        foreach (string ns in Namespaces)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    ns, "SELECT Name FROM HPBIOS_BIOSNumericSensor");
                using ManagementObjectCollection found = searcher.Get();
                foreach (ManagementBaseObject mo in found)
                {
                    mo.Dispose();
                    _namespace = ns;
                    return;
                }
            }
            catch
            {
                // Namespace or class absent here: try the next one.
            }
        }
        _dead = true; // not an HP business BIOS (or an Omen/Victus, which never exposes this)
    }

    private static bool IsRpmSensor(ManagementBaseObject mo)
    {
        try
        {
            return mo["BaseUnits"] is { } units && Convert.ToInt32(units) == BaseUnitsRpm;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private static double? ReadValue(ManagementBaseObject mo)
    {
        try
        {
            if (mo["CurrentReading"] is not { } raw)
                return null;
            double value = Convert.ToDouble(raw);
            // UnitModifier is a power-of-ten scale: a reading of 250 at modifier 1 is 2500 RPM.
            int modifier = mo["UnitModifier"] is { } m ? Convert.ToInt32(m) : 0;
            return value * Math.Pow(10, modifier);
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>HP labels the GPU-side fan; anything else (CPU Fan, CPU0 Fan, Fan 1) is
    /// treated as the CPU-side one, which is also the right default on a single-fan
    /// chassis: both feed the same chassis-fan proxy downstream.</summary>
    public static bool IsGpuFan(string? name) =>
        name is not null && name.Contains("GPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>A parked fan (0) and any out-of-range reading map to null.</summary>
    public static double? PlausibleRpm(double? rpm) => rpm is > 0 and < 10000 ? rpm : null;

    public void Dispose()
    {
        _namespace = null;
        _dead = true;
    }
}

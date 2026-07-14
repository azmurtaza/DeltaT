using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads CPU and GPU fan RPM from ASUS's ATK firmware interface
/// (<c>root\WMI:AsusAtkWmi_WMNB</c>) — the channel Armoury Crate and G-Helper poll on
/// ROG/TUF/Zenbook machines. Laptop fans sit behind the EC where LibreHardwareMonitor
/// sees nothing, so on these machines this is the only fan telemetry there is.
///
/// Strictly read-only: the sole method ever invoked is <c>DSTS</c> ("device status",
/// ASUS's getter). Its sibling <c>DEVS</c> ("device set") is what writes fan curves and
/// performance modes, and is never called from here, so profiles stay untouched.
///
/// Protocol (from the Linux <c>asus-wmi</c> driver, which reads the same firmware:
/// <c>ASUS_WMI_DEVID_CPU_FAN_CTRL</c> / <c>_GPU_FAN_CTRL</c> under
/// <c>ASUS_WMI_METHODID_DSTS</c>): pass the device id, take the low 16 bits of the
/// answer, multiply by 100 to get RPM. Reading a fan through the *_FAN_CTRL id looks odd
/// but is correct: on ASUS firmware that id is a status word whose low half carries the
/// current speed.
///
/// NOT YET VERIFIED ON REAL ASUS HARDWARE. The self-gating (see <see cref="AcerWmiFanReader"/>)
/// is what makes that safe: if the class is absent, the method missing, or the answer
/// shaped differently than assumed, the probe latches dark and DeltaT simply has no fan
/// data on that machine — the same as today. It can degrade to nothing; it cannot degrade
/// to wrong numbers. Because the exact out-parameter name of DSTS varies across the MOF
/// revisions in the wild, any numeric output the method returns is accepted.
/// Not thread-safe by design: call it from the monitor thread.</summary>
public sealed class AsusWmiFanReader : ILaptopFanProbe
{
    // asus-wmi device ids. A single-fan machine answers 0 (or errors) for the GPU id.
    private const uint CpuFanDeviceId = 0x00110013;
    private const uint GpuFanDeviceId = 0x00110014;

    private const int GiveUpAfterMisses = 30;

    public string Vendor => "ASUS";
    public bool IsDead => _dead;

    private ManagementObject? _instance;
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
        if (_instance is null)
            return default;

        bool anyAnswered = false;
        double? cpu = ReadRpm(CpuFanDeviceId, ref anyAnswered);
        double? gpu = ReadRpm(GpuFanDeviceId, ref anyAnswered);

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
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM AsusAtkWmi_WMNB");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                _instance = (ManagementObject)mo;
                return;
            }
        }
        catch
        {
            // Not ASUS ATK firmware / not elevated / WMI unhappy: stays dark.
        }
        _dead = _instance is null;
    }

    private double? ReadRpm(uint deviceId, ref bool anyAnswered)
    {
        try
        {
            using ManagementBaseObject inParams = _instance!.GetMethodParameters("DSTS");
            inParams["Device_ID"] = deviceId;
            using ManagementBaseObject? outParams = _instance.InvokeMethod("DSTS", inParams, null);
            if (outParams is null || FirstNumeric(outParams) is not { } raw)
                return null;
            // The method answered — that alone proves this is the right vendor, even if
            // the fan is parked (0) or the value is out of range.
            anyAnswered = true;
            return RpmFromStatus(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>DSTS returns a status word; the low 16 bits are the speed in hundreds of
    /// RPM. A parked fan (0) or an implausible value maps to null: a real answer, but not
    /// airflow data, exactly like every other absent reading here.</summary>
    public static double? RpmFromStatus(long status)
    {
        long rpm = (status & 0xFFFF) * 100;
        return rpm is > 0 and < 10000 ? rpm : null;
    }

    /// <summary>The DSTS out-parameter is named differently across ASUS MOF revisions
    /// (Device_Status / Status / ReturnValue). Take whatever integer came back rather than
    /// betting the whole vendor on one property name.</summary>
    private static long? FirstNumeric(ManagementBaseObject outParams)
    {
        foreach (PropertyData p in outParams.Properties)
        {
            if (p.Value is null)
                continue;
            try
            {
                return Convert.ToInt64(p.Value);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                // Not a number (a status string, an array): try the next property.
            }
        }
        return null;
    }

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
        _dead = true;
    }
}

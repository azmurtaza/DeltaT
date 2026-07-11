using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads CPU and GPU fan RPM from Lenovo's gaming firmware WMI interface
/// (<c>root\WMI:LENOVO_FAN_METHOD</c>) — the same channel Lenovo Vantage / Legion
/// Toolkit poll on Legion and LOQ machines. Laptop fans live behind the EC where
/// LibreHardwareMonitor sees nothing, so on these machines this is the only fan
/// telemetry there is, and fan RPM is what lets scoring separate "genuinely cooler"
/// from "fans cranked".
///
/// Strictly read-only: the sole method ever invoked is <c>Fan_GetCurrentFanSpeed</c>,
/// a getter — never <c>Fan_Set_Table</c> or any other Set*, so fan curves and modes
/// are untouched. Protocol (verified against LenovoLegionToolkit's WMI wrappers):
/// input <c>FanID</c> selects the fan (1 = CPU-side, 2 = GPU-side), output
/// <c>CurrentFanSpeed</c> is RPM directly. A machine with a single fan simply errors
/// or returns 0 for FanID 2, which maps to null.
///
/// Self-gating mirrors <see cref="AcerWmiFanReader"/>: the class must exist (Legion/LOQ
/// firmware), the process must be elevated (root\WMI denies standard users), and the
/// method must actually answer. A parked fan answers with a valid 0 and never counts as
/// a failure, so a quiet machine keeps its fan telemetry. Not thread-safe by design:
/// call it from the monitor thread.</summary>
public sealed class LenovoWmiFanReader : ILaptopFanProbe
{
    // Legion/LOQ fan ids. Attribution (which physical fan cools which part) can vary by
    // model, but both feed the same chassis-fan proxy downstream, so a swap is harmless;
    // 1→CPU / 2→GPU is the common convention and what the reason lines assume.
    private const int CpuFanId = 1;
    private const int GpuFanId = 2;

    // A firmware exposing the class but never answering a valid reading is a non-gaming
    // variant — stop asking. Once one valid answer lands (even a parked 0) the reader
    // never self-disables on quiet idle or a transient EC hiccup.
    private const int GiveUpAfterMisses = 30;

    public string Vendor => "Lenovo";
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
        double? cpu = ReadRpm(CpuFanId, ref anyAnswered);
        double? gpu = ReadRpm(GpuFanId, ref anyAnswered);

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
                @"root\WMI", "SELECT * FROM LENOVO_FAN_METHOD");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                _instance = (ManagementObject)mo;
                return;
            }
        }
        catch
        {
            // Not Legion/LOQ firmware / not elevated / WMI unhappy: stays dark.
        }
        _dead = _instance is null;
    }

    private double? ReadRpm(int fanId, ref bool anyAnswered)
    {
        try
        {
            using ManagementBaseObject inParams = _instance!.GetMethodParameters("Fan_GetCurrentFanSpeed");
            inParams["FanID"] = fanId;
            using ManagementBaseObject? outParams = _instance.InvokeMethod("Fan_GetCurrentFanSpeed", inParams, null);
            if (outParams?["CurrentFanSpeed"] is not { } raw)
                return null;
            // The method answered — that alone proves this is the right vendor, even if
            // the fan is parked (0) or the value is out of range.
            anyAnswered = true;
            return PlausibleRpm(Convert.ToInt32(raw));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A parked fan (0) and any out-of-range reading map to null — a real
    /// answer, but not airflow data, exactly like every other absent reading here.</summary>
    public static double? PlausibleRpm(int rpm) => rpm is > 0 and < 10000 ? rpm : null;

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
        _dead = true;
    }
}

using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads CPU and GPU fan RPM from Acer's gaming ACPI-WMI interface
/// (<c>root\wmi:AcerGamingFunction</c>, GUID 7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56) —
/// the same firmware channel NitroSense/PredatorSense poll. Laptop fans live behind
/// the EC where LibreHardwareMonitor can't see them, so on Acer gaming machines
/// (Nitro/Predator) this is the only fan telemetry there is — and fan RPM is what
/// lets scoring tell "genuinely cooler" from "fans cranked" (fan normalization and
/// the dust-vs-paste pattern both feed on it).
///
/// Strictly read-only: the only method ever invoked is the <c>GetGamingSysInfo</c>
/// getter — never a Set*, so fan curves, profiles and LEDs are untouched. Wire
/// protocol (verified against this project's dev machine MOF v2.93 and Linux
/// acer-wmi's predator_v4 support): input u32 packs the sensor id in bits 8–15 with
/// command 0x01 ("get sensor reading") in the low byte; the output u64 carries a
/// status in bits 0–7 (0 = the sensor answered) and the reading in bits 8–23 (RPM
/// for fan sensors). Sensor ids: 0x02 = CPU fan, 0x06 = GPU fan.
///
/// Self-gating: the class must exist (Acer gaming firmware), the process must be
/// elevated (root\wmi denies standard users), and readings must decode as valid —
/// anything else and the reader goes dark, leaving FanRpm null ("--" in the UI,
/// never a faked 0). Not thread-safe by design: call it from the monitor thread,
/// like the LHM session it rides along with.</summary>
public sealed class AcerWmiFanReader : ILaptopFanProbe
{
    public string Vendor => "Acer";

    /// <summary>Ruled out once the gaming class is absent, or after a run of failed
    /// reads without a single valid answer (a non-gaming Acer variant). A parked fan
    /// still answers validly, so a quiet machine never trips this.</summary>
    public bool IsDead => _dead;

    private const uint GetSensorReadingCommand = 0x01;
    private const uint CpuFanSensorId = 0x02;
    private const uint GpuFanSensorId = 0x06;

    // A firmware that exposes the class but never answers a single valid sensor
    // reading is a non-gaming variant — stop asking. Once one valid reading has
    // landed the feature never self-disables: a parked fan still answers with a
    // valid 0 (so quiet idle can't run the miss counter), and transient EC hiccups
    // shouldn't permanently cost the machine its fan telemetry.
    private const int GiveUpAfterMisses = 30;

    private ManagementObject? _instance;
    private bool _initTried;
    private bool _dead;
    private bool _everDecoded;
    private int _consecutiveMisses;

    /// <summary>Current fan RPMs, nulls where nothing trustworthy came back. A live
    /// call is two ~1 ms ACPI method evaluations; a dead reader costs nothing.</summary>
    public LaptopFanSample Read()
    {
        if (_dead)
            return default;
        if (!_initTried)
            Init();
        if (_instance is null)
            return default;

        bool anyValid = false;
        double? cpu = ReadRpm(CpuFanSensorId, ref anyValid);
        double? gpu = ReadRpm(GpuFanSensorId, ref anyValid);

        if (anyValid)
        {
            _everDecoded = true;
            _consecutiveMisses = 0;
        }
        else if (++_consecutiveMisses >= GiveUpAfterMisses && !_everDecoded)
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
                @"root\wmi", "SELECT * FROM AcerGamingFunction");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                // The ACPI device maps to a single instance; keep it for the
                // process lifetime so each poll is just a method invocation.
                _instance = (ManagementObject)mo;
                return;
            }
        }
        catch
        {
            // Not Acer gaming firmware / not elevated / WMI unhappy: stays dark.
        }
        _dead = _instance is null;
    }

    private double? ReadRpm(uint sensorId, ref bool anyValid)
    {
        try
        {
            using ManagementBaseObject inParams = _instance!.GetMethodParameters("GetGamingSysInfo");
            inParams["gmInput"] = (sensorId << 8) | GetSensorReadingCommand;
            using ManagementBaseObject? outParams = _instance.InvokeMethod("GetGamingSysInfo", inParams, null);
            if (outParams?["gmOutput"] is not { } raw)
                return null;
            if (!TryDecode(Convert.ToUInt64(raw), out double? rpm))
                return null;
            anyValid = true;
            return rpm;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pure protocol half, split out for tests. Returns whether the sensor
    /// answered at all (status byte 0); <paramref name="rpm"/> gets the reading from
    /// bits 8–23 when it's airflow data. A valid 0 means "fan parked" (Nitro/Predator
    /// fans stop at cool idle) — a real answer, but not airflow, so it maps to null
    /// exactly like every other absent reading in this codebase.</summary>
    public static bool TryDecode(ulong raw, out double? rpm)
    {
        rpm = null;
        if ((raw & 0xFF) != 0)
            return false;
        ulong value = (raw >> 8) & 0xFFFF;
        if (value is > 0 and < 10000)
            rpm = value;
        return true;
    }

    public void Dispose()
    {
        _instance?.Dispose();
        _instance = null;
        _dead = true;
    }
}

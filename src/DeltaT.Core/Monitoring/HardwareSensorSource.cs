using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace DeltaT.Core.Monitoring;

/// <summary>Real sensors via LibreHardwareMonitor. Sensor names verified against
/// this project's spike dump (docs/PLAN.md, P0): Intel exposes "CPU Package" /
/// "Core Max" / per-core "Distance to TjMax"; NVIDIA exposes "GPU Core" /
/// "GPU Hot Spot"; NVMe exposes "Temperature" + "Percentage Used"; batteries
/// expose "Degradation Level" but rarely temperature. Values get sanity clamps
/// because first reads can glitch (observed: storage temp 0, GPU power 312 W).
///
/// CPU temperature is the HOTTEST of package / core-max / per-core readings —
/// the same number vendor tools (NitroSense, HWiNFO) headline. Taking the first
/// available sensor instead can under-report by several degrees on hybrid dies.
///
/// A watchdog guards against the known LHM failure mode where the kernel-driver
/// session wedges (often when a vendor tool touches the same EC/SMBus) and every
/// sensor silently repeats its last value forever: if the CPU reading is
/// bit-identical for 90 s, or its temperature vanishes after having been present,
/// the whole Computer session is torn down and reopened.</summary>
public sealed class HardwareSensorSource : ISensorSource
{
    private Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private double? _cpuTjMax;

    // Resolved sensor references, so each Read() is direct value access instead
    // of repeated name scans. Keyed by hardware instance: a reopen creates new
    // instances, so stale entries can never be hit (the cache is also cleared).
    private readonly Dictionary<IHardware, CpuSensors> _cpuCache = new();
    private readonly Dictionary<IHardware, GpuSensors> _gpuCache = new();

    // Watchdog state (CPU only: a live CPU never legitimately freezes both its
    // temperature and load to the exact same values; an idle dGPU can).
    private double? _wdTemp, _wdLoad;
    private DateTimeOffset _wdLastChangeUtc;
    private DateTimeOffset _wdLastTempSeenUtc;
    private bool _wdEverSawTemp;
    private DateTimeOffset _lastReopenUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan FrozenReadingLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MissingTempLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReopenCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Self-healing / anomaly notes (sensor stall, reopen attempts) for the app log.</summary>
    public event Action<string>? Diagnostic;

    // Where laptop discrete GPUs visibly start pulling clocks back. NVIDIA
    // doesn't expose the target via LHM, so these are vendor conventions.
    private const double NvidiaGpuLimitC = 87;
    private const double AmdGpuLimitC = 100;
    private const double IntelCpuTjMaxFallback = 100;
    private const double AmdCpuTjMaxFallback = 95;

    public HardwareSensorSource()
    {
        _computer = BuildComputer();
        _computer.Open();
    }

    private static Computer BuildComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsBatteryEnabled = true,
    };

    public SensorSnapshot Read()
    {
        _computer.Accept(_visitor);
        var components = new List<ComponentReading>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (IHardware hw in _computer.Hardware)
        {
            ComponentReading? reading = hw.HardwareType switch
            {
                HardwareType.Cpu => MapCpu(hw),
                HardwareType.GpuNvidia or HardwareType.GpuAmd => MapDiscreteGpu(hw),
                HardwareType.GpuIntel => MapIntelGpu(hw),
                HardwareType.Storage => MapStorage(hw),
                HardwareType.Battery => MapBattery(hw),
                HardwareType.Motherboard => MapMotherboard(hw),
                _ => null,
            };
            if (reading is not null)
                components.Add(reading);
        }

        RunWatchdog(components, now);

        return new SensorSnapshot(now, IsOnAcPower(), components);
    }

    // ---------------------------------------------------------------- watchdog

    private void RunWatchdog(List<ComponentReading> components, DateTimeOffset now)
    {
        ComponentReading? cpu = null;
        foreach (ComponentReading c in components)
        {
            if (c.Kind == ComponentKind.Cpu) { cpu = c; break; }
        }
        if (cpu is null)
            return;

        if (cpu.TemperatureC is { } temp)
        {
            _wdLastTempSeenUtc = now;
            _wdEverSawTemp = true;
            if (temp != _wdTemp || cpu.LoadPercent != _wdLoad)
            {
                _wdTemp = temp;
                _wdLoad = cpu.LoadPercent;
                _wdLastChangeUtc = now;
            }
            else if (now - _wdLastChangeUtc >= FrozenReadingLimit)
            {
                // Temperature AND load bit-identical for this long never happens
                // on a live system — the driver session is repeating stale data.
                Reopen(now, $"CPU reading frozen at {temp:0.0} °C / {cpu.LoadPercent ?? 0:0.0} % for {(now - _wdLastChangeUtc).TotalSeconds:0} s");
            }
        }
        else if (_wdEverSawTemp && now - _wdLastTempSeenUtc >= MissingTempLimit)
        {
            Reopen(now, $"CPU temperature disappeared {(now - _wdLastTempSeenUtc).TotalSeconds:0} s ago");
        }
    }

    private void Reopen(DateTimeOffset now, string why)
    {
        if (now - _lastReopenUtc < ReopenCooldown)
            return;
        _lastReopenUtc = now;
        Diagnostic?.Invoke($"sensor stall detected ({why}) - reinitializing the sensor engine");
        try
        {
            _computer.Close();
        }
        catch { /* the wedged session may refuse to close cleanly */ }
        _cpuCache.Clear();
        _gpuCache.Clear();
        _wdTemp = _wdLoad = null;
        _wdEverSawTemp = false;
        _wdLastChangeUtc = _wdLastTempSeenUtc = now;
        try
        {
            _computer = BuildComputer();
            _computer.Open();
            Diagnostic?.Invoke("sensor engine reinitialized");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"sensor engine reopen failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- mapping

    private sealed class CpuSensors
    {
        public ISensor? Package, CoreMax, CoreAvg, Load, Power;
        public readonly List<ISensor> Cores = new();
        public readonly List<ISensor> Distances = new();
    }

    private CpuSensors ResolveCpu(IHardware hw)
    {
        if (_cpuCache.TryGetValue(hw, out CpuSensors? s))
            return s;
        s = new CpuSensors();
        foreach (ISensor sensor in hw.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Temperature when sensor.Name == "CPU Package":
                    s.Package = sensor;
                    break;
                case SensorType.Temperature when sensor.Name == "Core Max":
                    s.CoreMax = sensor;
                    break;
                case SensorType.Temperature when sensor.Name == "Core Average":
                    s.CoreAvg = sensor;
                    break;
                case SensorType.Temperature when sensor.Name.EndsWith("Distance to TjMax", StringComparison.Ordinal):
                    s.Distances.Add(sensor);
                    break;
                case SensorType.Temperature when sensor.Name.StartsWith("CPU Core #", StringComparison.Ordinal):
                    s.Cores.Add(sensor);
                    break;
                case SensorType.Load when sensor.Name == "CPU Total":
                    s.Load = sensor;
                    break;
                case SensorType.Power when sensor.Name == "CPU Package":
                    s.Power = sensor;
                    break;
            }
        }
        _cpuCache[hw] = s;
        return s;
    }

    private ComponentReading MapCpu(IHardware hw)
    {
        CpuSensors s = ResolveCpu(hw);

        // Hottest of everything the die reports. Package DTS and hottest core
        // disagree by a few degrees on hybrid parts; users compare us against
        // tools that show the max, and the max is what the paste has to survive.
        double? temp = MaxOf(Temp(s.Package), Temp(s.CoreMax));
        foreach (ISensor core in s.Cores)
            temp = MaxOf(temp, Temp(core));
        temp ??= Temp(s.CoreAvg);

        _cpuTjMax ??= DetectTjMax(hw, s);

        // The chip reports how close each core is to its throttle point — the most
        // direct throttling signal we can get without vendor SDKs.
        double? minDistance = null;
        foreach (ISensor d in s.Distances)
        {
            if (d.Value is { } v && (minDistance is not { } m || v < m))
                minDistance = v;
        }
        bool throttling = minDistance is { } dist && dist <= 1;

        return new ComponentReading(
            ComponentKind.Cpu, hw.Name,
            temp, null,
            Percent(s.Load),
            null,
            Watts(s.Power),
            null,
            throttling,
            _cpuTjMax);
    }

    private static double DetectTjMax(IHardware hw, CpuSensors s)
    {
        // TjMax = core temp + its distance-to-TjMax, for any core reporting both.
        foreach (ISensor d in s.Distances)
        {
            string coreName = d.Name[..d.Name.IndexOf(" Distance", StringComparison.Ordinal)];
            ISensor? core = Find(hw, SensorType.Temperature, coreName);
            if (core?.Value is { } t && d.Value is { } dist && t > 1 && t + dist is > 60 and < 120)
                return Math.Round(t + dist);
        }
        return hw.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? AmdCpuTjMaxFallback : IntelCpuTjMaxFallback;
    }

    private sealed class GpuSensors
    {
        public ISensor? Core, HotSpot, Load, Power;
        public readonly List<ISensor> Fans = new();
    }

    private GpuSensors ResolveGpu(IHardware hw)
    {
        if (_gpuCache.TryGetValue(hw, out GpuSensors? s))
            return s;
        s = new GpuSensors
        {
            Core = Find(hw, SensorType.Temperature, "GPU Core"),
            HotSpot = Find(hw, SensorType.Temperature, "GPU Hot Spot"),
            Load = Find(hw, SensorType.Load, "GPU Core"),
            Power = Find(hw, SensorType.Power, "GPU Package") ?? Find(hw, SensorType.Power, "GPU Power"),
        };
        foreach (ISensor sensor in hw.Sensors)
        {
            if (sensor.SensorType == SensorType.Fan)
                s.Fans.Add(sensor);
        }
        _gpuCache[hw] = s;
        return s;
    }

    private ComponentReading MapDiscreteGpu(IHardware hw)
    {
        GpuSensors s = ResolveGpu(hw);
        double limit = hw.HardwareType == HardwareType.GpuAmd ? AmdGpuLimitC : NvidiaGpuLimitC;
        double? temp = Temp(s.Core);
        double? fan = null;
        foreach (ISensor f in s.Fans)
        {
            if (f.Value is { } v && v is > 0 and < 10000)
                fan = MaxOf(fan, v);
        }
        return new ComponentReading(
            ComponentKind.GpuDiscrete, hw.Name,
            temp,
            Temp(s.HotSpot),
            Percent(s.Load),
            fan,
            Watts(s.Power),
            null,
            temp is { } t && t >= limit - 1,
            limit);
    }

    private static ComponentReading MapIntelGpu(IHardware hw) => new(
        ComponentKind.GpuIntegrated, hw.Name,
        MaxTemp(hw, _ => true),
        null,
        Percent(Find(hw, SensorType.Load, "D3D 3D")),
        null,
        Watts(Find(hw, SensorType.Power, "GPU Power")),
        null,
        false,
        null);

    private static ComponentReading MapStorage(IHardware hw) => new(
        ComponentKind.Storage, hw.Name,
        Temp(Find(hw, SensorType.Temperature, "Temperature") ?? FirstOfType(hw, SensorType.Temperature)),
        null,
        Percent(Find(hw, SensorType.Load, "Total Activity")),
        null, null,
        Percent(Find(hw, SensorType.Level, "Percentage Used")),
        false, null);

    private static ComponentReading MapBattery(IHardware hw) => new(
        ComponentKind.Battery, CleanName(hw.Name),
        Temp(FirstOfType(hw, SensorType.Temperature)),
        null, null, null,
        Watts(Find(hw, SensorType.Power, "Charge/Discharge Rate")),
        Percent(Find(hw, SensorType.Level, "Degradation Level")),
        false, null);

    private static ComponentReading? MapMotherboard(IHardware hw)
    {
        // Boards (and their SuperIO sub-hardware) only matter if they expose real
        // temps or fans — many laptops expose neither (this Nitro doesn't).
        var all = new List<ISensor>(hw.Sensors);
        foreach (IHardware sub in hw.SubHardware)
            all.AddRange(sub.Sensors);

        double? temp = null, fan = null;
        foreach (ISensor s in all)
        {
            if (s.SensorType == SensorType.Temperature)
                temp = MaxOf(temp, Temp(s));
            else if (s.SensorType == SensorType.Fan && s.Value is { } v && v is > 0 and < 10000)
                fan = MaxOf(fan, v);
        }
        if (temp is null && fan is null)
            return null;

        return new ComponentReading(ComponentKind.Motherboard, hw.Name, temp, null, null, fan, null, null, false, null);
    }

    // ------------------------------------------------------------- helpers

    // Some firmware pads names with NUL bytes (observed: battery "AP21D8M\0").
    private static string CleanName(string name) => name.Replace("\0", "").Trim();

    private static ISensor? Find(IHardware hw, SensorType type, string name)
    {
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType == type && s.Name == name)
                return s;
        }
        return null;
    }

    private static ISensor? FirstOfType(IHardware hw, SensorType type)
    {
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType == type)
                return s;
        }
        return null;
    }

    private static double? MaxTemp(IHardware hw, Func<ISensor, bool> filter)
    {
        double? max = null;
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Temperature && filter(s))
                max = MaxOf(max, Temp(s));
        }
        return max;
    }

    private static double? MaxOf(double? a, double? b) =>
        a is { } x ? (b is { } y && y > x ? y : x) : b;

    private static double? Temp(ISensor? s) => s?.Value is { } v && v is > 1 and < 119 ? Math.Round(v, 1) : null;

    private static double? Percent(ISensor? s) => s?.Value is { } v && v is >= 0 and <= 100 ? Math.Round(v, 1) : null;

    private static double? Watts(ISensor? s) => s?.Value is { } v && v is >= 0 and < 500 ? Math.Round(v, 1) : null;

    private static bool IsOnAcPower()
    {
        return GetSystemPowerStatus(out SystemPowerStatus status) && status.ACLineStatus != 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus; // 0 battery, 1 AC, 255 unknown (treated as AC)
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public void Dispose() => _computer.Close();

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}

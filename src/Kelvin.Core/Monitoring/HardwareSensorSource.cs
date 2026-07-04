using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Kelvin.Core.Monitoring;

/// <summary>Real sensors via LibreHardwareMonitor. Sensor names verified against
/// this project's spike dump (docs/PLAN.md, P0): Intel exposes "CPU Package" /
/// "Core Max" / per-core "Distance to TjMax"; NVIDIA exposes "GPU Core" /
/// "GPU Hot Spot"; NVMe exposes "Temperature" + "Percentage Used"; batteries
/// expose "Degradation Level" but rarely temperature. Values get sanity clamps
/// because first reads can glitch (observed: storage temp 0, GPU power 312 W).</summary>
public sealed class HardwareSensorSource : ISensorSource
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private double? _cpuTjMax;

    // Where laptop discrete GPUs visibly start pulling clocks back. NVIDIA
    // doesn't expose the target via LHM, so these are vendor conventions.
    private const double NvidiaGpuLimitC = 87;
    private const double AmdGpuLimitC = 100;
    private const double IntelCpuTjMaxFallback = 100;
    private const double AmdCpuTjMaxFallback = 95;

    public HardwareSensorSource()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
        };
        _computer.Open();
    }

    public SensorSnapshot Read()
    {
        _computer.Accept(_visitor);
        var components = new List<ComponentReading>();

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

        return new SensorSnapshot(DateTimeOffset.UtcNow, IsOnAcPower(), components);
    }

    private ComponentReading MapCpu(IHardware hw)
    {
        double? temp = Temp(Find(hw, SensorType.Temperature, "CPU Package"))
                    ?? Temp(Find(hw, SensorType.Temperature, "Core Max"))
                    ?? Temp(Find(hw, SensorType.Temperature, "Core Average"))
                    ?? MaxTemp(hw, s => s.Name.StartsWith("CPU Core #", StringComparison.Ordinal) && !s.Name.Contains("TjMax"));

        _cpuTjMax ??= DetectTjMax(hw);

        // The chip reports how close each core is to its throttle point — the most
        // direct throttling signal we can get without vendor SDKs.
        double? minDistance = hw.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name.EndsWith("Distance to TjMax", StringComparison.Ordinal))
            .Select(s => (double?)s.Value)
            .Where(v => v.HasValue)
            .Min();
        bool throttling = minDistance is { } d && d <= 1;

        return new ComponentReading(
            ComponentKind.Cpu, hw.Name,
            temp, null,
            Percent(Find(hw, SensorType.Load, "CPU Total")),
            null,
            Watts(Find(hw, SensorType.Power, "CPU Package")),
            null,
            throttling,
            _cpuTjMax);
    }

    private double DetectTjMax(IHardware hw)
    {
        // TjMax = core temp + its distance-to-TjMax, for any core reporting both.
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || !s.Name.EndsWith("Distance to TjMax", StringComparison.Ordinal))
                continue;
            string coreName = s.Name[..s.Name.IndexOf(" Distance", StringComparison.Ordinal)];
            ISensor? core = Find(hw, SensorType.Temperature, coreName);
            if (core?.Value is { } t && s.Value is { } dist && t > 1 && t + dist is > 60 and < 120)
                return Math.Round(t + dist);
        }
        return hw.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? AmdCpuTjMaxFallback : IntelCpuTjMaxFallback;
    }

    private static ComponentReading MapDiscreteGpu(IHardware hw)
    {
        double limit = hw.HardwareType == HardwareType.GpuAmd ? AmdGpuLimitC : NvidiaGpuLimitC;
        double? temp = Temp(Find(hw, SensorType.Temperature, "GPU Core"));
        return new ComponentReading(
            ComponentKind.GpuDiscrete, hw.Name,
            temp,
            Temp(Find(hw, SensorType.Temperature, "GPU Hot Spot")),
            Percent(Find(hw, SensorType.Load, "GPU Core")),
            MaxFan(hw),
            Watts(Find(hw, SensorType.Power, "GPU Package") ?? Find(hw, SensorType.Power, "GPU Power")),
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
        ComponentKind.Battery, hw.Name.Trim(),
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

        double? temp = all.Where(s => s.SensorType == SensorType.Temperature)
                          .Select(s => Temp(s)).Where(v => v.HasValue).Max();
        double? fan = all.Where(s => s.SensorType == SensorType.Fan)
                         .Select(s => (double?)s.Value).Where(v => v is > 0 and < 10000).Max();
        if (temp is null && fan is null)
            return null;

        return new ComponentReading(ComponentKind.Motherboard, hw.Name, temp, null, null, fan, null, null, false, null);
    }

    // ------------------------------------------------------------- helpers

    private static ISensor? Find(IHardware hw, SensorType type, string name) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name);

    private static ISensor? FirstOfType(IHardware hw, SensorType type) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type);

    private static double? MaxTemp(IHardware hw, Func<ISensor, bool> filter) =>
        hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && filter(s))
                  .Select(s => Temp(s)).Where(v => v.HasValue).Max();

    private static double? MaxFan(IHardware hw) =>
        hw.Sensors.Where(s => s.SensorType == SensorType.Fan)
                  .Select(s => (double?)s.Value).Where(v => v is > 0 and < 10000).Max();

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

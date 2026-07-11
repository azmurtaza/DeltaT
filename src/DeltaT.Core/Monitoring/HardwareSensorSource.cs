using System.Runtime.InteropServices;
using System.Security.Principal;
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
/// the whole Computer session is torn down and reopened.
///
/// Laptop fans sit behind the EC where LHM sees nothing; on supported gaming machines
/// (Acer Nitro/Predator, Lenovo Legion/LOQ) a read-only vendor WMI interface supplies
/// CPU/GPU fan RPM instead (<see cref="LaptopFanReader"/>) — real laptop airflow data
/// for fan normalization at last.</summary>
public sealed class HardwareSensorSource : ISensorSource
{
    private Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private double? _cpuTjMax;
    private readonly BatteryCycleReader _batteryCycles = new();
    private readonly AcpiThermalZoneReader _acpiZone = new();
    private readonly CpuMsrTemperatureReader _msrReader = new();
    private readonly LaptopFanReader _laptopFans = new();
    private bool _cpuTempEverMissingLogged;
    private bool _acpiFallbackLogged;
    private bool _msrFallbackLogged;
    private bool _laptopFansLogged;

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

    // Cold-start recovery: the WinRing0 kernel driver can fail to initialise at
    // startup (a vendor tool or the just-replaced instance still holding it after an
    // auto-update, a boot-time race), which leaves *every* CPU temperature null for
    // this whole process while a fresh start would have worked. If a CPU is present
    // but never yields a temperature, rebuild the session a few times to reload the
    // driver — then stop, so a genuinely non-elevated run (temps truly unavailable)
    // doesn't reopen forever.
    private DateTimeOffset _wdColdStartSince;
    private int _coldStartReopens;
    private const int MaxColdStartReopens = 4;
    private static readonly TimeSpan ColdStartRetry = TimeSpan.FromSeconds(25);

    // Reloading the driver only helps if we actually have the rights to read CPU MSRs;
    // a non-elevated run will never get a temperature no matter how many times we retry,
    // so don't churn the session for it.
    private readonly bool _isElevated = ProcessIsElevated();

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
        _wdColdStartSince = DateTimeOffset.UtcNow;
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

        // Laptop EC fans are invisible to LHM; a supported vendor's gaming WMI answers.
        LaptopFanSample ecFans = _laptopFans.Read();
        if (!_laptopFansLogged && ecFans.HasAny)
        {
            _laptopFansLogged = true;
            Diagnostic?.Invoke($"fan telemetry active via {_laptopFans.ActiveVendor} gaming WMI (CPU {FmtRpm(ecFans.CpuRpm)}, GPU {FmtRpm(ecFans.GpuRpm)})");
        }

        foreach (IHardware hw in _computer.Hardware)
        {
            ComponentReading? reading = hw.HardwareType switch
            {
                HardwareType.Cpu => MapCpu(hw, ecFans.CpuRpm),
                HardwareType.GpuNvidia or HardwareType.GpuAmd => MapDiscreteGpu(hw, ecFans.GpuRpm),
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
        else if (_wdEverSawTemp)
        {
            if (now - _wdLastTempSeenUtc >= MissingTempLimit)
                Reopen(now, $"CPU temperature disappeared {(now - _wdLastTempSeenUtc).TotalSeconds:0} s ago");
        }
        else if (_isElevated && _coldStartReopens < MaxColdStartReopens && now - _wdColdStartSince >= ColdStartRetry)
        {
            // A CPU is present but has never once reported a temperature — most likely
            // the kernel driver didn't come up. Reload the session to try again.
            _coldStartReopens++;
            Reopen(now, "CPU temperature never appeared - reloading the sensor driver (it may have failed to initialise at startup)", bypassCooldown: true);
        }
    }

    private void Reopen(DateTimeOffset now, string why, bool bypassCooldown = false)
    {
        if (!bypassCooldown && now - _lastReopenUtc < ReopenCooldown)
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
        _wdLastChangeUtc = _wdLastTempSeenUtc = _wdColdStartSince = now;
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
        public ISensor? Package, CoreMax, CoreAvg, Tctl, Load, Power, PowerCores;
        public readonly List<ISensor> Cores = new();
        public readonly List<ISensor> Distances = new();
        // Every temperature sensor on the package except the distance-to-TjMax
        // ones (which are gaps, not absolute temps): the vendor-agnostic safety net.
        public readonly List<ISensor> AllTemps = new();
    }

    private CpuSensors ResolveCpu(IHardware hw)
    {
        // Re-resolve if a cached entry captured no temperature sensors at all: some LHM
        // drivers populate the CPU's sensor list lazily over the first few updates, so a
        // set resolved too early can be permanently empty. Rescanning until at least one
        // temperature sensor appears makes cold-start detection reliable across vendors.
        if (_cpuCache.TryGetValue(hw, out CpuSensors? s) && s.AllTemps.Count > 0)
            return s;
        s = new CpuSensors();
        foreach (ISensor sensor in hw.Sensors)
        {
            switch (sensor.SensorType)
            {
                // Intel exposes these; AMD does not.
                case SensorType.Temperature when sensor.Name == "CPU Package":
                    s.Package = sensor;
                    break;
                case SensorType.Temperature when sensor.Name == "Core Max":
                    s.CoreMax = sensor;
                    break;
                case SensorType.Temperature when sensor.Name == "Core Average":
                    s.CoreAvg = sensor;
                    break;
                // AMD Ryzen's primary die temperature (e.g. "Core (Tctl/Tdie)",
                // "Core (Tdie)"). Without this, AMD laptops report no CPU sensor at all.
                case SensorType.Temperature when sensor.Name.StartsWith("Core (Tctl", StringComparison.Ordinal)
                                              || sensor.Name.StartsWith("Core (Tdie", StringComparison.Ordinal):
                    s.Tctl = sensor;
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
                // Intel: "CPU Package"; AMD: "Package"; either may also expose "CPU Cores".
                case SensorType.Power when sensor.Name is "CPU Package" or "Package":
                    s.Power = sensor;
                    break;
                case SensorType.Power when sensor.Name == "CPU Cores":
                    s.PowerCores = sensor;
                    break;
            }

            if (sensor.SensorType == SensorType.Temperature
                && !sensor.Name.EndsWith("Distance to TjMax", StringComparison.Ordinal))
                s.AllTemps.Add(sensor);
        }
        _cpuCache[hw] = s;
        return s;
    }

    private ComponentReading MapCpu(IHardware hw, double? ecFanRpm)
    {
        CpuSensors s = ResolveCpu(hw);

        // Hottest of everything the die reports. Package DTS and hottest core
        // disagree by a few degrees on hybrid parts; users compare us against
        // tools that show the max, and the max is what the paste has to survive.
        // Tctl/Tdie is the AMD equivalent of package temperature.
        double? temp = MaxOf(Temp(s.Package), Temp(s.CoreMax));
        foreach (ISensor core in s.Cores)
            temp = MaxOf(temp, Temp(core));
        temp = MaxOf(temp, Temp(s.Tctl));
        temp ??= Temp(s.CoreAvg);

        // Vendor-agnostic safety net: if no sensor we recognise by name produced a
        // reading (an unfamiliar AMD APU, a future part), fall back to the hottest of
        // whatever temperature sensors the package does expose. As long as the chip
        // surfaces any core temperature at all, DeltaT reads it.
        if (temp is null)
        {
            foreach (ISensor t in s.AllTemps)
                temp = MaxOf(temp, Temp(t));
        }

        // A CPU newer than the pinned LHM build (Arrow/Lunar/Panther Lake, post-Zen-5)
        // exposes NO sensors above — read the die's architectural thermal registers
        // directly instead. Full sampling rate and 1 °C DTS precision, unlike the ACPI
        // zone below (EC-paced: 10–20 s stale, coarse) which stays as the true last
        // resort. Also yields the real TjMax and live throttle state.
        bool msrThrottling = false;
        if (temp is null && _msrReader.TryRead(hw.Name) is { } msr)
        {
            temp = msr.TemperatureC;
            _cpuTjMax ??= msr.TjMaxC;
            msrThrottling = msr.Throttling;
            if (!_msrFallbackLogged)
            {
                _msrFallbackLogged = true;
                Diagnostic?.Invoke($"reading the CPU's thermal registers directly ({temp:0.#} °C) - this CPU model is newer than the bundled sensor library");
            }
        }

        // On many desktops the CPU's MSR temperatures need the kernel driver (admin),
        // but the board's SuperIO chip exposes a "CPU"/"CPU Socket" temperature that
        // reads without it. Borrow that so a non-elevated or unusual machine still
        // shows a CPU temperature instead of a blank card.
        temp ??= FallbackCpuTempFromBoard();

        // Truly-last resort: the ACPI thermal zone. Not the exact die, but real and
        // roughly CPU-tracking, and available on almost any Windows machine when
        // elevated. Only consulted when everything above came up empty.
        if (temp is null && _acpiZone.CurrentCelsius is { } zone)
        {
            temp = zone;
            if (!_acpiFallbackLogged)
            {
                _acpiFallbackLogged = true;
                Diagnostic?.Invoke($"using ACPI thermal-zone temperature ({zone:0.#} °C) as a CPU fallback - LibreHardwareMonitor couldn't read this CPU directly");
            }
        }

        if (temp is null && !_cpuTempEverMissingLogged)
        {
            _cpuTempEverMissingLogged = true;
            string names = string.Join(", ", hw.Sensors
                .Where(x => x.SensorType == SensorType.Temperature)
                .Select(x => x.Name));
            Diagnostic?.Invoke($"no CPU temperature from '{hw.Name}' (admin needed for package temps?). Temp sensors seen: [{names}]");
        }

        _cpuTjMax ??= DetectTjMax(hw, s);

        // The chip reports how close each core is to its throttle point — the most
        // direct throttling signal we can get without vendor SDKs.
        double? minDistance = null;
        foreach (ISensor d in s.Distances)
        {
            if (d.Value is { } v && (minDistance is not { } m || v < m))
                minDistance = v;
        }
        bool throttling = minDistance is { } dist && dist <= 1 || msrThrottling;

        return new ComponentReading(
            ComponentKind.Cpu, hw.Name,
            temp, null,
            Percent(s.Load),
            ecFanRpm,
            Watts(s.Power ?? s.PowerCores),
            null,
            throttling,
            _cpuTjMax);
    }

    /// <summary>Hottest board/SuperIO temperature whose name looks like a CPU sensor
    /// ("CPU", "CPU Socket", "CPU Core"…). Used only when the CPU hardware itself gives
    /// no temperature, so an accurate MSR reading always wins when it's available.</summary>
    private double? FallbackCpuTempFromBoard()
    {
        double? best = null;
        foreach (IHardware hw in _computer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Motherboard)
                continue;
            best = MaxOf(best, CpuNamedTemp(hw));
            foreach (IHardware sub in hw.SubHardware)
                best = MaxOf(best, CpuNamedTemp(sub));
        }
        return best;
    }

    private static double? CpuNamedTemp(IHardware hw)
    {
        double? best = null;
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Temperature
                && s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                best = MaxOf(best, Temp(s));
        }
        return best;
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

    private ComponentReading MapDiscreteGpu(IHardware hw, double? ecFanRpm)
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
        fan ??= ecFanRpm; // laptop dGPU fans live behind the EC — LHM's list is empty there
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

    private ComponentReading MapBattery(IHardware hw) => new(
        ComponentKind.Battery, CleanName(hw.Name),
        Temp(FirstOfType(hw, SensorType.Temperature)),
        null, null, null,
        Watts(Find(hw, SensorType.Power, "Charge/Discharge Rate")),
        Percent(Find(hw, SensorType.Level, "Degradation Level")),
        false, null,
        BatteryCycles: _batteryCycles.CurrentCycles);

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

    private static string FmtRpm(double? rpm) => rpm is { } v ? $"{v:0} rpm" : "--";

    private static double? MaxOf(double? a, double? b) =>
        a is { } x ? (b is { } y && y > x ? y : x) : b;

    private static double? Temp(ISensor? s) => s?.Value is { } v && v is > 1 and < 119 ? Math.Round(v, 1) : null;

    // Loads occasionally read a hair over 100 (driver rounding); clamp those instead of
    // discarding them — a discarded load reading would drop the whole minute from paste
    // telemetry. Beyond 105 it's a glitch, not rounding.
    private static double? Percent(ISensor? s) => s?.Value is { } v && v is >= 0 and <= 105 ? Math.Min(100, Math.Round(v, 1)) : null;

    private static double? Watts(ISensor? s) => s?.Value is { } v && v is >= 0 and < 500 ? Math.Round(v, 1) : null;

    private static bool ProcessIsElevated()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

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

    public void Dispose()
    {
        _batteryCycles.Dispose();
        _acpiZone.Dispose();
        _laptopFans.Dispose();
        _computer.Close();
    }

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

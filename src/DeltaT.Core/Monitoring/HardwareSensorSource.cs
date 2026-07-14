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
    private readonly NvmlGpuReader _nvml = new();
    private readonly CpuLoadReader _cpuLoad = new();
    private bool _nvmlLogged;
    /// <summary>True while the CPU's temperature is coming from the thermal registers
    /// directly, which lets LHM's expensive CPU update drop to a slow clock (power only).</summary>
    private bool _cpuServedByMsr;

    /// <summary>LHM's name for the discrete NVIDIA card, so NVML can bind to the same
    /// device (a multi-GPU desktop must not read one card's watts against another's
    /// temperature). Null on machines with no NVIDIA card, which keeps NVML dark.</summary>
    private string? NvidiaName
    {
        get
        {
            foreach (IHardware hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.GpuNvidia)
                    return hw.Name;
            }
            return null;
        }
    }
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
        // NVML first: when it is live it serves the discrete NVIDIA card's fast-moving
        // values (~0.19 ms) and the visitor below can leave LHM's 61 ms NVIDIA update on a
        // slow clock, since all LHM is still needed for there is hotspot and fan RPM.
        NvmlSample nv = _nvml.Read(NvidiaName);
        _visitor.NvidiaServedByNvml = _nvml.IsLive;
        // Set from the previous tick's outcome (MapCpu runs after the visitor). The first
        // tick therefore refreshes LHM's CPU fully, which is what we want anyway: it is what
        // the fallback path would need if the MSR read turns out not to work here.
        _visitor.CpuServedByMsr = _cpuServedByMsr;
        if (_nvml.IsLive && !_nvmlLogged)
        {
            _nvmlLogged = true;
            Diagnostic?.Invoke($"GPU telemetry via NVML ({_nvml.DeviceName}) - LibreHardwareMonitor's slow NVIDIA path is now polled only for hotspot and fan");
        }

        _computer.Accept(_visitor);
        var components = new List<ComponentReading>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Laptop EC fans are invisible to LHM; a supported vendor's gaming WMI answers.
        // The WMI round trip costs ~19 ms, and a fan takes seconds to change speed, so it is
        // polled on its own clock and the reading carried between polls.
        LaptopFanSample ecFans = ReadFansCached(now: DateTimeOffset.UtcNow);
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
                // NVML's sample only applies to the NVIDIA card; an AMD card is read from
                // LHM exactly as before.
                HardwareType.GpuNvidia => MapDiscreteGpu(hw, ecFans.GpuRpm, nv),
                HardwareType.GpuAmd => MapDiscreteGpu(hw, ecFans.GpuRpm, default),
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
        _visitor.Reset();
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

        // Fast path, and on an elevated run this is the normal path: read the die's
        // architectural thermal registers ourselves (~1 ms) instead of letting LHM do it
        // (~122 ms, because it walks every logical processor switching thread affinity to
        // read each one's MSRs). It is the SAME register - verified side by side on the dev
        // machine, the two agree sample for sample - so nothing about the recorded numbers
        // changes, only what they cost. When it answers, the visitor drops LHM's CPU update
        // to a slow clock, where it is still needed for package power.
        //
        // This also remains the ONLY source on CPUs newer than the pinned LHM build
        // (Arrow/Lunar/Panther Lake, post-Zen-5), which expose no LHM sensors at all.
        bool msrThrottling = false;
        double? temp = null;
        if (_msrReader.TryRead(hw.Name) is { } msr)
        {
            temp = msr.TemperatureC;
            _cpuTjMax ??= msr.TjMaxC;
            msrThrottling = msr.Throttling;
            _cpuServedByMsr = true;
        }
        else
        {
            _cpuServedByMsr = false;
        }

        // Hottest of everything the die reports. Package DTS and hottest core
        // disagree by a few degrees on hybrid parts; users compare us against
        // tools that show the max, and the max is what the paste has to survive.
        // Tctl/Tdie is the AMD equivalent of package temperature.
        if (temp is null)
        {
            temp = MaxOf(Temp(s.Package), Temp(s.CoreMax));
            foreach (ISensor core in s.Cores)
                temp = MaxOf(temp, Temp(core));
            temp = MaxOf(temp, Temp(s.Tctl));
            temp ??= Temp(s.CoreAvg);
        }

        // Vendor-agnostic safety net: if no sensor we recognise by name produced a
        // reading (an unfamiliar AMD APU, a future part), fall back to the hottest of
        // whatever temperature sensors the package does expose. As long as the chip
        // surfaces any core temperature at all, DeltaT reads it.
        if (temp is null)
        {
            foreach (ISensor t in s.AllTemps)
                temp = MaxOf(temp, Temp(t));
        }

        if (_cpuServedByMsr && !_msrFallbackLogged)
        {
            _msrFallbackLogged = true;
            Diagnostic?.Invoke($"CPU temperature read straight from the thermal registers ({temp:0.#} °C) - LibreHardwareMonitor's slow CPU path is now polled only for package power");
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
            // Load from the Windows scheduler's own counters (microseconds) when the MSR
            // path is carrying this CPU, since LHM's copy of the same number is only
            // refreshed on the slow clock then. Same quantity, same meaning.
            (_cpuServedByMsr ? _cpuLoad.Read() : null) ?? Percent(s.Load),
            ecFanRpm,
            // Package power first (Intel "CPU Package", AMD "Package"); cores-only power is
            // the fallback when the package rail exists but never reports. This is the one
            // CPU value only LHM has, and the reason its update still runs at all.
            Watts(s.Power) ?? Watts(s.PowerCores),
            null,
            throttling,
            _cpuTjMax);
    }

    private static readonly TimeSpan FanPollInterval = TimeSpan.FromSeconds(6);
    private DateTimeOffset _lastFanPoll = DateTimeOffset.MinValue;
    private LaptopFanSample _lastFanSample;

    /// <summary>The vendor fan WMI at its own cadence. A fan ramps over seconds, so a 6 s
    /// poll loses nothing, while polling it every tick cost ~19 ms of the sensor thread each
    /// time (it is a WMI method call into the EC).</summary>
    private LaptopFanSample ReadFansCached(DateTimeOffset now)
    {
        if (now - _lastFanPoll < FanPollInterval)
            return _lastFanSample;
        _lastFanPoll = now;
        _lastFanSample = _laptopFans.Read();
        return _lastFanSample;
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
        public ISensor? Core, HotSpot, Load;
        /// <summary>Every power sensor the card exposes, best first. Read in order and the
        /// first with a live value wins.</summary>
        public readonly List<ISensor> Power = new();
        public readonly List<ISensor> Fans = new();
    }

    /// <summary>Power sensor names in preference order, across vendors. NVIDIA reports
    /// "GPU Package"; AMD reports "GPU Package" but leaves it null on many cards, where
    /// "GPU PPT" (package power tracking) or "GPU Core" is the live one; Intel Arc reports
    /// "GPU Package"/"GPU Total"; Intel integrated reports "GPU Power". Whole-package
    /// figures come first because thermal resistance is about everything the die
    /// dissipates, but any of these is a valid basis: scoring compares a machine against
    /// its own baseline, so what matters is that the SAME sensor is read every time on a
    /// given card, which an ordered list guarantees.</summary>
    private static readonly string[] GpuPowerNames =
        { "GPU Package", "GPU PPT", "GPU Total", "GPU Power", "GPU Core", "GPU SoC" };

    private GpuSensors ResolveGpu(IHardware hw)
    {
        if (_gpuCache.TryGetValue(hw, out GpuSensors? s))
            return s;
        s = new GpuSensors
        {
            Core = Find(hw, SensorType.Temperature, "GPU Core"),
            HotSpot = Find(hw, SensorType.Temperature, "GPU Hot Spot"),
            Load = Find(hw, SensorType.Load, "GPU Core"),
        };
        foreach (string name in GpuPowerNames)
        {
            if (Find(hw, SensorType.Power, name) is { } p)
                s.Power.Add(p);
        }
        // Vendor-agnostic safety net, same as the CPU's: an unfamiliar card (a future
        // Arc, an AMD APU naming its rail something new) still yields watts as long as it
        // exposes any power sensor at all, rather than silently dropping to raw-ΔT scoring.
        foreach (ISensor sensor in hw.Sensors)
        {
            if (sensor.SensorType == SensorType.Power && !s.Power.Contains(sensor))
                s.Power.Add(sensor);
            if (sensor.SensorType == SensorType.Fan)
                s.Fans.Add(sensor);
        }
        _gpuCache[hw] = s;
        return s;
    }

    /// <summary>First power sensor that is actually reporting. A card can expose a rail
    /// and never populate it (AMD's "GPU Package" on many laptop dies), so presence of the
    /// sensor is not proof of a reading: fall through until one answers.</summary>
    private static double? FirstWatts(List<ISensor> candidates)
    {
        foreach (ISensor s in candidates)
        {
            if (Watts(s) is { } w)
                return w;
        }
        return null;
    }

    /// <param name="nv">NVML's reading of this card, when NVML is live. It wins per value,
    /// because it is both far cheaper to obtain and fresher: LHM's rows are only refreshed
    /// every 30 s once NVML is carrying the fast signals. Each value still falls back to LHM
    /// independently, so a driver that answers for temperature but not power degrades one
    /// reading rather than the whole card.</param>
    private ComponentReading MapDiscreteGpu(IHardware hw, double? ecFanRpm, NvmlSample nv)
    {
        GpuSensors s = ResolveGpu(hw);
        double limit = hw.HardwareType == HardwareType.GpuAmd ? AmdGpuLimitC : NvidiaGpuLimitC;
        double? temp = nv.TemperatureC ?? Temp(s.Core);
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
            // Hotspot stays LHM's: NVML reports the hotspot field unsupported on this driver.
            // It moves slowly (it is a gap against the edge sensor), so a 30 s refresh is fine.
            Temp(s.HotSpot),
            nv.LoadPercent ?? Percent(s.Load),
            fan,
            nv.PowerW ?? FirstWatts(s.Power),
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
        _nvml.Dispose();
        _computer.Close();
    }

    /// <summary>Refreshes each hardware item only as often as its data can actually
    /// change. LHM's Update() cost is wildly uneven: on the dev machine the CPU costs
    /// ~2 ms but the NVIDIA GPU costs ~61 ms, because LHM's NVIDIA path also pulls
    /// clocks, PCIe throughput, D3D engine counters and memory stats DeltaT never reads.
    /// Updating everything at the sampling interval burned ~3% of a core around the
    /// clock and put a 61 ms driver call in front of every game frame (the frame-stutter
    /// reports). Sensors keep their last value between refreshes, so a skipped Update
    /// simply carries the previous reading forward.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        private readonly Dictionary<IHardware, DateTimeOffset> _lastUpdate = new();

        /// <summary>Zero = every tick. The rest are paced by how fast the reading can
        /// move: a GPU die swings in seconds, SMART wear and battery temperature do not
        /// (and each SMART read is a command sent to the drive).</summary>
        /// <summary>Set when NVML is carrying the NVIDIA card's temperature, load and power.
        /// LHM is then only needed for hotspot and fan RPM, both of which move slowly, so its
        /// expensive NVIDIA update drops from every 6 s to every 30 s.</summary>
        public bool NvidiaServedByNvml { get; set; }

        /// <summary>Set when the CPU's temperature and load are coming from the thermal
        /// registers and the Windows scheduler instead of LHM. Elevated, LHM's CPU update
        /// costs ~122 ms per tick (an affinity switch and MSR read per logical processor).
        /// All it is still needed for then is package power, which moves slowly enough for a
        /// 30 s clock, so this is where most of the app's CPU cost disappears.</summary>
        public bool CpuServedByMsr { get; set; }

        private TimeSpan CadenceFor(HardwareType type) => type switch
        {
            HardwareType.Cpu => CpuServedByMsr ? TimeSpan.FromSeconds(30) : TimeSpan.Zero,
            HardwareType.GpuNvidia => NvidiaServedByNvml ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(6),
            HardwareType.GpuAmd or HardwareType.GpuIntel => TimeSpan.FromSeconds(6),
            HardwareType.Storage => TimeSpan.FromSeconds(60),
            HardwareType.Battery => TimeSpan.FromSeconds(30),
            // Everything else (CPU, motherboard/SuperIO and its fan tachometers) measured
            // sub-millisecond, and carries the fast-moving signals. Poll it every tick.
            _ => TimeSpan.Zero,
        };

        /// <summary>Forget pacing state (call when the LHM session is reopened: the old
        /// hardware instances are gone and the new ones must all refresh immediately).</summary>
        public void Reset() => _lastUpdate.Clear();

        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan cadence = CadenceFor(hardware.HardwareType);
            bool due = cadence == TimeSpan.Zero
                || !_lastUpdate.TryGetValue(hardware, out DateTimeOffset last)
                || now - last >= cadence;

            if (due)
            {
                hardware.Update();
                _lastUpdate[hardware] = now;
            }

            // Sub-hardware inherits the parent's cadence: it is the same physical part
            // (a CPU's cores, a GPU's memory), so refreshing it on a different clock
            // would hand out a snapshot whose halves disagree about when they were read.
            if (due)
            {
                foreach (IHardware sub in hardware.SubHardware)
                    sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}

namespace DeltaT.Core.Monitoring;

public enum SimScenario
{
    Healthy,
    AgingPaste,
    DegradedPaste,
    DustyAirflow,
}

/// <summary>Fake laptop for dev, demo and tests. Generates a plausible workload
/// (idle → bursts → sustained load) and first-order thermal responses. Degraded
/// paste shows up exactly the way the scoring engine hunts for it: bigger deltas
/// under load, fast heat-soak, throttle kisses. Dusty airflow shows elevated
/// steady-state everywhere but a normal soak rate.</summary>
public sealed class SimulatedSensorSource : ISensorSource
{
    private readonly Random _rng;
    private readonly Func<double> _ambient;
    private readonly TimeSpan _step;
    private readonly bool _warmDemo;
    private DateTimeOffset _now;

    private double _cpuTemp;
    private double _gpuTemp;
    private double _ssdTemp;
    private double _cpuLoad;
    private double _gpuLoad;
    private double _cpuFanRpm;
    private double _gpuFanRpm;
    private int _phaseSecondsLeft;
    private Phase _phase = Phase.Idle;

    private const double CpuLimit = 100;
    private const double GpuLimit = 87;

    public SimScenario Scenario { get; set; }

    public SimulatedSensorSource(
        SimScenario scenario = SimScenario.Healthy,
        double? fixedAmbientC = null,
        Func<double>? ambient = null,
        DateTimeOffset? startUtc = null,
        TimeSpan? step = null,
        int seed = 1337,
        bool warmDemo = false)
    {
        Scenario = scenario;
        _ambient = ambient ?? (() => fixedAmbientC ?? 32.0);
        _now = startUtc ?? DateTimeOffset.UtcNow;
        _step = step ?? TimeSpan.FromSeconds(2);
        _rng = new Random(seed);
        _warmDemo = warmDemo;
        double a = _ambient();
        // Screenshot mode: start already deep in a gaming session so the very first
        // captured frame reads as a machine under load, not a cold idle boot.
        if (warmDemo)
        {
            _phase = Phase.Gaming;
            _phaseSecondsLeft = int.MaxValue;
            _cpuLoad = 68;
            _gpuLoad = 95;
            _cpuTemp = a + 46;
            _gpuTemp = a + 44;
            _ssdTemp = a + 24;
            _cpuFanRpm = 4100;
            _gpuFanRpm = 4300;
        }
        else
        {
            _cpuTemp = a + 18;
            _gpuTemp = a + 12;
            _ssdTemp = a + 20;
        }
    }

    private enum Phase { Idle, Light, Gaming, Burst }

    public SensorSnapshot Read()
    {
        _now += _step;
        double dt = _step.TotalSeconds;
        double ambient = _ambient();

        AdvanceWorkload(dt);

        // First-order approach to a load-dependent target. Bad paste = tiny thermal
        // coupling, so the die reaches a hotter target much faster (small tau).
        double cpuTau = Scenario == SimScenario.DegradedPaste ? 12 : Scenario == SimScenario.AgingPaste ? 25 : 40;
        double gpuTau = Scenario == SimScenario.DegradedPaste ? 15 : Scenario == SimScenario.AgingPaste ? 28 : 45;

        _cpuTemp = Approach(_cpuTemp, ambient + CpuDelta(_cpuLoad), cpuTau, dt) + Noise(0.5);
        _gpuTemp = Approach(_gpuTemp, ambient + GpuDelta(_gpuLoad), gpuTau, dt) + Noise(0.4);
        _ssdTemp = Approach(_ssdTemp, ambient + 20 + _cpuLoad * 0.10, 120, dt) + Noise(0.2);

        bool cpuThrottle = _cpuTemp >= CpuLimit - 0.5;
        bool gpuThrottle = _gpuTemp >= GpuLimit - 0.5;
        _cpuTemp = Math.Min(_cpuTemp, CpuLimit);
        _gpuTemp = Math.Min(_gpuTemp, GpuLimit);

        // Each fan follows its own die like a laptop EC curve: quiet floor near
        // idle, ramps with that die's heat (the Acer WMI reader exposes CPU + GPU
        // fans separately). Dusty airflow spins harder for the same result.
        double dust = Scenario == SimScenario.DustyAirflow ? 1.14 : 1.0;
        double FanTarget(double temp) =>
            (temp < ambient + 14 ? 0 : 1700 + Math.Max(0, temp - (ambient + 14)) * 95) * dust;
        _cpuFanRpm = Clamp(Approach(_cpuFanRpm, FanTarget(_cpuTemp), 8, dt) + Noise(40), 0, 6200);
        _gpuFanRpm = Clamp(Approach(_gpuFanRpm, FanTarget(_gpuTemp), 8, dt) + Noise(40), 0, 6200);
        double? cpuFan = _cpuFanRpm > 500 ? Math.Round(_cpuFanRpm) : null;
        double? gpuFan = _gpuFanRpm > 500 ? Math.Round(_gpuFanRpm) : null;

        var components = new List<ComponentReading>
        {
            new(ComponentKind.Cpu, "Simulated Core i5-13420H",
                Math.Round(_cpuTemp, 1), null, Math.Round(_cpuLoad, 1), cpuFan,
                Math.Round(8 + _cpuLoad * 0.5, 1), null, cpuThrottle, CpuLimit),
            new(ComponentKind.GpuDiscrete, "Simulated RTX 3050 Laptop",
                Math.Round(_gpuTemp, 1), Math.Round(_gpuTemp + 7, 1), Math.Round(_gpuLoad, 1), gpuFan,
                Math.Round(5 + _gpuLoad * 0.7, 1), null, gpuThrottle, GpuLimit),
            new(ComponentKind.Storage, "Simulated NVMe SSD",
                Math.Round(_ssdTemp, 1), null, Math.Round(Math.Min(100, _cpuLoad * 0.3 + 1), 1), null, null,
                5, false, null),
            new(ComponentKind.Battery, "Simulated Battery",
                null, null, null, null, null, 7.0, false, null, BatteryCycles: 214),
        };

        return new SensorSnapshot(_now, true, components);
    }

    private void AdvanceWorkload(double dt)
    {
        // Screenshot mode holds a steady gaming load — never fall back to idle.
        if (_warmDemo)
        {
            double gCpu = 62 + _rng.NextDouble() * 16;
            double gGpu = 90 + _rng.NextDouble() * 9;
            _cpuLoad = Clamp(_cpuLoad + (gCpu - _cpuLoad) * Math.Min(1, dt / 6) + Noise(2), 0, 100);
            _gpuLoad = Clamp(_gpuLoad + (gGpu - _gpuLoad) * Math.Min(1, dt / 6) + Noise(1.5), 0, 100);
            return;
        }

        _phaseSecondsLeft -= (int)dt;
        if (_phaseSecondsLeft <= 0)
        {
            // Weighted random walk through usage phases, dwell long enough for
            // calm-then-slam soak measurements to occur naturally.
            (_phase, _phaseSecondsLeft) = _rng.Next(100) switch
            {
                < 40 => (Phase.Idle, _rng.Next(120, 400)),
                < 65 => (Phase.Light, _rng.Next(90, 300)),
                < 90 => (Phase.Gaming, _rng.Next(240, 600)),
                _ => (Phase.Burst, _rng.Next(30, 90)),
            };
        }

        (double cpuTarget, double gpuTarget) = _phase switch
        {
            Phase.Idle => (4 + _rng.NextDouble() * 4, 1.0),
            Phase.Light => (20 + _rng.NextDouble() * 15, 8 + _rng.NextDouble() * 10),
            Phase.Gaming => (55 + _rng.NextDouble() * 25, 85 + _rng.NextDouble() * 13),
            _ => (92 + _rng.NextDouble() * 8, 15 + _rng.NextDouble() * 20),
        };

        _cpuLoad = Clamp(_cpuLoad + (cpuTarget - _cpuLoad) * Math.Min(1, dt / 6) + Noise(2), 0, 100);
        _gpuLoad = Clamp(_gpuLoad + (gpuTarget - _gpuLoad) * Math.Min(1, dt / 6) + Noise(1.5), 0, 100);
    }

    private double CpuDelta(double load)
    {
        double delta = 16 + load / 100.0 * 46; // healthy: idle +16, full load +62
        return delta + Scenario switch
        {
            SimScenario.AgingPaste => load / 100.0 * 6,
            SimScenario.DegradedPaste => 2 + load / 100.0 * 13,
            SimScenario.DustyAirflow => load < 8 ? 2 : 8,
            _ => 0,
        };
    }

    private double GpuDelta(double load)
    {
        double delta = 11 + load / 100.0 * 37; // healthy: idle +11, full load +48
        return delta + Scenario switch
        {
            SimScenario.AgingPaste => load / 100.0 * 5,
            SimScenario.DegradedPaste => 1 + load / 100.0 * 12,
            SimScenario.DustyAirflow => load < 8 ? 2 : 7,
            _ => 0,
        };
    }

    private static double Approach(double current, double target, double tauSeconds, double dt) =>
        current + (target - current) * (1 - Math.Exp(-dt / tauSeconds));

    private double Noise(double scale) => (_rng.NextDouble() - 0.5) * scale;

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    public void Dispose() { }
}

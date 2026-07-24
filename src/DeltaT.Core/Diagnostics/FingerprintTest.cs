using System.Text.Json.Serialization;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Weather;

namespace DeltaT.Core.Diagnostics;

public enum FingerprintTarget { Cpu, Gpu }

/// <summary>Which leg of the test protocol is running — the machine-readable twin of
/// the prose <c>Phase</c>, so a UI can light the matching segment of a protocol strip
/// without parsing the phrase.</summary>
public enum FingerprintStage { Settle, Load, Cooldown }

public sealed record FingerprintProgress(
    string Phase, FingerprintStage Stage, double SecondsLeft, double? TempC, double? Load);

/// <summary>One fingerprint run's numbers. The primary Start/Peak/Sustained fields
/// describe whichever component was loaded (<see cref="Target"/>); their JSON names
/// keep the historical "Cpu*" spelling so every fingerprint stored before GPU runs
/// existed still deserializes for month-over-month comparison. The Gpu* fields are
/// the ride-along GPU readings of a CPU run (a game happening to run alongside).</summary>
public sealed record FingerprintResult(
    DateTimeOffset AtUtc,
    double? AmbientC,
    [property: JsonPropertyName("CpuStartC")] double StartC,
    [property: JsonPropertyName("CpuPeakC")] double PeakC,
    [property: JsonPropertyName("CpuSustainedC")] double SustainedC,
    [property: JsonPropertyName("CpuSustainedDeltaC")] double? SustainedDeltaC,
    double SoakRatePerMin,
    int ThrottleSamples,
    double? GpuPeakC,
    double? GpuSustainedDeltaC,
    bool GpuWasLoaded,
    bool OnAcPower,
    string Target = "Cpu",
    /// <summary>Which timing protocol produced these numbers. Fingerprints are only
    /// comparable within one protocol (see <see cref="FingerprintTest.CurrentProtocol"/>).
    /// Absent in fingerprints stored before protocols existed, which deserialize to 1.</summary>
    int Protocol = 1,
    /// <summary>How long the load actually ran. Under protocol 2 this varies by machine:
    /// the load stops when the component settles.</summary>
    double LoadSeconds = 0,
    /// <summary>True when the load ended because the temperature genuinely stopped
    /// climbing. False means it ran into the ceiling still rising, so this number is a
    /// floor, not the plateau. Recorded rather than hidden, because a run that never
    /// settled is not measuring the same thing as one that did.</summary>
    bool Settled = true,
    /// <summary>Intel-only day-one verdict: under this held full load the CPU reached its
    /// thermal limit AND its own MSRs confirmed HEAT (not a power/current limit) was the
    /// active limiter AND it drew meaningfully below its configured PL2. All three together
    /// mean the cooling is the ceiling from the very first run, with no baseline needed. A
    /// deliberately power-limited machine fails the thermal-limiter gate, so it never trips
    /// this. False on AMD and when the driver can't read the registers.</summary>
    bool ThermallyConstrained = false,
    /// <summary>The configured PL2 (short-term turbo watts) observed during the run, for the
    /// day-one message. Null on AMD / when unreadable.</summary>
    double? Pl2W = null);

/// <summary>The on-demand thermal fingerprint: 12 s of calm, then full load on the chosen
/// component held until it stops climbing (90 s floor, 240 s ceiling), while we watch how it
/// soaks and where it settles. Repeatable, so month-over-month fingerprints are directly
/// comparable — the active counterpart to the passive baseline. CPU runs spin every core
/// (CpuBurner); GPU runs saturate the compute engine through OpenCL (GpuBurner).
/// During a CPU run the GPU isn't loaded by us, but if a game/benchmark happens to
/// be running its numbers ride along.</summary>
public sealed class FingerprintTest
{
    /// <summary>Bumped whenever a phase duration changes. A fingerprint is only ever
    /// compared against another of the SAME protocol: the recorded temperature depends on
    /// how long the load ran, so comparing a 90 s run against an old 150 s run would read
    /// ~1.4 °C cooler and announce a paste improvement that never happened. Old stored
    /// fingerprints carry no field and deserialize to 1.</summary>
    public const int CurrentProtocol = 2;

    // Phase durations, chosen from measured thermal curves on a real laptop rather than
    // taste (i5-13420H + RTX 3050, 1 s sampling; see the timing study).
    //
    // SETTLE - the pre-load calm, whose only job is to hand the load phase a clean start
    // temperature (the tail-10 s mean). Measured: an idle die is stable within ~2 s, and
    // the start temperature came out identical (50.00 °C) whether averaged over the last
    // 5 s or the last 30 s. The old 25 s bought nothing. It cannot rescue a machine that
    // was gaming a minute ago either - no plausible settle can - so it is sized for its
    // real job and no more.
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(12);

    /// <summary>Cosmetic only: every number is already computed before this phase runs.
    /// It exists so the user watches the temperature fall instead of the window closing on
    /// a hot reading. Measured: the CPU sheds 93% of its drop within 10 s.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    /// <summary>The load doesn't run for a fixed time: it runs until the temperature stops
    /// climbing. What the fingerprint records is the steady state, so the honest stopping
    /// condition is "the machine has reached steady state", not a stopwatch. A thin laptop
    /// gets there in 90 s and stops; a desktop under a big air cooler keeps climbing for
    /// minutes and is given the time it needs. This also makes the number comparable across
    /// runs in the way that actually matters: two runs that both plateaued are both
    /// measuring the plateau, whatever the clock said.
    ///
    /// FLOOR (90 s) - the sustained temperature is the mean of the last 45 s, so the load
    /// must always outlast the opening transient or the "plateau" would be measured on the
    /// climb. Measured on the dev laptop: the CPU die is at temperature within ~10 s, but
    /// package power decays from its 40 W turbo to a steady 32.5 W over the first ~30 s and
    /// the temperature follows it down. A 90 s floor puts the whole 45 s window (t=45..90 s)
    /// past that decay with margin.
    ///
    /// CEILING (240 s) - a cap so a machine that never quite settles (or a sensor that keeps
    /// jittering) still terminates. Hitting it is recorded, not hidden.</summary>
    public static readonly TimeSpan LoadFloor = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan LoadCeiling = TimeSpan.FromSeconds(240);

    /// <summary>The window whose slope decides "settled". Same 45 s the sustained
    /// temperature is averaged over, so the test stops exactly when the thing it is about to
    /// measure has stopped moving.</summary>
    public static readonly TimeSpan PlateauWindow = TimeSpan.FromSeconds(45);

    /// <summary>Below this rate of climb the die is at its plateau. Set from the measured
    /// curves: a settled laptop CPU still creeps ~+0.5 °C/min as the chassis soaks (a real
    /// but uninteresting drift that never ends), while a GPU genuinely still climbing reads
    /// +2 °C/min at 90 s. 1 °C/min sits cleanly between the two, so it stops on the chassis
    /// creep but never on the climb.</summary>
    public const double PlateauSlopeCPerMin = 1.0;

    /// <summary>Rate of climb (°C/min): the shift between the trailing window and the window
    /// immediately before it, each reduced to a TRIMMED MEAN (outer 20% of readings dropped
    /// at both ends).
    ///
    /// Both halves of that design are load-bearing, and both were found by replaying real
    /// captured curves through this rule:
    /// - Trimmed, because a loaded die is not a clean line. This CPU throws +10 °C
    ///   single-sample turbo spikes every half-minute; a plain mean (or a least-squares fit)
    ///   reads one as "still climbing", so the test would never agree it had settled and
    ///   every run would grind on to the ceiling.
    /// - Mean rather than median, because GPU temperatures arrive quantized to whole degrees.
    ///   A median of a 45 s window can only ever land on an integer, which swamps a real
    ///   +2 °C/min climb and reports a flat 0.00 - it stopped the GPU 1.4 °C short of its
    ///   plateau in replay. Averaging many samples resolves the sub-degree trend that the
    ///   quantization hides.
    /// Comparing two adjacent windows (rather than fitting within one) puts 45 s between the
    /// two estimates, which is what makes a slow +0.5 °C/min creep distinguishable from a
    /// +2 °C/min climb at all. Pure, so the stopping rule is testable without heating
    /// anything.</summary>
    public static double? SlopePerMinute(
        IReadOnlyList<(DateTimeOffset Ts, double Temp)> samples, TimeSpan window)
    {
        if (samples.Count < 2)
            return null;

        DateTimeOffset now = samples[^1].Ts;
        var recent = samples.Where(s => s.Ts > now - window).ToList();
        var previous = samples.Where(s => s.Ts > now - window - window && s.Ts <= now - window).ToList();
        if (recent.Count < 4 || previous.Count < 4)
            return null; // not enough load history yet to compare two windows

        double gapMinutes = (Mean(recent.Select(s => s.Ts.ToUnixTimeMilliseconds() / 60000.0))
                             - Mean(previous.Select(s => s.Ts.ToUnixTimeMilliseconds() / 60000.0)));
        if (gapMinutes <= 0)
            return null;

        return (TrimmedMean(recent.Select(s => s.Temp)) - TrimmedMean(previous.Select(s => s.Temp))) / gapMinutes;
    }

    private static double Mean(IEnumerable<double> values) => values.Average();

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return null;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Mean with the outer 20% dropped at each end: keeps the sub-degree resolution
    /// of an average while ignoring turbo spikes and sensor dropouts.</summary>
    private static double TrimmedMean(IEnumerable<double> values)
    {
        var xs = values.OrderBy(v => v).ToList();
        int drop = (int)(xs.Count * 0.2);
        var kept = xs.Skip(drop).Take(Math.Max(1, xs.Count - 2 * drop)).ToList();
        return kept.Average();
    }

    /// <summary>Has the component settled? Only asked once past the floor.</summary>
    public static bool HasPlateaued(IReadOnlyList<(DateTimeOffset Ts, double Temp)> samples) =>
        SlopePerMinute(samples, PlateauWindow) is { } slope && Math.Abs(slope) <= PlateauSlopeCPerMin;

    /// <summary>How close to TjMax the peak must land to count as "reached the thermal limit".</summary>
    public const double ThermalLimitMarginC = 3.0;

    /// <summary>Fraction of PL2 below which the sustained package power counts as "meaningfully
    /// short of the budget", the same bar the live diagnosis uses.</summary>
    public const double Pl2DeficitFraction = 0.85;

    /// <summary>The day-one absolute verdict, gated so it can only ever fire when heat is
    /// genuinely the ceiling. ALL must hold: the peak reached/approached TjMax; the CPU's own
    /// limit-reason register confirmed the THERMAL limiter (not a power or current limiter) was
    /// active during the held load; and the sustained package power sat meaningfully below the
    /// configured PL2. Skipping any one of these is what would false-alarm a deliberately
    /// power-limited machine, so none may be dropped. Pure and testable: no I/O, no clocks.</summary>
    public static bool IsThermallyConstrained(
        double peakC, double? tjMaxC, bool thermalLimiterSeen, double? sustainedLoadW, double? pl2W)
    {
        if (tjMaxC is not { } tj || pl2W is not { } pl2 || sustainedLoadW is not { } w || pl2 <= 0)
            return false;
        bool reachedLimit = peakC >= tj - ThermalLimitMarginC;
        bool powerDeficit = w < pl2 * Pl2DeficitFraction;
        return reachedLimit && thermalLimiterSeen && powerDeficit;
    }

    private readonly MonitoringService _monitor;
    private readonly IAmbientProvider _ambient;
    private readonly Func<string?, IDisposable>? _gpuBurnFactory;

    /// <param name="gpuBurnFactory">How to start the GPU load, given the discrete card's name.
    /// When null the load runs in-process via <see cref="GpuBurner"/> (the Spike and tests).
    /// The real app passes a factory that runs the load in a CHILD PROCESS: OpenCL on some
    /// early GPU drivers (seen on a Blackwell RTX 50-series laptop that drives its own display)
    /// faults at the driver level, and a native access violation / TDR reset cannot be caught in
    /// managed code, so an in-process burn takes the whole app down. Isolated in a child, that
    /// fault kills only the child and the test reports it instead of crashing.</param>
    public FingerprintTest(MonitoringService monitor, IAmbientProvider ambient,
        Func<string?, IDisposable>? gpuBurnFactory = null)
    {
        _monitor = monitor;
        _ambient = ambient;
        _gpuBurnFactory = gpuBurnFactory;
    }

    public async Task<FingerprintResult> RunAsync(FingerprintTarget target,
        IProgress<FingerprintProgress>? progress, CancellationToken ct)
    {
        ComponentKind kind = target == FingerprintTarget.Gpu ? ComponentKind.GpuDiscrete : ComponentKind.Cpu;
        string label = target == FingerprintTarget.Gpu ? "GPU" : "CPU";

        // Fail fast, before spending 90 s under load, when the target has no readable sensor.
        // On a hybrid AMD-APU + NVIDIA laptop the discrete card parks under Optimus and exposes
        // no temperature until something wakes it, and DeltaT no longer lets the integrated
        // Radeon stand in for it (that stand-in is exactly what produced a GPU run recording the
        // iGPU's temperature). A clear message here beats a burn that ends in "not enough samples".
        if (target == FingerprintTarget.Gpu
            && _monitor.Latest is { } latest
            && latest.Find(ComponentKind.GpuDiscrete)?.TemperatureC is null)
        {
            throw new InvalidOperationException(
                "No discrete GPU temperature is available to fingerprint. On a hybrid laptop the "
                + "dedicated GPU may be idle or switched off; open a GPU app (or set the graphics "
                + "mode so the dedicated GPU is active) and try again.");
        }

        var samples = new List<(DateTimeOffset Ts, double Temp, bool Throttling)>();
        var gpuSamples = new List<(double Temp, double Load)>();
        // Intel-only, CPU runs: the power budget observed each tick, for the day-one
        // thermally-constrained verdict. Empty on AMD / when the driver can't read the MSRs.
        var cpuBudget = new List<(DateTimeOffset Ts, double? W, bool ThermalActive, double? Pl2)>();
        double? cpuTjMax = null; // TjMax of the loaded CPU, for the day-one thermal-limit gate
        bool onAc = true;

        // The active phase and when it ends, so a snapshot arriving mid-phase can report
        // an accurate countdown alongside its reading. Written on the run thread at each
        // phase change, read on the monitor thread in Collect — stable within a phase, so
        // a torn read at worst mislabels a single frame's countdown by a tick.
        string phase = "";
        FingerprintStage stage = FingerprintStage.Settle;
        DateTimeOffset phaseEnd = DateTimeOffset.UtcNow;

        void Collect(SensorSnapshot snap)
        {
            onAc = snap.OnAcPower;
            if (snap.Find(kind) is { TemperatureC: { } t } reading)
            {
                samples.Add((snap.TimestampUtc, t, reading.IsThrottling));
                if (target == FingerprintTarget.Cpu && reading.PowerLimit is { } pl)
                    cpuBudget.Add((snap.TimestampUtc, reading.PowerW, pl.ThermalActive, pl.Pl2W));
                if (target == FingerprintTarget.Cpu && reading.ThrottleLimitC is { } tj)
                    cpuTjMax = tj;
                // Push the reading the instant it lands — the same snapshot the main
                // window renders from — so the fingerprint gauge can't trail it by a tick.
                if (phase.Length > 0)
                    progress?.Report(new FingerprintProgress(
                        phase, stage, Math.Max(0, (phaseEnd - DateTimeOffset.UtcNow).TotalSeconds), t, null));
            }
            if (target == FingerprintTarget.Cpu
                && snap.Find(ComponentKind.GpuDiscrete) is { TemperatureC: { } gt, LoadPercent: { } gl })
                gpuSamples.Add((gt, gl));
        }

        _monitor.SnapshotCaptured += Collect;
        try
        {
            await TickPhase("Settling. Hands off the machine", FingerprintStage.Settle, Settle, progress, ct).ConfigureAwait(false);

            double start = TailAverage(samples, TimeSpan.FromSeconds(10)) ?? samples.LastOrDefault().Temp;
            int loadStartIndex = samples.Count;
            int gpuLoadStartIndex = gpuSamples.Count;
            DateTimeOffset loadStartTs = DateTimeOffset.UtcNow;

            bool plateaued;
            using (IDisposable burner = CreateBurner(target))
            {
                plateaued = await HoldUntilSettled($"Full {label} load, holding until it settles",
                    () => samples.Skip(loadStartIndex).Select(s => (s.Ts, s.Temp)).ToList(),
                    loadStartTs, progress, ct).ConfigureAwait(false);
            }
            var loadDuration = DateTimeOffset.UtcNow - loadStartTs;

            var loadSamples = samples.Skip(loadStartIndex).ToList();
            if (loadSamples.Count < 10)
                throw new InvalidOperationException("Not enough sensor samples during the load phase.");

            double peak = loadSamples.Max(s => s.Temp);
            double sustained = TailAverage(samples, TimeSpan.FromSeconds(45)) ?? peak;
            int throttles = loadSamples.Count(s => s.Throttling);

            // Soak rate: rise over the first 60 s of load.
            var minuteIn = loadSamples.Where(s => s.Ts <= loadStartTs + TimeSpan.FromSeconds(60)).ToList();
            double soakRate = minuteIn.Count >= 2
                ? (minuteIn[^1].Temp - start) / Math.Max(10, (minuteIn[^1].Ts - loadStartTs).TotalSeconds) * 60
                : 0;

            double? ambient = _ambient.CurrentAmbientC;
            var gpuLoaded = gpuSamples.Skip(gpuLoadStartIndex).Where(g => g.Load >= 60).ToList();

            // Day-one absolute verdict (Intel CPU runs only): did the CPU hit its thermal limit
            // while drawing below its PL2, with its own MSRs naming HEAT as the limiter? The load
            // phase pins every core (CpuBurner), so the "load pinned" gate is satisfied by
            // construction; the remaining gates come from the registers observed during it.
            var loadBudget = cpuBudget.Where(b => b.Ts >= loadStartTs).ToList();
            bool thermalLimiterSeen = loadBudget.Any(b => b.ThermalActive);
            double? sustainedLoadW = Median(loadBudget.Where(b => b.W is > 0).Select(b => b.W!.Value).ToList());
            double? pl2W = loadBudget.Select(b => b.Pl2).LastOrDefault(p => p is > 0);
            bool thermallyConstrained = target == FingerprintTarget.Cpu
                && IsThermallyConstrained(peak, cpuTjMax, thermalLimiterSeen, sustainedLoadW, pl2W);

            var result = new FingerprintResult(
                AtUtc: DateTimeOffset.UtcNow,
                AmbientC: ambient,
                StartC: Math.Round(start, 1),
                PeakC: Math.Round(peak, 1),
                SustainedC: Math.Round(sustained, 1),
                SustainedDeltaC: ambient is { } a ? Math.Round(sustained - a, 1) : null,
                SoakRatePerMin: Math.Round(soakRate, 1),
                ThrottleSamples: throttles,
                GpuPeakC: gpuLoaded.Count > 0 ? Math.Round(gpuLoaded.Max(g => g.Temp), 1) : null,
                GpuSustainedDeltaC: gpuLoaded.Count > 0 && ambient is { } a2
                    ? Math.Round(gpuLoaded.TakeLast(20).Average(g => g.Temp) - a2, 1) : null,
                GpuWasLoaded: gpuLoaded.Count > 0,
                OnAcPower: onAc,
                Target: target.ToString(),
                Protocol: CurrentProtocol,
                LoadSeconds: Math.Round(loadDuration.TotalSeconds, 1),
                Settled: plateaued,
                ThermallyConstrained: thermallyConstrained,
                Pl2W: pl2W is { } p ? Math.Round(p, 0) : null);

            // Cooldown ticks like any other phase — the countdown counts down and the
            // gauge keeps tracking the component as it falls — but a Stop here just ends
            // the wait and still returns the finished result: the numbers are already in hand.
            await TickPhase("Cooling down", FingerprintStage.Cooldown, Cooldown, progress, ct, cancelable: false).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _monitor.SnapshotCaptured -= Collect;
        }

        async Task TickPhase(string phaseName, FingerprintStage phaseStage, TimeSpan duration,
            IProgress<FingerprintProgress>? prog, CancellationToken token, bool cancelable = true)
        {
            phase = phaseName;
            stage = phaseStage;
            DateTimeOffset end = DateTimeOffset.UtcNow + duration;
            phaseEnd = end;
            while (DateTimeOffset.UtcNow < end)
            {
                if (token.IsCancellationRequested)
                {
                    if (cancelable) token.ThrowIfCancellationRequested();
                    return;
                }
                // Countdown tick every half second so the timer never stalls. The
                // temperature rides in live from Collect; null here means "unchanged",
                // so the gauge holds the last reading between snapshots.
                prog?.Report(new FingerprintProgress(phaseName, phaseStage, (end - DateTimeOffset.UtcNow).TotalSeconds, null, null));
                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (cancelable) throw; return; }
            }
        }

        // The load phase: hold until the component stops climbing, between the floor and
        // the ceiling. Returns whether it actually settled (false = it ran into the cap
        // while still moving, which the result records rather than papers over).
        async Task<bool> HoldUntilSettled(string phaseName,
            Func<List<(DateTimeOffset Ts, double Temp)>> loadSamplesSoFar, DateTimeOffset startedAt,
            IProgress<FingerprintProgress>? prog, CancellationToken token)
        {
            phase = phaseName;
            stage = FingerprintStage.Load;
            DateTimeOffset floor = startedAt + LoadFloor;
            DateTimeOffset ceiling = startedAt + LoadCeiling;
            phaseEnd = ceiling; // the countdown shows the worst case; settling early ends it sooner

            // Two consecutive passes are needed to stop. One flat window can happen by luck
            // between two turbo spikes; two in a row cannot.
            int consecutiveFlat = 0;

            while (DateTimeOffset.UtcNow < ceiling)
            {
                token.ThrowIfCancellationRequested();

                if (DateTimeOffset.UtcNow >= floor)
                {
                    consecutiveFlat = HasPlateaued(loadSamplesSoFar()) ? consecutiveFlat + 1 : 0;
                    if (consecutiveFlat >= 2)
                        return true;
                }

                prog?.Report(new FingerprintProgress(
                    phaseName, FingerprintStage.Load, (ceiling - DateTimeOffset.UtcNow).TotalSeconds, null, null));
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            return false; // hit the ceiling still climbing
        }

        double? TailAverage(List<(DateTimeOffset Ts, double Temp, bool Throttling)> list, TimeSpan tail)
        {
            if (list.Count == 0) return null;
            DateTimeOffset cut = list[^1].Ts - tail;
            var window = list.Where(s => s.Ts >= cut).ToList();
            return window.Count > 0 ? window.Average(s => s.Temp) : null;
        }
    }

    private IDisposable CreateBurner(FingerprintTarget target)
    {
        if (target == FingerprintTarget.Cpu)
            return new CpuBurner();
        // Aim the compute load at the same GPU the sensors watch (the discrete card
        // on a hybrid laptop, not the display iGPU).
        string? gpuName = _monitor.Latest?.Find(ComponentKind.GpuDiscrete)?.Name;
        return _gpuBurnFactory is { } factory ? factory(gpuName) : new GpuBurner(gpuName);
    }
}

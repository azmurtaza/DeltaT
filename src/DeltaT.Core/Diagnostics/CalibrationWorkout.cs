namespace DeltaT.Core.Diagnostics;

/// <summary>A CPU load source whose intensity can be steered at runtime. Implemented by
/// <see cref="CpuBurner"/>; abstracted so the workout controller is testable without spinning
/// real cores.</summary>
public interface ILoadEngine : IDisposable
{
    double Utilization { get; set; }
}

public enum WorkoutPhase { Medium, Heavy, Done }

/// <summary>A tick of workout progress, for a status line or progress bar.</summary>
public sealed record WorkoutProgress(
    WorkoutPhase Phase,
    double OverallFraction,     // 0..1 across the whole workout
    int TargetLoadPct,
    double? CurrentLoadPct,     // measured CPU load this tick (null if unavailable)
    TimeSpan Remaining);

/// <summary>Guided calibration for the CPU's Medium and Heavy load buckets — the scarce ones
/// organic use is slow to fill (on the dev machine Heavy had 8 learned minutes against 58-112
/// for the lighter buckets). It drives a closed-loop CPU load to a target percentage and holds
/// each bucket while the NORMAL monitoring pipeline records the loaded minutes.
///
/// It is deliberately inert toward accuracy: it writes nothing itself and never touches
/// <c>ScoringEngine</c>, <c>BaselineBuilder</c>, or the baseline schema. It only manufactures
/// real loaded telemetry, recorded with its true measured watts, so the UNCHANGED confidence
/// gate can lock sooner. A single run is one independent bout; reaching a lock still needs the
/// gate's several spaced observations, so the machine is never talked into a baseline it didn't
/// earn.
///
/// It does NOT cover Max, by measured design. A full-load bucket only calibrates faithfully at
/// the same boost/power state its organic data was learned at (measured: a boost-off workout
/// read 47% under a boost-on Max baseline, squarely the accuracy-wrecking regime), and the
/// fingerprint already fills Max. Medium and Heavy barely engage turbo, so they sit at the
/// machine's real operating point (measured within ~6% of the learned baseline watts) and stay
/// inside the safe band the acquisition-fidelity benchmark proved.</summary>
public sealed class CalibrationWorkout
{
    public const int MediumTargetPct = 55;
    public const int HeavyTargetPct = 80;

    // Proportional gain: nudge duty by this much per point of load error. Small, because the
    // load-vs-duty curve is steep — a gentle push converges without oscillating.
    private const double Kp = 0.004;

    private readonly TimeSpan _holdPerPhase;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="holdPerPhase">How long to hold each bucket. Default 3 min, enough to bank
    /// several loaded minutes per bucket in one bout.</param>
    /// <param name="delay">Injectable for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public CalibrationWorkout(TimeSpan? holdPerPhase = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _holdPerPhase = holdPerPhase ?? TimeSpan.FromMinutes(3);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public TimeSpan TotalDuration => _holdPerPhase * 2;

    /// <summary>Pure controller step: steer duty toward the load target from the MEASURED load.
    /// Open-loop duty overshoots hard on real silicon (a little burn saturates the load
    /// counter), so a fixed duty can't hit a bucket; this corrects each tick. A missing reading
    /// holds the current duty rather than guessing. Result clamped to a sane burn range.</summary>
    public static double SteerUtilization(double util, int targetPct, double? currentPct)
    {
        if (currentPct is not { } cur) return util;
        return Math.Clamp(util + Kp * (targetPct - cur), 0.05, 1.0);
    }

    /// <summary>Run the workout: hold Medium then Heavy, steering the engine each second from the
    /// live CPU load, reporting progress. Throws <see cref="OperationCanceledException"/> if
    /// cancelled; always disposes the engine so the load stops the moment it ends.</summary>
    public async Task RunAsync(
        Func<double, ILoadEngine> makeEngine, Func<double?> readCpuLoad,
        IProgress<WorkoutProgress>? progress, CancellationToken token)
    {
        (WorkoutPhase Phase, int Target)[] phases =
        {
            (WorkoutPhase.Medium, MediumTargetPct),
            (WorkoutPhase.Heavy, HeavyTargetPct),
        };
        TimeSpan total = _holdPerPhase * phases.Length;
        TimeSpan elapsed = TimeSpan.Zero;
        var tick = TimeSpan.FromSeconds(1);

        foreach ((WorkoutPhase phase, int target) in phases)
        {
            double util = target / 100.0; // first guess; the loop corrects it
            using ILoadEngine engine = makeEngine(util);
            for (TimeSpan phaseElapsed = TimeSpan.Zero; phaseElapsed < _holdPerPhase; phaseElapsed += tick)
            {
                token.ThrowIfCancellationRequested();
                await _delay(tick, token).ConfigureAwait(false);
                elapsed += tick;
                double? load = readCpuLoad();
                util = SteerUtilization(util, target, load);
                engine.Utilization = util;
                progress?.Report(new WorkoutProgress(
                    phase, Math.Min(1.0, elapsed / total), target, load, total - elapsed));
            }
        }
        progress?.Report(new WorkoutProgress(WorkoutPhase.Done, 1.0, 0, null, TimeSpan.Zero));
    }
}

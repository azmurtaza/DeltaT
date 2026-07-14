using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;

namespace DeltaT.Core.Scoring;

/// <summary>The ground-truth condition a synthetic machine is put in, so the benchmark
/// can check whether DeltaT recovers the cause it was actually given.</summary>
public enum Condition
{
    Healthy,
    PasteDegraded,
    DustAirflow,
    FanFault,
    MountPumpout,
    Overclock,   // a confounder: hotter, but by design — must NOT read as a fault
    Undervolt,   // a confounder: cooler, but by config — must NOT read as improvement/fault
    ColdSeason,  // a confounder: cooler because the room is cold — must NOT false-alarm
}

/// <summary>Per-condition tally: how often DeltaT reached the right conclusion.</summary>
public sealed record ConditionResult(
    Condition Condition,
    int Trials,
    int CorrectCause,       // primary diagnosis matched the intended cause (fault conditions)
    int CorrectlyCleared,   // correctly NOT flagged as a fault (confounder/healthy conditions)
    double MeanScore)
{
    public bool IsFault => Condition is Condition.PasteDegraded or Condition.DustAirflow
        or Condition.FanFault or Condition.MountPumpout;

    public double Accuracy => Trials == 0 ? 0
        : (IsFault ? CorrectCause : CorrectlyCleared) / (double)Trials;
}

/// <summary>Detection-sensitivity curve for one fault: the smallest severity (°C of extra
/// load-rise, or ° of gap) at which the score crosses each verdict boundary and at which the
/// diagnosis first names the right cause.</summary>
public sealed record SensitivityCurve(
    string Fault,
    double? FlagsAgingAtC,     // score first drops below 85 (Fresh→Aging watch)
    double? FlagsActionAtC,    // score first drops below 70 (Good→plan-a-fix)
    double? NamesCauseAtC);    // diagnosis first names the correct cause

public sealed record BenchmarkReport(
    IReadOnlyList<ConditionResult> Conditions,
    IReadOnlyList<SensitivityCurve> Sensitivity)
{
    public double FaultDetectionRate => Rate(c => c.IsFault);
    public double ConfounderClearRate => Rate(c => !c.IsFault);
    public double OverallAccuracy => Conditions.Count == 0 ? 0 : Conditions.Average(c => c.Accuracy);

    private double Rate(Func<ConditionResult, bool> pick)
    {
        var subset = Conditions.Where(pick).ToList();
        if (subset.Count == 0) return 0;
        int trials = subset.Sum(c => c.Trials);
        int correct = subset.Sum(c => c.IsFault ? c.CorrectCause : c.CorrectlyCleared);
        return trials == 0 ? 0 : correct / (double)trials;
    }
}

/// <summary>Measures how accurately the scoring + diagnosis engine recovers a KNOWN thermal
/// fault from realistic, noisy telemetry. It is not a self-graded demo: each trial injects a
/// specific ground-truth condition through the same physics the real machine obeys
/// (rise scales with power, degraded paste raises load-dependent rise and slows heat
/// transfer both ways, dust lifts every bucket while fans work harder, a tired fan spins
/// slower), then runs the <b>real</b> <see cref="ScoringEngine"/> and
/// <see cref="ThermalDiagnostician"/> and checks the verdict against the truth. The numbers
/// it returns are DeltaT's actual discrimination under measurement noise. Pure and seeded, so
/// a run is reproducible and unit-testable.</summary>
public static class DetectionBenchmark
{
    // A mid-range gaming-laptop CPU baseline: rise-over-ambient and package watts per bucket.
    private static readonly ComponentProfile Profile = new(TypicalIdleDeltaC: 18, TypicalHeavyDeltaC: 60, SustainedNormC: 90, ConcernC: 96);
    private const double AmbientC = 28;         // Warm band
    private const int Warm = (int)AmbientBand.Warm;
    private const int Cold = (int)AmbientBand.Cold;
    private const double LimitC = 100;

    private readonly record struct Cell(LoadBucket Bucket, double Delta, double PowerW, double FanRpm);

    // Healthy reference the machine learned. Deltas rise with load; power rises with load.
    private static readonly Cell[] Baseline =
    {
        new(LoadBucket.Idle, 18, 8, 1500),
        new(LoadBucket.Medium, 42, 45, 3400),
        new(LoadBucket.Heavy, 58, 72, 4200),
        new(LoadBucket.Max, 64, 90, 4600),
    };

    public static BenchmarkReport Run(int seed = 20260713, int trialsPerCondition = 400)
    {
        var rng = new Random(seed);
        var conditions = new List<ConditionResult>();
        foreach (Condition condition in Enum.GetValues<Condition>())
            conditions.Add(RunCondition(condition, trialsPerCondition, rng));

        var curves = new List<SensitivityCurve>
        {
            SweepPaste(rng),
            SweepDust(rng),
            SweepMount(rng),
        };
        return new BenchmarkReport(conditions, curves);
    }

    private static ConditionResult RunCondition(Condition condition, int trials, Random rng)
    {
        int correctCause = 0, correctlyCleared = 0;
        double scoreSum = 0;
        for (int i = 0; i < trials; i++)
        {
            // Fault conditions get a clearly-degraded but realistic severity; confounders get
            // a realistic magnitude of their (non-fault) effect.
            double severity = condition switch
            {
                Condition.PasteDegraded => 6 + rng.NextDouble() * 6,   // +6..12 °C load-rise
                Condition.DustAirflow => 4 + rng.NextDouble() * 5,     // +4..9 °C broad
                Condition.FanFault => 3 + rng.NextDouble() * 4,
                Condition.MountPumpout => 10 + rng.NextDouble() * 10,  // +10..20 ° gap
                Condition.Overclock => 0.25 + rng.NextDouble() * 0.15, // +25..40 % power
                Condition.Undervolt => 0.20 + rng.NextDouble() * 0.15, // −20..35 % power
                _ => 0,
            };

            ComponentScore score = ScoreTrial(condition, severity, rng, out ThermalCause primary);
            scoreSum += score.Value;

            if (Matches(condition, primary))
                correctCause++;
            // "Cleared" = not talked into a cooling fault: healthy-ish score AND the leading
            // cause is not one of the hardware faults.
            bool flaggedFault = score.Value < 70 || primary is ThermalCause.Paste or ThermalCause.Airflow
                or ThermalCause.FanFault or ThermalCause.Mount;
            if (!flaggedFault)
                correctlyCleared++;
        }
        return new ConditionResult(condition, trials, correctCause, correctlyCleared, scoreSum / trials);
    }

    private static bool Matches(Condition condition, ThermalCause primary) => condition switch
    {
        Condition.PasteDegraded => primary == ThermalCause.Paste,
        Condition.DustAirflow => primary == ThermalCause.Airflow,
        Condition.FanFault => primary == ThermalCause.FanFault,
        Condition.MountPumpout => primary == ThermalCause.Mount,
        // Confounders "match" when the engine does NOT invent a hardware fault.
        _ => primary is ThermalCause.Healthy or ThermalCause.PowerConfig or ThermalCause.HighAmbient,
    };

    private static ComponentScore ScoreTrial(Condition condition, double severity, Random rng, out ThermalCause primary)
    {
        var baseRows = Baseline.Select(c => new BaselineBucket(
            c.Bucket, Warm, c.Delta, c.Delta + 3, c.FanRpm, 300, AmbientC + c.Delta,
            GapAvg: c.Bucket == LoadBucket.Idle ? null : 10, PowerAvg: c.PowerW)).ToList();

        double soakBase = 20, coolBase = 22;
        double soakRecent = soakBase, coolRecent = coolBase;
        var recent = new List<RecentBucketObs>();
        int band = condition == Condition.ColdSeason ? Cold : Warm;
        double ambient = condition == Condition.ColdSeason ? 4 : AmbientC;

        foreach (Cell c in Baseline)
        {
            double delta = c.Delta;
            double power = c.PowerW;
            double fan = c.FanRpm;
            double gap = c.Bucket == LoadBucket.Idle ? 0 : 10;
            double loadFrac = c.Bucket switch
            {
                LoadBucket.Idle => 0.0, LoadBucket.Medium => 0.5,
                LoadBucket.Heavy => 0.8, _ => 1.0,
            };

            switch (condition)
            {
                case Condition.PasteDegraded:
                    delta += severity * loadFrac;              // load-dependent extra rise
                    soakRecent = soakBase * (1 + 0.06 * severity);
                    coolRecent = coolBase * (1 - 0.05 * severity);
                    break;
                case Condition.DustAirflow:
                    delta += severity * (0.5 + 0.5 * loadFrac); // broad, present even near idle
                    fan *= 1.15;                                // fans work harder for the same result
                    break;
                case Condition.FanFault:
                    fan *= 0.6;                                 // can't reach its old speed
                    delta += severity * loadFrac * 0.6;
                    break;
                case Condition.MountPumpout:
                    if (c.Bucket != LoadBucket.Idle) gap += severity; // hotspot pulls away from edge
                    break;
                case Condition.Overclock:
                    power *= 1 + severity;                       // more watts → proportionally hotter
                    delta *= 1 + severity;
                    break;
                case Condition.Undervolt:
                    power *= 1 - severity;
                    delta *= 1 - severity;
                    break;
            }

            // Measurement noise on every channel.
            delta += Gauss(rng, 0.6);
            power = Math.Max(1, power + Gauss(rng, power * 0.03));
            fan = Math.Max(0, fan + Gauss(rng, 60));
            double temp = ambient + delta;

            recent.Add(new RecentBucketObs(
                c.Bucket, band, 60, delta, temp, temp + 4, fan, 0,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : gap + Gauss(rng, 0.5),
                PowerAvg: power));
        }

        var input = new ScoreInput(
            ComponentKind.GpuDiscrete, "Bench GPU", recent, baseRows,
            RecentWindowHours: 7 * 24, ThrottleEvents: 0,
            SoakRateRecent: soakRecent + Gauss(rng, 0.5), SoakRateBaseline: soakBase,
            CooldownRateRecent: coolRecent + Gauss(rng, 0.5), CooldownRateBaseline: coolBase,
            LimitC: LimitC, Profile: Profile, BaselineReady: true, CalibrationProgress: 1.0);

        ComponentScore score = ScoringEngine.Score(input, t => $"{t:0} °C");
        primary = score.Diagnosis?.Primary.Cause ?? ThermalCause.Healthy;
        return score;
    }

    // ---- sensitivity sweeps: find the smallest severity that trips each threshold ----

    private static SensitivityCurve SweepPaste(Random rng) =>
        Sweep("Degraded paste", Condition.PasteDegraded, ThermalCause.Paste, 0.5, 14, rng);

    private static SensitivityCurve SweepDust(Random rng) =>
        Sweep("Dust / airflow", Condition.DustAirflow, ThermalCause.Airflow, 0.5, 12, rng);

    private static SensitivityCurve SweepMount(Random rng) =>
        Sweep("Mount / pump-out", Condition.MountPumpout, ThermalCause.Mount, 1, 24, rng);

    private static SensitivityCurve Sweep(string name, Condition condition, ThermalCause cause, double from, double to, Random rng)
    {
        double? aging = null, action = null, named = null;
        for (double s = from; s <= to; s += 0.5)
        {
            // Average several noisy trials at this severity for a stable crossing point.
            double scoreSum = 0; int namedHits = 0; const int n = 40;
            for (int i = 0; i < n; i++)
            {
                ComponentScore sc = ScoreTrial(condition, s, rng, out ThermalCause primary);
                scoreSum += sc.Value;
                if (primary == cause) namedHits++;
            }
            double meanScore = scoreSum / n;
            aging ??= meanScore < 85 ? s : null;
            action ??= meanScore < 70 ? s : null;
            named ??= namedHits >= n * 0.5 ? s : null;
            if (aging is not null && action is not null && named is not null)
                break;
        }
        return new SensitivityCurve(name, aging, action, named);
    }

    private static double Gauss(Random rng, double sd)
    {
        if (sd <= 0) return 0;
        double u1 = 1 - rng.NextDouble(), u2 = 1 - rng.NextDouble();
        return sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

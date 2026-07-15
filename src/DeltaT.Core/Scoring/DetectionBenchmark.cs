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

/// <summary>Per-condition acquisition-fidelity tally: how far an organically-acquired and a
/// workout-acquired baseline move the final score away from the ideal (true) baseline, and
/// how often they flip the leading cause. The workout is only worth building if its numbers
/// track the organic ones.</summary>
public sealed record FidelityResult(
    Condition Condition,
    int Trials,
    double OrganicMeanAbsErr,     // mean |score(organic) − score(true)|, in score points
    double SyntheticMeanAbsErr,   // mean |score(workout) − score(true)|
    double OrganicMaxAbsErr,
    double SyntheticMaxAbsErr,
    int OrganicFlips,             // leading cause differed from the true-baseline diagnosis
    int SyntheticFlips);

public sealed record FidelityReport(IReadOnlyList<FidelityResult> Conditions)
{
    private int Total => Conditions.Sum(c => c.Trials);
    public double OrganicMeanAbsErr => Total == 0 ? 0 : Conditions.Sum(c => c.OrganicMeanAbsErr * c.Trials) / Total;
    public double SyntheticMeanAbsErr => Total == 0 ? 0 : Conditions.Sum(c => c.SyntheticMeanAbsErr * c.Trials) / Total;
    public double SyntheticMaxAbsErr => Conditions.Count == 0 ? 0 : Conditions.Max(c => c.SyntheticMaxAbsErr);
    public int OrganicFlips => Conditions.Sum(c => c.OrganicFlips);
    public int SyntheticFlips => Conditions.Sum(c => c.SyntheticFlips);

    /// <summary>The go/no-go: a workout-acquired baseline must be no less faithful than an
    /// organically-acquired one (within a small score tolerance) and must not add any
    /// cause-attribution flips. If this holds, the feature keeps accuracy; if not, it doesn't.</summary>
    public bool SyntheticNoWorseThanOrganic(double scoreTolerance = 1.0) =>
        SyntheticMeanAbsErr <= OrganicMeanAbsErr + scoreTolerance
        && SyntheticFlips <= OrganicFlips;
}

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
            double severity = Severity(condition, rng);

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

    /// <summary>Realistic magnitude of a condition's effect, shared by every path so the
    /// fault benchmark and the acquisition-fidelity benchmark stress the engine identically.</summary>
    private static double Severity(Condition condition, Random rng) => condition switch
    {
        Condition.PasteDegraded => 6 + rng.NextDouble() * 6,   // +6..12 °C load-rise
        Condition.DustAirflow => 4 + rng.NextDouble() * 5,     // +4..9 °C broad
        Condition.FanFault => 3 + rng.NextDouble() * 4,
        Condition.MountPumpout => 10 + rng.NextDouble() * 10,  // +10..20 ° gap
        Condition.Overclock => 0.25 + rng.NextDouble() * 0.15, // +25..40 % power
        Condition.Undervolt => 0.20 + rng.NextDouble() * 0.15, // −20..35 % power
        _ => 0,
    };

    private static ComponentScore ScoreTrial(Condition condition, double severity, Random rng, out ThermalCause primary)
    {
        List<RecentBucketObs> recent = BuildRecent(condition, severity, rng,
            out double soakRecent, out double coolRecent, out double soakBase, out double coolBase);
        List<BaselineBucket> baseRows = BuildBaselineRows(Acquisition.Perfect, 0, rng);
        return ScoreAgainst(recent, soakRecent, coolRecent, soakBase, coolBase, baseRows, out primary);
    }

    /// <summary>Generate this trial's recent telemetry (the machine's true behaviour under the
    /// condition, plus measurement noise on every channel). Split out from scoring so the same
    /// recent load can be scored against several differently-acquired baselines.</summary>
    private static List<RecentBucketObs> BuildRecent(
        Condition condition, double severity, Random rng,
        out double soakRecent, out double coolRecent, out double soakBase, out double coolBase)
    {
        soakBase = 20; coolBase = 22;
        double sRec = soakBase, cRec = coolBase;
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
                    sRec = soakBase * (1 + 0.06 * severity);
                    cRec = coolBase * (1 - 0.05 * severity);
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
                // A power change (an overclock, an undervolt, a raised/lowered limit, or CPU
                // boost switched on or off) moves the RATES as well as the temperatures: the
                // die soaks up heat proportionally faster on more watts, and it starts its
                // cooldown from a proportionally hotter point. Both must be modelled, or the
                // benchmark can't see the engine mistaking a boost-mode change for paste.
                case Condition.Overclock:
                    power *= 1 + severity;                       // more watts → proportionally hotter
                    delta *= 1 + severity;
                    sRec = soakBase * (1 + severity);
                    cRec = coolBase * (1 + severity);
                    break;
                case Condition.Undervolt:
                    power *= 1 - severity;
                    delta *= 1 - severity;
                    sRec = soakBase * (1 - severity);
                    cRec = coolBase * (1 - severity);
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

        soakRecent = sRec + Gauss(rng, 0.5);
        coolRecent = cRec + Gauss(rng, 0.5);
        return recent;
    }

    private static ComponentScore ScoreAgainst(
        List<RecentBucketObs> recent, double soakRecent, double coolRecent,
        double soakBase, double coolBase, List<BaselineBucket> baseRows, out ThermalCause primary)
    {
        var input = new ScoreInput(
            ComponentKind.GpuDiscrete, "Bench GPU", recent, baseRows,
            RecentWindowHours: 7 * 24, ThrottleEvents: 0,
            SoakRateRecent: soakRecent, SoakRateBaseline: soakBase,
            CooldownRateRecent: coolRecent, CooldownRateBaseline: coolBase,
            LimitC: LimitC, Profile: Profile, BaselineReady: true, CalibrationProgress: 1.0);

        ComponentScore score = ScoringEngine.Score(input, t => $"{t:0} °C");
        primary = score.Diagnosis?.Primary.Cause ?? ThermalCause.Healthy;
        return score;
    }

    // ===================== Phase 0: baseline-acquisition fidelity =====================
    // The guided-calibration idea is: instead of waiting for organic loads to fill the
    // baseline, run controlled graded loads to fill it fast. That only earns its place if a
    // synthetically-acquired baseline produces the SAME verdicts as an organically-acquired
    // one. This measures exactly that, in numbers, so the "does it hold accuracy?" question
    // is answered by the benchmark and not by intuition.

    /// <summary>How a baseline was gathered before scoring runs against it.</summary>
    public enum Acquisition
    {
        Perfect,    // the machine's true cell means, noise-free: the ideal reference
        Organic,    // many samples spread across each bucket's real operating range (normal use)
        Synthetic,  // fewer, tighter samples at the workout's fixed operating point (a burner
                    // tends to sit a little higher in the bucket than typical use)
    }

    // Organic use scatters a cell across many minutes and operating points; a workout is a
    // handful of tight, repeatable holds. Neither count matters for the cell MEAN much once
    // it converges, so the discriminator is the operating-point offset below, not sample size.
    private const int OrganicSamplesPerCell = 30;
    private const int SyntheticSamplesPerCell = 15;
    private const double OrganicPowerSpread = 0.12;   // ±12 % power scatter within a bucket
    private const double SyntheticPowerSpread = 0.02;  // a controlled hold is tight
    // How far the workout's fixed operating point sits from the user's average use in the
    // same bucket, as a fraction of bucket power. This is the real design lever: a workout
    // that targets each bucket's representative point has a small offset; a naive burner that
    // just pins the bucket ceiling has a large one. Swept, because it's the whole question.
    public const double WellTargetedWorkoutBias = 0.03;
    public const double NaiveBurnerBias = 0.15;
    // Thermal resistance drifts slightly with operating point (leakage rises with power/temp),
    // so a fixed workout point is NOT trivially identical to organic usage: any residual the
    // power-normalizer can't absorb shows up as fidelity error. Without this the test is a gimme.
    private const double LeakageK = 0.06;

    /// <summary>True die-to-ambient rise for a bucket at a given package power, with a mild
    /// leakage nonlinearity so °C/W is not perfectly constant across the operating range.</summary>
    private static double TrueDelta(Cell c, double powerW)
    {
        double r0 = c.Delta / c.PowerW;                                  // canonical resistance °C/W
        double r = r0 * (1 + LeakageK * (powerW - c.PowerW) / c.PowerW); // drifts with operating point
        return r * powerW;
    }

    /// <summary>Simulate acquiring a baseline the given way and return the cells scoring will
    /// compare against. Perfect returns the exact truth; Organic/Synthetic average simulated
    /// calibration samples, so any acquisition bias (operating-point offset the normalizer
    /// can't fully absorb) is baked into the returned cell means.</summary>
    private static List<BaselineBucket> BuildBaselineRows(Acquisition mode, double synBias, Random rng)
    {
        if (mode == Acquisition.Perfect)
            return Baseline.Select(c => new BaselineBucket(
                c.Bucket, Warm, c.Delta, c.Delta + 3, c.FanRpm, 300, AmbientC + c.Delta,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : 10, PowerAvg: c.PowerW)).ToList();

        bool syn = mode == Acquisition.Synthetic;
        int samples = syn ? SyntheticSamplesPerCell : OrganicSamplesPerCell;
        double bias = syn ? synBias : 0.0;
        double spread = syn ? SyntheticPowerSpread : OrganicPowerSpread;

        var rows = new List<BaselineBucket>();
        foreach (Cell c in Baseline)
        {
            double dSum = 0, pSum = 0, fSum = 0, gSum = 0;
            for (int i = 0; i < samples; i++)
            {
                double power = Math.Max(1, c.PowerW * (1 + bias + Gauss(rng, spread)));
                double delta = TrueDelta(c, power) + Gauss(rng, 0.6);
                double fan = Math.Max(0, c.FanRpm * (1 + 0.4 * (power / c.PowerW - 1)) + Gauss(rng, 60));
                dSum += delta; pSum += power; fSum += fan; gSum += 10 + Gauss(rng, 0.5);
            }
            double dAvg = dSum / samples, pAvg = pSum / samples, fAvg = fSum / samples, gAvg = gSum / samples;
            rows.Add(new BaselineBucket(
                c.Bucket, Warm, dAvg, dAvg + 3, fAvg, samples * 5, AmbientC + dAvg,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : gAvg, PowerAvg: pAvg));
        }
        return rows;
    }

    public static FidelityReport RunAcquisitionFidelity(
        int seed = 20260716, int trialsPerCondition = 300, double synBias = WellTargetedWorkoutBias)
    {
        var rng = new Random(seed);
        var results = new List<FidelityResult>();
        foreach (Condition condition in Enum.GetValues<Condition>())
            results.Add(RunFidelityCondition(condition, trialsPerCondition, synBias, rng));
        return new FidelityReport(results);
    }

    /// <summary>The fault classification a cause belongs to. Health, a benign power difference,
    /// and high ambient are all "no fault" and interchangeable: reclassifying between them is
    /// cosmetic, not an accuracy failure (the main benchmark's Matches treats them the same).
    /// A flip only matters when it changes WHICH fault (or fault-vs-none) is named.</summary>
    private static int CauseClass(ThermalCause cause) => cause switch
    {
        ThermalCause.Paste => 1,
        ThermalCause.Airflow => 2,
        ThermalCause.FanFault => 3,
        ThermalCause.Mount => 4,
        _ => 0,   // Healthy / PowerConfig / HighAmbient / CoolingHeadroom: no hardware fault
    };

    private static FidelityResult RunFidelityCondition(Condition condition, int trials, double synBias, Random rng)
    {
        double orgSum = 0, synSum = 0, orgMax = 0, synMax = 0;
        int orgFlips = 0, synFlips = 0;
        for (int i = 0; i < trials; i++)
        {
            double severity = Severity(condition, rng);
            List<RecentBucketObs> recent = BuildRecent(condition, severity, rng,
                out double soakRecent, out double coolRecent, out double soakBase, out double coolBase);

            // Score the SAME recent load against three baselines: the true one, an organically
            // acquired one, and a workout-acquired one. Error is measured against the true one,
            // so the question is whether the workout stays as faithful as organic calibration.
            double perfect = ScoreAgainst(recent, soakRecent, coolRecent, soakBase, coolBase,
                BuildBaselineRows(Acquisition.Perfect, synBias, rng), out ThermalCause cp).Value;
            double organic = ScoreAgainst(recent, soakRecent, coolRecent, soakBase, coolBase,
                BuildBaselineRows(Acquisition.Organic, synBias, rng), out ThermalCause co).Value;
            double synthetic = ScoreAgainst(recent, soakRecent, coolRecent, soakBase, coolBase,
                BuildBaselineRows(Acquisition.Synthetic, synBias, rng), out ThermalCause cs).Value;

            double eo = Math.Abs(organic - perfect), es = Math.Abs(synthetic - perfect);
            orgSum += eo; synSum += es;
            orgMax = Math.Max(orgMax, eo); synMax = Math.Max(synMax, es);
            if (CauseClass(co) != CauseClass(cp)) orgFlips++;
            if (CauseClass(cs) != CauseClass(cp)) synFlips++;
        }
        return new FidelityResult(condition, trials, orgSum / trials, synSum / trials, orgMax, synMax, orgFlips, synFlips);
    }

    // ===================== Phase 3: per-cell power-state contamination =====================
    // A single load bucket is often learned across TWO power regimes — CPU boost/turbo ON and
    // OFF, or two power-plan limits — because the machine oscillates between them in normal use.
    // Blended into one cell, the baseline's mean power and mean rise sit BETWEEN the regimes;
    // and because thermal resistance drifts with operating point (LeakageK), the power-normalizer
    // cannot perfectly recover a recent reading taken at one extreme. This measures whether that
    // contamination misleads the score of a HEALTHY machine, and whether keeping the regimes in
    // SEPARATE power-tagged cells (so scoring compares against the regime the reading is in)
    // removes it. The tagged path is what per-cell power-state tagging buys.

    /// <summary>Per-condition contamination tally: how far a blended (mixed-regime) baseline and
    /// a power-tagged baseline move a healthy machine's score from the ideal, and how often each
    /// invents a fault that isn't there.</summary>
    public sealed record ContaminationResult(
        int Trials,
        double BlendedMeanAbsErr, double TaggedMeanAbsErr,
        double BlendedMaxAbsErr, double TaggedMaxAbsErr,
        int BlendedFalseFaults, int TaggedFalseFaults,
        double BlendedSignedErr, double TaggedSignedErr, int ReferenceFalseFaults);

    // The two regimes a bucket is learned across, as a fraction of the cell's canonical power.
    // A mobile CPU's boost-off package power is well under half its boosting draw, so a 1.0 / 0.6
    // split (blended mean 0.8) is a realistic, not extreme, oscillation.
    public const double BoostOnFactor = 1.0;
    public const double BoostOffFactor = 0.6;
    private const int RegimeSamplesPerCell = 20;

    /// <summary>Score a HEALTHY machine currently running with boost ON against three baselines:
    /// one learned at the same (boost-on) regime, one blended across both regimes, and one that
    /// keeps only the matching (boost-on) power-tagged cell. Error is measured against the
    /// same-regime reference, so it isolates exactly the contamination the blend introduces.</summary>
    public static ContaminationResult RunPowerContamination(int seed = 20260717, int trials = 400)
    {
        var rng = new Random(seed);
        double blendSum = 0, tagSum = 0, blendMax = 0, tagMax = 0, blendSigned = 0, tagSigned = 0;
        int blendFalse = 0, tagFalse = 0, refFalse = 0;
        for (int i = 0; i < trials; i++)
        {
            List<RecentBucketObs> recent = BuildHealthyRecentAtRegime(BoostOnFactor, rng);

            // Reference: a baseline learned at the SAME regime the reading is in. Rate signals are
            // disabled (baseline rates 0) so this isolates the rise/power cell contamination — the
            // only thing per-cell power tagging changes; soak/cooldown are separate event signals.
            List<BaselineBucket> reference = BuildRegimeBaseline(new[] { BoostOnFactor }, rng);
            List<BaselineBucket> blended = BuildRegimeBaseline(new[] { BoostOffFactor, BoostOnFactor }, rng);
            // Tagged = exactly what the real pipeline persists for a multi-modal bucket: the
            // blended cell PLUS a power-tagged sub-cell per regime. Scoring's nearest-power match
            // is what has to pick the boost-on sub-cell here, so this exercises the real engine.
            List<BaselineBucket> tagged = BuildTaggedBaseline(rng);

            double sr = ScoreHealthy(recent, reference, out ThermalCause cr);
            double sb = ScoreHealthy(recent, blended, out ThermalCause cb);
            double st = ScoreHealthy(recent, tagged, out ThermalCause ct);

            double eb = Math.Abs(sb - sr), et = Math.Abs(st - sr);
            blendSum += eb; tagSum += et;
            blendSigned += sb - sr; tagSigned += st - sr;
            blendMax = Math.Max(blendMax, eb); tagMax = Math.Max(tagMax, et);
            if (CauseClass(cb) != 0 || sb < 70) blendFalse++;
            if (CauseClass(ct) != 0 || st < 70) tagFalse++;
            if (CauseClass(cr) != 0 || sr < 70) refFalse++;
        }
        return new ContaminationResult(trials, blendSum / trials, tagSum / trials, blendMax, tagMax,
            blendFalse, tagFalse, blendSigned / trials, tagSigned / trials, refFalse);
    }

    private static double ScoreHealthy(List<RecentBucketObs> recent, List<BaselineBucket> baseline, out ThermalCause primary) =>
        ScoreAgainst(recent, 0, 0, 0, 0, baseline, out primary).Value;

    /// <summary>A healthy machine's recent telemetry at a given power regime (fraction of each
    /// cell's canonical power). Fan scales with power the way a real fan curve does, so a boost-on
    /// reading spins faster — the same coupling the baseline learns.</summary>
    private static List<RecentBucketObs> BuildHealthyRecentAtRegime(double regime, Random rng)
    {
        var recent = new List<RecentBucketObs>();
        foreach (Cell c in Baseline)
        {
            double power = c.PowerW * regime;
            double delta = TrueDelta(c, power) + Gauss(rng, 0.6);
            double fan = Math.Max(0, c.FanRpm * (1 + 0.4 * (power / c.PowerW - 1)) + Gauss(rng, 60));
            power = Math.Max(1, power + Gauss(rng, power * 0.03));
            double temp = AmbientC + delta;
            recent.Add(new RecentBucketObs(
                c.Bucket, Warm, 60, delta, temp, temp + 4, fan, 0,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : 10 + Gauss(rng, 0.5), PowerAvg: power));
        }
        return recent;
    }

    /// <summary>What the real pipeline stores for a bucket seen across both regimes: the blended
    /// cell plus one power-tagged sub-cell per regime (loaded buckets only, as BuildPowerSubcells
    /// emits). Scoring's nearest-power match must pick the sub-cell matching the reading.</summary>
    private static List<BaselineBucket> BuildTaggedBaseline(Random rng)
    {
        var cells = new List<BaselineBucket>(BuildRegimeBaseline(new[] { BoostOffFactor, BoostOnFactor }, rng));
        foreach (double regime in new[] { BoostOffFactor, BoostOnFactor })
            foreach (BaselineBucket sub in BuildRegimeBaseline(new[] { regime }, rng))
                if (sub.Bucket is LoadBucket.Medium or LoadBucket.Heavy or LoadBucket.Max)
                    cells.Add(sub with { IsPowerSubcell = true });
        return cells;
    }

    /// <summary>A baseline whose loaded cells are learned across the given power regimes, blended
    /// into one cell each (the mean power, rise and fan of all their minutes). One regime =
    /// a clean single-regime cell; two regimes = the mixed-power contamination.</summary>
    private static List<BaselineBucket> BuildRegimeBaseline(double[] regimes, Random rng)
    {
        var rows = new List<BaselineBucket>();
        foreach (Cell c in Baseline)
        {
            double dSum = 0, pSum = 0, fSum = 0, gSum = 0; int n = 0;
            foreach (double regime in regimes)
                for (int i = 0; i < RegimeSamplesPerCell; i++)
                {
                    double power = Math.Max(1, c.PowerW * regime * (1 + Gauss(rng, OrganicPowerSpread)));
                    double delta = TrueDelta(c, power) + Gauss(rng, 0.6);
                    double fan = Math.Max(0, c.FanRpm * (1 + 0.4 * (power / c.PowerW - 1)) + Gauss(rng, 60));
                    dSum += delta; pSum += power; fSum += fan; gSum += 10 + Gauss(rng, 0.5); n++;
                }
            double dAvg = dSum / n, pAvg = pSum / n, fAvg = fSum / n, gAvg = gSum / n;
            rows.Add(new BaselineBucket(
                c.Bucket, Warm, dAvg, dAvg + 3, fAvg, n * 5, AmbientC + dAvg,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : gAvg, PowerAvg: pAvg));
        }
        return rows;
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

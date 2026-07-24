using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

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
    PowerCapDeep,// a confounder: a hard frequency cap / deep power limit (e.g. a CPU locked
                 // to 1.5 GHz) cuts loaded package power 60–75% — far beyond the power
                 // normalizer's clamp band. Everything downstream (rise, soak, cooldown,
                 // fan speed, hotspot gap) legitimately shrinks with it; none of it may
                 // read as a fault, on ANY aspect.
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
        Condition.PowerCapDeep => 0.60 + rng.NextDouble() * 0.15, // −60..75 % power under load
        _ => 0,
    };

    /// <summary>How strongly a temperature-targeting fan curve answers a power change:
    /// the fraction of a fractional power change that shows up in rpm. The same coupling
    /// the contamination/toggle benchmarks model, now applied to every power confounder,
    /// because a real fan slows when the die cools — a benchmark whose fans ignore the
    /// watts cannot see the engine mistaking that slowdown for a failing fan.</summary>
    private const double FanCurveCoupling = 0.4;

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
                // The gap scales with power too: hotspot−edge is heat flux × internal
                // resistance, so more watts widen a HEALTHY card's gap and fewer narrow it.
                // And the fan curve answers the die temperature, so it rides the same watts.
                // Both couplings must be modelled, or the benchmark can't see the engine
                // blaming a power knob on the mount or on a failing fan.
                case Condition.Overclock:
                    power *= 1 + severity;                       // more watts → proportionally hotter
                    delta *= 1 + severity;
                    fan *= 1 + FanCurveCoupling * severity;
                    gap *= 1 + severity;
                    sRec = soakBase * (1 + severity);
                    cRec = coolBase * (1 + severity);
                    break;
                case Condition.Undervolt:
                    power *= 1 - severity;
                    delta *= 1 - severity;
                    fan *= 1 - FanCurveCoupling * severity;
                    gap *= 1 - severity;
                    sRec = soakBase * (1 - severity);
                    cRec = coolBase * (1 - severity);
                    break;
                case Condition.PowerCapDeep:
                    // The cap binds under load; an idle die already ran at low clocks.
                    if (c.Bucket != LoadBucket.Idle)
                    {
                        power *= 1 - severity;
                        delta *= 1 - severity;
                        fan *= 1 - FanCurveCoupling * severity;
                        gap *= 1 - severity;
                        sRec = soakBase * (1 - severity);
                        cRec = coolBase * (1 - severity);
                    }
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

    // ===================== Phase 4: constant boost-mode toggling =====================
    // Phase 3 proves a PURE boost-on window is judged fairly. A user who flips boost on and
    // off constantly produces something harsher: the recent window itself is a MIX of the two
    // regimes whose blend fraction wanders week to week (the aggregates blend every minute in
    // the window into one row per bucket), and the soak/cooldown rates ride the same watts.
    // Bulletproof means the verdict barely moves however the toggle lands: no false fault at
    // any mix, and the score spread across every possible mix stays within noise.

    /// <summary>One pipeline configuration's tally under boost toggling: score error against a
    /// like-for-like reference, false faults, and the score SPREAD (max−min of the mean score
    /// as the boost mix sweeps 0→100%) — the "fluctuating verdict" a toggling user experiences.</summary>
    public sealed record ToggleConfigResult(
        string Config, double MeanAbsErr, double MaxAbsErr, double SignedErr, int FalseFaults, double ScoreSpread);

    public sealed record ToggleResult(int Trials, int ReferenceFalseFaults, double ReferenceSpread,
        IReadOnlyList<ToggleConfigResult> Configs);

    /// <summary>Fraction of the learning epoch's minutes spent at boost-on. 50/50 is the honest
    /// worst case for a habitual toggler: the blended cell sits exactly between the regimes.</summary>
    public const double BaselineBoostMix = 0.5;

    // Canonical soak/cooldown rates at full (boost-on) power; both scale linearly with watts
    // (dT/dt ≈ P/C, and a cooldown starts from a die temperature that itself scales with P).
    // 20/22 °C/min at the canonical power matches the main benchmark's healthy machine.
    private const double SoakRate0 = 20;
    private const double CoolRate0 = 22;

    /// <summary>Package-power factor of a window that spent fraction <paramref name="phi"/> of
    /// its minutes at boost-on and the rest at boost-off.</summary>
    private static double MixFactor(double phi) => BoostOffFactor + (BoostOnFactor - BoostOffFactor) * phi;

    /// <summary>Score a HEALTHY machine whose recent window toggles between boost regimes, per
    /// trial at a random mix, through the three real pipeline shapes: the blended baseline
    /// (pre-tagging), the power-tagged baseline with today's blended recent rows, and the
    /// power-tagged baseline with regime-split recent rows. Error is measured against a
    /// like-for-like reference (a baseline learned at the very same mix), so it isolates
    /// exactly what the toggle contamination costs each configuration.</summary>
    public static ToggleResult RunBoostToggle(int seed = 20260716, int trials = 400)
    {
        var rng = new Random(seed);
        const int configs = 3;
        double[] sum = new double[configs], max = new double[configs], signed = new double[configs];
        int[] falseFaults = new int[configs];
        int refFalse = 0;
        Span<double> s = stackalloc double[configs];

        for (int i = 0; i < trials; i++)
        {
            double phi = rng.NextDouble(); // this week's boost-on fraction, anywhere from 0 to 1
            List<RecentBucketObs> mixedRecent = BuildHealthyRecentMixed(phi, rng);
            List<RecentBucketObs> splitRecent = BuildHealthyRecentSplit(phi, rng);
            double soakRecent = SoakRate0 * MixFactor(phi) + Gauss(rng, 0.5);
            double coolRecent = CoolRate0 * MixFactor(phi) + Gauss(rng, 0.5);
            // The epoch learned its rate baselines across the same 50/50 toggling it learned
            // its cells at; the reference is judged with rate baselines at the trial's own mix.
            double soakBase = SoakRate0 * MixFactor(BaselineBoostMix);
            double coolBase = CoolRate0 * MixFactor(BaselineBoostMix);

            List<BaselineBucket> blended = BuildRegimeBaseline(new[] { BoostOffFactor, BoostOnFactor }, rng);
            List<BaselineBucket> tagged = BuildTaggedBaseline(rng);
            List<BaselineBucket> reference = BuildMixBaseline(phi, rng);

            double sr = ScoreAgainst(mixedRecent, soakRecent, coolRecent,
                SoakRate0 * MixFactor(phi), CoolRate0 * MixFactor(phi), reference, out ThermalCause cr).Value;
            ThermalCause[] causes = new ThermalCause[configs];
            s[0] = ScoreAgainst(mixedRecent, soakRecent, coolRecent, soakBase, coolBase, blended, out causes[0]).Value;
            s[1] = ScoreAgainst(mixedRecent, soakRecent, coolRecent, soakBase, coolBase, tagged, out causes[1]).Value;
            s[2] = ScoreAgainst(splitRecent, soakRecent, coolRecent, soakBase, coolBase, tagged, out causes[2]).Value;

            if (CauseClass(cr) != 0 || sr < 70) refFalse++;
            for (int c = 0; c < configs; c++)
            {
                double e = Math.Abs(s[c] - sr);
                sum[c] += e; signed[c] += s[c] - sr; max[c] = Math.Max(max[c], e);
                if (CauseClass(causes[c]) != 0 || s[c] < 70) falseFaults[c]++;
            }
        }

        (double[] spreads, double refSpread) = SweepToggleSpread(seed + 1);
        string[] names = { "blended (pre-tag)", "tagged + blended recent", "tagged + split recent" };
        var results = new List<ToggleConfigResult>(configs);
        for (int c = 0; c < configs; c++)
            results.Add(new ToggleConfigResult(names[c], sum[c] / trials, max[c], signed[c] / trials, falseFaults[c], spreads[c]));
        return new ToggleResult(trials, refFalse, refSpread, results);
    }

    /// <summary>Sweep the boost mix 0→100% against FIXED baselines (one machine, many weeks of
    /// different toggle habits) and measure how far the mean score moves — the fluctuation a
    /// toggling user actually watches on the dial. The reference sweep (a like-for-like baseline
    /// at every mix) is the noise floor of the measurement itself.</summary>
    private static (double[] Spreads, double ReferenceSpread) SweepToggleSpread(int seed)
    {
        var baseRng = new Random(seed);
        List<BaselineBucket> blended = BuildRegimeBaseline(new[] { BoostOffFactor, BoostOnFactor }, baseRng);
        List<BaselineBucket> tagged = BuildTaggedBaseline(baseRng);
        double soakBase = SoakRate0 * MixFactor(BaselineBoostMix);
        double coolBase = CoolRate0 * MixFactor(BaselineBoostMix);
        const int perPoint = 40;

        double[] min = { double.MaxValue, double.MaxValue, double.MaxValue }, max = { 0, 0, 0 };
        double refMin = double.MaxValue, refMax = 0;
        var rng = new Random(seed + 7);
        Span<double> mean = stackalloc double[3];
        for (double phi = 0; phi <= 1.0001; phi += 0.1)
        {
            mean.Clear();
            double refMean = 0;
            for (int i = 0; i < perPoint; i++)
            {
                List<RecentBucketObs> mixed = BuildHealthyRecentMixed(phi, rng);
                List<RecentBucketObs> split = BuildHealthyRecentSplit(phi, rng);
                double soakRecent = SoakRate0 * MixFactor(phi) + Gauss(rng, 0.5);
                double coolRecent = CoolRate0 * MixFactor(phi) + Gauss(rng, 0.5);
                mean[0] += ScoreAgainst(mixed, soakRecent, coolRecent, soakBase, coolBase, blended, out _).Value;
                mean[1] += ScoreAgainst(mixed, soakRecent, coolRecent, soakBase, coolBase, tagged, out _).Value;
                mean[2] += ScoreAgainst(split, soakRecent, coolRecent, soakBase, coolBase, tagged, out _).Value;
                refMean += ScoreAgainst(mixed, soakRecent, coolRecent,
                    SoakRate0 * MixFactor(phi), CoolRate0 * MixFactor(phi), BuildMixBaseline(phi, rng), out _).Value;
            }
            for (int c = 0; c < 3; c++)
            {
                double m = mean[c] / perPoint;
                min[c] = Math.Min(min[c], m); max[c] = Math.Max(max[c], m);
            }
            refMean /= perPoint;
            refMin = Math.Min(refMin, refMean); refMax = Math.Max(refMax, refMean);
        }
        return (new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] }, refMax - refMin);
    }

    /// <summary>Recent telemetry of a healthy machine whose window toggled between regimes, as
    /// TODAY'S aggregate pipeline reports it: one row per bucket blending every minute (SUM/SUM),
    /// so the row's rise, fan and watts are the minutes-weighted mixture means.</summary>
    private static List<RecentBucketObs> BuildHealthyRecentMixed(double phi, Random rng)
    {
        var recent = new List<RecentBucketObs>();
        foreach (Cell c in Baseline)
        {
            double pOn = c.PowerW * BoostOnFactor, pOff = c.PowerW * BoostOffFactor;
            double delta = phi * TrueDelta(c, pOn) + (1 - phi) * TrueDelta(c, pOff) + Gauss(rng, 0.6);
            double power = phi * pOn + (1 - phi) * pOff;
            double fan = Math.Max(0, c.FanRpm * (1 + 0.4 * (power / c.PowerW - 1)) + Gauss(rng, 60));
            power = Math.Max(1, power + Gauss(rng, power * 0.03));
            double temp = AmbientC + delta;
            recent.Add(new RecentBucketObs(
                c.Bucket, Warm, 60, delta, temp, temp + 4, fan, 0,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : 10 + Gauss(rng, 0.5), PowerAvg: power));
        }
        return recent;
    }

    /// <summary>The same toggled window as regime-SPLIT recent rows: one regime-pure row per
    /// power band (what splitting the recent aggregates by power band produces), falling back to
    /// the blended row when either side is too thin to judge — the same gate the engine's
    /// per-bucket minimum imposes, so a split never throws away a comparable bucket.</summary>
    private static List<RecentBucketObs> BuildHealthyRecentSplit(double phi, Random rng)
    {
        var recent = new List<RecentBucketObs>();
        foreach (Cell c in Baseline)
        {
            int onMinutes = (int)Math.Round(60 * phi), offMinutes = 60 - onMinutes;
            bool loaded = c.Bucket is LoadBucket.Medium or LoadBucket.Heavy or LoadBucket.Max;
            if (!loaded || onMinutes < ScoringEngine.MinMinutes(c.Bucket) || offMinutes < ScoringEngine.MinMinutes(c.Bucket))
            {
                // Too lopsided to split (or idle): the pipeline keeps the blended row.
                recent.AddRange(BuildHealthyRecentMixed(phi, rng).Where(r => r.Bucket == c.Bucket));
                continue;
            }
            foreach ((double regime, int minutes) in new[] { (BoostOnFactor, onMinutes), (BoostOffFactor, offMinutes) })
            {
                double power = c.PowerW * regime;
                double delta = TrueDelta(c, power) + Gauss(rng, 0.6);
                double fan = Math.Max(0, c.FanRpm * (1 + 0.4 * (power / c.PowerW - 1)) + Gauss(rng, 60));
                power = Math.Max(1, power + Gauss(rng, power * 0.03));
                double temp = AmbientC + delta;
                recent.Add(new RecentBucketObs(
                    c.Bucket, Warm, minutes, delta, temp, temp + 4, fan, 0,
                    GapAvg: 10 + Gauss(rng, 0.5), PowerAvg: power));
            }
        }
        return recent;
    }

    /// <summary>The like-for-like ideal for a toggled window: a baseline whose cells were learned
    /// at the very same boost mix. What a perfectly matched reference would compare against.</summary>
    private static List<BaselineBucket> BuildMixBaseline(double phi, Random rng)
    {
        var rows = new List<BaselineBucket>();
        foreach (Cell c in Baseline)
        {
            double dSum = 0, pSum = 0, fSum = 0, gSum = 0; int n = 0;
            for (int i = 0; i < RegimeSamplesPerCell * 2; i++)
            {
                double regime = i < RegimeSamplesPerCell * 2 * phi ? BoostOnFactor : BoostOffFactor;
                double power = Math.Max(1, c.PowerW * regime * (1 + Gauss(rng, OrganicPowerSpread)));
                double delta = TrueDelta(c, power) + Gauss(rng, 0.6);
                double fan = Math.Max(0, c.FanRpm * (1 + 0.4 * (power / c.PowerW - 1)) + Gauss(rng, 60));
                dSum += delta; pSum += power; fSum += fan; gSum += 10 + Gauss(rng, 0.5); n++;
            }
            double dAvg = dSum / n;
            rows.Add(new BaselineBucket(
                c.Bucket, Warm, dAvg, dAvg + 3, fSum / n, n * 5, AmbientC + dAvg,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : gSum / n, PowerAvg: pSum / n));
        }
        return rows;
    }

    // ===================== Phase 6: battery-contaminated rate events =====================
    // The rise/baseline comparison is AC-only end to end (on_ac is a primary-key dimension of
    // the aggregates and ScoreCoordinator filters both windows), but the soak/cooldown RATES
    // come from the events table, which historically had no AC awareness. Battery power limits
    // throttle the watts, both rates ride the watts (dT/dt ≈ P/C), and the power normalizer
    // CANNOT rescue this one: its power means come from the AC-filtered cells, so the battery
    // watt drop is structurally invisible to it. The failure mode is a habit change (a travel
    // week on battery against a plugged-in baseline): the recent cooldown mean drags down and
    // reads "sheds heat slower", one of the paste tells. This measures what that costs through
    // the real engine, for the pipeline that blends battery events into the mean (pre-fix) and
    // the pipeline that feeds AC-only rates (what the repository now guarantees).

    public sealed record BatteryRateResult(
        int Trials,
        double ContaminatedMeanErr, double ContaminatedMaxErr, int ContaminatedFalseFaults,
        double FilteredMeanErr, double FilteredMaxErr, int FilteredFalseFaults,
        int ReferenceFalseFaults);

    /// <summary>Package power on battery as a fraction of AC power: mobile firmware limits
    /// commonly halve to third the sustained wattage the moment the charger comes out.</summary>
    public const double BatteryPowerFactor = 0.35;

    public static BatteryRateResult RunBatteryRates(int seed = 20260716, int trials = 400)
    {
        var rng = new Random(seed);
        double contSum = 0, contMax = 0, filtSum = 0, filtMax = 0;
        int contFalse = 0, filtFalse = 0, refFalse = 0;

        for (int i = 0; i < trials; i++)
        {
            // A healthy, plugged-in-learned machine whose recent week ran some fraction of
            // its load edges on battery. The rise cells stay AC-clean (the aggregate pipeline
            // guarantees that); only the event-derived rates carry the battery sessions.
            double phi = rng.NextDouble(); // fraction of the week's soak/cooldown edges on battery
            List<RecentBucketObs> recent = BuildRecent(Condition.Healthy, 0, rng,
                out _, out _, out double soakBase, out double coolBase);

            double batteryMix = 1 - phi + phi * BatteryPowerFactor;
            double soakContaminated = soakBase * batteryMix + Gauss(rng, 0.5);
            double coolContaminated = coolBase * batteryMix + Gauss(rng, 0.5);
            double soakClean = soakBase + Gauss(rng, 0.5);
            double coolClean = coolBase + Gauss(rng, 0.5);

            List<BaselineBucket> baseline = BuildBaselineRows(Acquisition.Perfect, 0, rng);
            ComponentScore reference = ScoreAgainst(recent, soakBase, coolBase, soakBase, coolBase,
                baseline, out _);

            ComponentScore contaminated = ScoreAgainst(recent, soakContaminated, coolContaminated,
                soakBase, coolBase, baseline, out _);
            ComponentScore filtered = ScoreAgainst(recent, soakClean, coolClean,
                soakBase, coolBase, baseline, out _);

            double ec = Math.Abs(contaminated.Value - reference.Value), ef = Math.Abs(filtered.Value - reference.Value);
            contSum += ec; filtSum += ef;
            contMax = Math.Max(contMax, ec); filtMax = Math.Max(filtMax, ef);
            if (AnyFaultFinding(contaminated) || contaminated.Value < 85) contFalse++;
            if (AnyFaultFinding(filtered) || filtered.Value < 85) filtFalse++;
            if (AnyFaultFinding(reference) || reference.Value < 85) refFalse++;
        }

        return new BatteryRateResult(trials, contSum / trials, contMax, contFalse,
            filtSum / trials, filtMax, filtFalse, refFalse);
    }

    // ===================== Phase 7: fixed-indoor ambient regime separation =====================
    // DeltaT can score rise against either the outside weather (mode 0) or a user-set fixed indoor
    // temperature (mode 1). The two measure the die against DIFFERENT references, so their rises are
    // not comparable: if a fixed-indoor reading (rise over the set indoor temp) is judged against a
    // weather-mode baseline (rise over the true outside temp), the whole rise shifts by the gap
    // between the two references and reads as a broad thermal fault on a perfectly healthy machine.
    // The mode dimension keeps the two regimes' baselines entirely separate so this can never
    // happen. This phase measures both the harm (scoring fixed-mode data against the weather
    // baseline, the bug a mode-blind pipeline would have) and the fix (scoring it against the
    // fixed-mode baseline, what the shipped pipeline guarantees), so the separation is a measured
    // property and not a claim.

    public sealed record AmbientRegimeResult(
        int Trials,
        // Fixed-mode reading judged against its OWN fixed-mode baseline (the shipped behaviour).
        double SeparatedMeanScore, int SeparatedFalseFaults,
        // The same reading judged against the WEATHER baseline (the mode-blind bug).
        double CrossModeMeanScore, int CrossModeFalseFaults);

    /// <summary>A healthy machine scored in fixed-indoor mode, where the user's set indoor
    /// temperature sits a few degrees below the true ambient the die actually sees (an AC set-point
    /// that undershoots the room, both within one ambient band). Its recorded rise is therefore
    /// inflated by that gap. Scored against a matching fixed-mode baseline the inflation cancels
    /// (both sides carry it); scored against a weather-mode baseline it does not, and the gap reads
    /// as a fault. Reference is the separated score, so the cross-mode error is exactly the
    /// contamination the mode separation removes.</summary>
    public static AmbientRegimeResult RunAmbientRegime(int seed = 20260721, int trials = 400)
    {
        var rng = new Random(seed);
        double sepScoreSum = 0, crossScoreSum = 0;
        int sepFalse = 0, crossFalse = 0;
        for (int i = 0; i < trials; i++)
        {
            // Gap between the true ambient and the user's fixed set-point, a few degrees, kept
            // inside one band so the weather baseline's cells actually line up with the reading.
            double offset = 3 + rng.NextDouble() * 3; // +3..6 °C of inflated rise
            List<RecentBucketObs> recent = BuildFixedIndoorRecent(offset, rng);
            List<BaselineBucket> fixedBaseline = BuildFixedIndoorBaseline(offset, rng);
            List<BaselineBucket> weatherBaseline = BuildFixedIndoorBaseline(0, rng);

            double sep = ScoreHealthy(recent, fixedBaseline, out ThermalCause cs);
            double cross = ScoreHealthy(recent, weatherBaseline, out ThermalCause cc);

            sepScoreSum += sep;
            crossScoreSum += cross;
            if (CauseClass(cs) != 0 || sep < 85) sepFalse++;
            if (CauseClass(cc) != 0 || cross < 85) crossFalse++;
        }
        return new AmbientRegimeResult(trials, sepScoreSum / trials, sepFalse, crossScoreSum / trials, crossFalse);
    }

    /// <summary>Healthy recent telemetry in fixed-indoor mode: the recorded rise is the true rise
    /// plus <paramref name="offset"/> (the die measured against a fixed reference that sits that far
    /// below the true ambient). Absolute die temperature stays physically correct.</summary>
    private static List<RecentBucketObs> BuildFixedIndoorRecent(double offset, Random rng)
    {
        const double trueAmbient = 30; // Warm band
        var recent = new List<RecentBucketObs>();
        foreach (Cell c in Baseline)
        {
            double trueRise = c.Delta + Gauss(rng, 0.6);
            double recordedDelta = trueRise + offset;             // rise over the fixed indoor reference
            double dieTemp = trueAmbient + trueRise;              // = fixedRef + recordedDelta
            double power = Math.Max(1, c.PowerW + Gauss(rng, c.PowerW * 0.03));
            double fan = Math.Max(0, c.FanRpm + Gauss(rng, 60));
            recent.Add(new RecentBucketObs(
                c.Bucket, Warm, 60, recordedDelta, dieTemp, dieTemp + 4, fan, 0,
                GapAvg: c.Bucket == LoadBucket.Idle ? null : 10 + Gauss(rng, 0.5), PowerAvg: power));
        }
        return recent;
    }

    /// <summary>A baseline learned at a reference sitting <paramref name="offset"/>° below the true
    /// ambient (offset 0 = the weather baseline learned at the true ambient). Same band and same
    /// absolute die temperature either way; only the recorded rise carries the offset.</summary>
    private static List<BaselineBucket> BuildFixedIndoorBaseline(double offset, Random rng)
    {
        const double trueAmbient = 30;
        var rows = new List<BaselineBucket>();
        foreach (Cell c in Baseline)
        {
            double dSum = 0, pSum = 0, fSum = 0, gSum = 0; const int n = 30;
            for (int i = 0; i < n; i++)
            {
                double trueRise = c.Delta + Gauss(rng, 0.6);
                dSum += trueRise + offset;
                pSum += Math.Max(1, c.PowerW + Gauss(rng, c.PowerW * 0.03));
                fSum += Math.Max(0, c.FanRpm + Gauss(rng, 60));
                gSum += 10 + Gauss(rng, 0.5);
            }
            double dAvg = dSum / n;
            rows.Add(new BaselineBucket(
                c.Bucket, Warm, dAvg, dAvg + 3, fSum / n, n * 5, trueAmbient + (dAvg - offset),
                GapAvg: c.Bucket == LoadBucket.Idle ? null : gSum / n, PowerAvg: pSum / n));
        }
        return rows;
    }

    // ===================== Phase 5: deep power caps & power-coupled faults =====================
    // The headline benchmark judges the PRIMARY cause and a score<70 line. That is too coarse for
    // the promise "a power knob never reads as a fault on ANY aspect": a false FanFault can sit at
    // rank 2 behind a PowerConfig reassurance, a false cooldown penalty can cost 12 points without
    // crossing 70, and an aspect cell can read Watch while the verdict stays Good. This suite holds
    // the strict bar for the harshest realistic power moves (a hard frequency cap, a deep power
    // limit), and proves the power-awareness does not BLIND the engine: a genuinely failing fan on
    // a capped machine and genuine pump-out on an undervolted card must still be named.

    public sealed record PowerCapResult(
        int Trials,
        // Healthy machine under a deep cap (power −60..75% under load): the strict bar.
        int HealthyFaultFindings,   // ANY Paste/Airflow/FanFault/Mount finding surfaced, any rank
        int HealthyBelow85,         // verdict left Excellent (false points crept in)
        int HealthyAspectNotClear,  // any measurable fault aspect cell below Clear (<85)
        double HealthyMeanScore,
        // Detection retained under power confounders.
        int FanFaultUnderCapNamed,  // failing fan on a deeply capped machine: FanFault surfaced
        int PumpoutUnderUndervoltNamed); // pump-out on an undervolted card: Mount surfaced

    private static bool AnyFaultFinding(ComponentScore s) =>
        s.Diagnosis is { } d && d.Findings.Any(f => f.Cause is ThermalCause.Paste
            or ThermalCause.Airflow or ThermalCause.FanFault or ThermalCause.Mount);

    private static bool AnyFaultAspectBelowClear(ComponentScore s) =>
        s.Aspects.Any(a => a.Aspect is HealthAspect.Paste or HealthAspect.Airflow
            or HealthAspect.Fans or HealthAspect.Mount && a.Score is { } v && v < 85);

    public static PowerCapResult RunPowerCapSuite(int seed = 20260716, int trials = 400)
    {
        var rng = new Random(seed);
        int healthyFault = 0, healthyBelow85 = 0, healthyAspect = 0, fanNamed = 0, pumpNamed = 0;
        double healthyScoreSum = 0;

        for (int i = 0; i < trials; i++)
        {
            // 1) Healthy machine, deep cap. Nothing may read as a fault anywhere.
            double capSev = Severity(Condition.PowerCapDeep, rng);
            List<RecentBucketObs> capped = BuildRecent(Condition.PowerCapDeep, capSev, rng,
                out double soakR, out double coolR, out double soakB, out double coolB);
            ComponentScore healthy = ScoreAgainst(capped, soakR, coolR, soakB, coolB,
                BuildBaselineRows(Acquisition.Perfect, 0, rng), out _);
            healthyScoreSum += healthy.Value;
            if (AnyFaultFinding(healthy)) healthyFault++;
            if (healthy.Scored && healthy.Value < 85) healthyBelow85++;
            if (AnyFaultAspectBelowClear(healthy)) healthyAspect++;

            // 2) The same deep cap, but the fan is GENUINELY failing on top of it (well below
            // even what the reduced watts explain). The hint must still surface.
            capSev = Severity(Condition.PowerCapDeep, rng);
            List<RecentBucketObs> cappedFanFault = BuildRecent(Condition.PowerCapDeep, capSev, rng,
                out soakR, out coolR, out soakB, out coolB)
                .Select(r => r with { FanAvg = r.FanAvg * 0.55 }).ToList();
            ComponentScore fanScore = ScoreAgainst(cappedFanFault, soakR, coolR, soakB, coolB,
                BuildBaselineRows(Acquisition.Perfect, 0, rng), out _);
            if (fanScore.Diagnosis is { } fd && fd.Findings.Any(f => f.Cause == ThermalCause.FanFault))
                fanNamed++;

            // 3) An undervolted card (power within the normalizer's range) whose paste has
            // genuinely pumped out: the gap is double what the reduced watts predict. The
            // narrower absolute gap must not hide it.
            double uvSev = Severity(Condition.Undervolt, rng);
            List<RecentBucketObs> uvPumpout = BuildRecent(Condition.Undervolt, uvSev, rng,
                out soakR, out coolR, out soakB, out coolB)
                .Select(r => r with { GapAvg = r.GapAvg * 2.0 }).ToList();
            ComponentScore uvScore = ScoreAgainst(uvPumpout, soakR, coolR, soakB, coolB,
                BuildBaselineRows(Acquisition.Perfect, 0, rng), out _);
            if (uvScore.Diagnosis is { } ud && ud.Findings.Any(f => f.Cause == ThermalCause.Mount))
                pumpNamed++;
        }

        return new PowerCapResult(trials, healthyFault, healthyBelow85, healthyAspect,
            healthyScoreSum / trials, fanNamed, pumpNamed);
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

    // ===================== Calibration confidence-gate fidelity =====================
    // The main Run() bypasses the calibration gate entirely (it hardcodes BaselineReady:true),
    // so the "stuck at 80%" failure modes had no measured floor. This exercises the REAL gate
    // (BaselineBuilder.Assess) and proves three properties in numbers: a stable machine locks
    // in a bounded number of sessions; a lone thinly-sampled cell can never veto a well-pinned
    // baseline (evidence-mass weighting); and a GPU whose watts scatter game-to-game locks once
    // its session means are power-normalized (as the gate now does), where raw ΔT leaves it
    // stuck. Seeded and deterministic. Floors are locked in DetectionBenchmarkTests.

    public sealed record CalibrationFidelityResult(
        int Trials,
        double StableLockRate,               // stable CPU: fraction that reach a lock
        double StableMedianSessionsToLock,   // how few sessions a clean machine needs
        double RareCellVetoRate,             // fraction where one thin cell blocks a ready baseline (want ~0)
        double GpuRawLockRate,               // scattered GPU judged on RAW ΔT (pre-A2): want low
        double GpuNormLockRate);             // same judged power-normalized (A2): want high

    public static CalibrationFidelityResult RunCalibrationFidelity(int seed = 20260724, int trials = 400)
    {
        var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var cured = start.AddHours(120); // past the cure floor, so only data confidence is in play
        const int band = 2;

        static BucketStat HeavyStat() =>
            new(LoadBucket.Heavy, band, true, 200, 6000, 80, 60, 95, 80, 50, null, 0, null, 50);

        bool Ready(BucketStat[] stats, Func<LoadBucket, int, IReadOnlyList<double>> means, int sessions) =>
            BaselineBuilder.Assess(start, cured, stats, means, sessions, pasteIsFresh: false).Ready;

        var rng = new Random(seed);
        int stableLocks = 0, rareVetoes = 0, gpuRawLocks = 0, gpuNormLocks = 0;
        var sessionsToLock = new List<int>();

        for (int t = 0; t < trials; t++)
        {
            // (1) Stable CPU: tight session means. Add sessions until it locks (cap 10).
            var stable = new List<double>();
            int locked = -1;
            for (int s = 1; s <= 10; s++)
            {
                stable.Add(50 + Gauss(rng, 0.4));
                if (s < BaselineBuilder.MinSessionsPerCell) continue;
                double[] arr = stable.ToArray();
                if (Ready(new[] { HeavyStat() },
                        (b, bd) => b == LoadBucket.Heavy && bd == band ? arr : Array.Empty<double>(), s))
                {
                    locked = s;
                    break;
                }
            }
            if (locked > 0)
            {
                stableLocks++;
                sessionsToLock.Add(locked);
            }

            // (2) Rare-cell poison: a well-pinned Heavy baseline plus one thin Medium/cold cell.
            double[] heavyTight = { 50.0, 50.2, 49.8, 50.1, 49.9 };
            double rareVal = 60 + Gauss(rng, 3);
            var poison = new[]
            {
                HeavyStat(),
                new BucketStat(LoadBucket.Medium, 0, true, 8, 240, 70, 55, 85, 55, 40, null, 0, null, 35),
            };
            bool poisonReady = Ready(poison,
                (b, bd) => b == LoadBucket.Heavy && bd == band ? heavyTight
                         : b == LoadBucket.Medium && bd == 0 ? new[] { rareVal }
                         : Array.Empty<double>(), 6);
            if (!poisonReady)
                rareVetoes++;

            // (3) Scattered GPU: healthy tight resistance, watts vary a lot. Raw ΔT scatters;
            // power-normalized (to the cell's mean watts) collapses to the resistance.
            const int gs = 6;
            var powers = new double[gs];
            var raw = new double[gs];
            for (int s = 0; s < gs; s++)
            {
                double p = Math.Clamp(60 + Gauss(rng, 14), 25, 110);
                double resistance = 0.9 + Gauss(rng, 0.015); // healthy, tight °C/W
                powers[s] = p;
                raw[s] = resistance * p;
            }
            double meanP = powers.Average();
            var norm = new double[gs];
            for (int s = 0; s < gs; s++)
                norm[s] = raw[s] * Math.Clamp(meanP / powers[s], 0.5, 2.0);

            var gpuStat = new[]
            {
                new BucketStat(LoadBucket.Max, band, true, 60, 1800, 80, 60, 95, 99, raw.Average(), null, 0, null, meanP),
            };
            if (Ready(gpuStat, (b, bd) => b == LoadBucket.Max && bd == band ? raw : Array.Empty<double>(), gs))
                gpuRawLocks++;
            if (Ready(gpuStat, (b, bd) => b == LoadBucket.Max && bd == band ? norm : Array.Empty<double>(), gs))
                gpuNormLocks++;
        }

        sessionsToLock.Sort();
        double median = sessionsToLock.Count == 0 ? 0 : sessionsToLock[sessionsToLock.Count / 2];
        return new CalibrationFidelityResult(
            trials,
            stableLocks / (double)trials,
            median,
            rareVetoes / (double)trials,
            gpuRawLocks / (double)trials,
            gpuNormLocks / (double)trials);
    }

    // ===================== Intel PL2 thermal-constraint disambiguation =====================
    // The absolute (day-one) signal: an Intel CPU can report, via MSR 0x64F + 0x610, that it is
    // being held below its own configured PL2 by HEAT right now. That is direct corroboration
    // that COOLING, not the power budget, is the ceiling. The hazard the feature must avoid is
    // false-alarming a machine that is deliberately power-limited (boost off / low power plan),
    // which draws far below PL2 for a reason that is NOT heat. This proves the disambiguation:
    // a thermally-pinned machine surfaces a thermal/headroom cause; a deliberately power-limited
    // one (the maintainer's own dev laptop, ~16 W where the baseline learned ~40 W, a cool die)
    // never does. Locked in DetectionBenchmarkTests.

    public sealed record Pl2DisambiguationResult(
        int Trials,
        double ConstrainedNamedThermal, // thermally-pinned CPU surfaces a thermal/headroom cause
        double ByDesignFalseFaults,     // boost-off CPU wrongly shows a cooling fault (want ~0)
        double ByDesignMeanScore);      // boost-off CPU must stay healthy

    public static Pl2DisambiguationResult RunPl2Disambiguation(int seed = 20260724, int trials = 400)
    {
        var rng = new Random(seed);
        List<BaselineBucket> baseRows = BuildBaselineRows(Acquisition.Perfect, 0, rng);

        int constrainedNamed = 0, byDesignFaults = 0;
        double byDesignScoreSum = 0;

        for (int i = 0; i < trials; i++)
        {
            // (A) A machine behaving like its own baseline, but whose CPU is thermally pinned
            // below PL2 RIGHT NOW (the MSR limiter says heat is the ceiling). The corroboration
            // must turn a would-be "healthy" read into a named thermal / headroom cause.
            var healthy = BuildRecent(Condition.Healthy, 0, rng, out double sR, out double cR, out double sB, out double cB);
            ScoreCpu(healthy, sR, cR, sB, cB, baseRows, thermallyConstrained: true, out ThermalCause primA);
            if (primA is ThermalCause.CoolingHeadroom or ThermalCause.FanFault or ThermalCause.Airflow or ThermalCause.Mount)
                constrainedNamed++;

            // (B) The dev-laptop config: deliberately boost-off / power-capped, drawing far
            // below PL2 with a cool die, so the thermal limiter is NOT asserting (flag false).
            // This must never read as a cooling fault.
            var boostOff = BuildRecent(Condition.PowerCapDeep, 0.62, rng, out double sR2, out double cR2, out double sB2, out double cB2);
            ComponentScore byDesign = ScoreCpu(boostOff, sR2, cR2, sB2, cB2, baseRows, thermallyConstrained: false, out ThermalCause primB);
            byDesignScoreSum += byDesign.Value;
            bool fault = byDesign.Value < 70 || primB is ThermalCause.Paste or ThermalCause.Airflow
                or ThermalCause.FanFault or ThermalCause.Mount;
            if (fault)
                byDesignFaults++;
        }

        return new Pl2DisambiguationResult(
            trials, constrainedNamed / (double)trials, byDesignFaults / (double)trials, byDesignScoreSum / trials);
    }

    /// <summary>Score a CPU-shaped scenario (no hotspot gap, so the mount aspect stays out) with
    /// the Intel thermal-constraint flag set as given.</summary>
    private static ComponentScore ScoreCpu(
        List<RecentBucketObs> recent, double soakRecent, double coolRecent, double soakBase, double coolBase,
        List<BaselineBucket> baseRows, bool thermallyConstrained, out ThermalCause primary)
    {
        var cpuRecent = recent.Select(r => r with { GapAvg = null }).ToList();
        var input = new ScoreInput(
            ComponentKind.Cpu, "Bench CPU", cpuRecent, baseRows,
            RecentWindowHours: 7 * 24, ThrottleEvents: 0,
            SoakRateRecent: soakRecent, SoakRateBaseline: soakBase,
            CooldownRateRecent: coolRecent, CooldownRateBaseline: coolBase,
            LimitC: LimitC, Profile: Profile, BaselineReady: true, CalibrationProgress: 1.0,
            CpuThermallyPowerConstrained: thermallyConstrained);
        ComponentScore score = ScoringEngine.Score(input, t => $"{t:0} °C");
        primary = score.Diagnosis?.Primary.Cause ?? ThermalCause.Healthy;
        return score;
    }

    private static double Gauss(Random rng, double sd)
    {
        if (sd <= 0) return 0;
        double u1 = 1 - rng.NextDouble(), u2 = 1 - rng.NextDouble();
        return sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

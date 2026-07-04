using Kelvin.Core.Monitoring;

namespace Kelvin.Core.Scoring;

/// <summary>Turns telemetry into a 0–100 paste-health score. Pure function of its
/// input — no clocks, no I/O — so every rule here is unit-testable.
///
/// The philosophy: absolute temperatures are weather; *changes against this
/// machine's own ambient-corrected baseline* are paste. A CPU that always ran
/// 93 °C under heavy load in hot weather is healthy; one that used to run 85 °C
/// in the same weather and now runs 93 °C is drying out.</summary>
public static class ScoringEngine
{
    // --- tunables, in one place -------------------------------------------

    /// <summary>°C of excess delta that we forgive as noise.</summary>
    public const double ExcessDeadbandC = 0.8;

    /// <summary>Points lost per °C the weighted delta exceeds baseline.</summary>
    public const double PointsPerDegree = 4.5;

    public const double MaxExcessPenalty = 45;
    public const double MaxThrottlePenalty = 25;
    public const double ThrottlePointsPerDailyEvent = 12;
    public const double MaxSoakPenalty = 15;
    public const double MaxAbsolutePenalty = 12;

    /// <summary>Minimum minutes of recent data in a bucket before it may judge.</summary>
    public static int MinMinutes(LoadBucket b) => b switch
    {
        LoadBucket.Heavy => 8,
        LoadBucket.Medium => 10,
        LoadBucket.Light => 15,
        _ => 20,
    };

    /// <summary>Heavier load buckets say more about paste (that's when the heat
    /// actually has to cross it).</summary>
    public static double Weight(LoadBucket b) => b switch
    {
        LoadBucket.Heavy => 0.50,
        LoadBucket.Medium => 0.30,
        LoadBucket.Light => 0.15,
        _ => 0.05,
    };

    private const double AdjacentBandWeightFactor = 0.6;

    // ----------------------------------------------------------------------

    public static ComponentScore Score(ScoreInput input, Func<double, string> fmtTemp)
    {
        var reasons = new List<ScoreReason>();

        if (!input.BaselineReady)
        {
            AddAbsoluteObservations(input, reasons, fmtTemp, calibrating: true);
            return ComponentScore.CalibratingScore(input.Kind, input.Name, input.CalibrationProgress, reasons);
        }

        double penalty = 0;

        // 1) Ambient-corrected delta vs baseline, load-bucket by load-bucket.
        (double? weightedExcess, double? heavyExcess, double? idleExcess, bool broadExcess, bool usedAdjacentBand)
            = ComputeExcess(input);

        if (weightedExcess is { } excess)
        {
            if (excess > ExcessDeadbandC)
            {
                double p = Math.Min(MaxExcessPenalty, (excess - ExcessDeadbandC) * PointsPerDegree);
                penalty += p;
                reasons.Add(new ScoreReason("delta-excess",
                    $"Running {excess:0.#} °C hotter than baseline at comparable load and weather{(usedAdjacentBand ? " (nearest weather band)" : "")}.",
                    p));
            }
            else if (excess < -1.5)
            {
                reasons.Add(new ScoreReason("delta-cooler",
                    $"Running {-excess:0.#} °C cooler than baseline — paste is doing great.", 0));
            }
            else
            {
                reasons.Add(new ScoreReason("delta-on-baseline", "Temperatures sit on baseline at comparable load and weather.", 0));
            }
        }
        else
        {
            reasons.Add(new ScoreReason("delta-no-data",
                "Not enough recent load to compare against baseline — run something demanding (or the fingerprint test) for a sharper score.", 0));
        }

        // 2) Thermal throttling — the paste failing at its actual job.
        if (input.ThrottleEvents > 0 && input.RecentWindowHours > 0)
        {
            double perDay = input.ThrottleEvents / (input.RecentWindowHours / 24.0);
            double p = perDay * ThrottlePointsPerDailyEvent;
            p = Math.Max(p, input.ThrottleEvents >= 2 ? 8 : 4); // even rare throttling matters
            p = Math.Min(MaxThrottlePenalty, p);
            penalty += p;
            reasons.Add(new ScoreReason("throttle",
                $"Hit the thermal limit {input.ThrottleEvents}× in the last {input.RecentWindowHours / 24:0.#} days.", p));
        }

        // 3) Heat-soak rate: drying paste makes temperature spike faster on load onset.
        if (input.SoakRateRecent is { } soakNow && input.SoakRateBaseline is { } soakBase && soakBase > 1)
        {
            double ratio = soakNow / soakBase;
            if (ratio > 1.15)
            {
                double p = Math.Min(MaxSoakPenalty, (ratio - 1.0) * 45);
                penalty += p;
                reasons.Add(new ScoreReason("soak",
                    $"Heat-soaks {(ratio - 1) * 100:0}% faster than baseline when load hits ({soakNow:0.#} vs {soakBase:0.#} °C/min).", p));
            }
        }

        // 4) Absolute sanity vs silicon limit and chassis norms.
        penalty += AddAbsoluteObservations(input, reasons, fmtTemp, calibrating: false);

        int score = (int)Math.Round(Math.Clamp(100 - penalty, 0, 100));
        PatternHint hint = InferPattern(input, heavyExcess, idleExcess, broadExcess);

        return new ComponentScore(input.Kind, input.Name, score, Verdicts.FromScore(score), false, 1.0, reasons, hint);
    }

    private static (double? Weighted, double? Heavy, double? Idle, bool Broad, bool Adjacent) ComputeExcess(ScoreInput input)
    {
        double sumWeighted = 0, sumWeights = 0;
        double? heavyExcess = null, idleExcess = null;
        int bucketsInExcess = 0, bucketsCompared = 0;
        bool usedAdjacent = false;

        foreach (LoadBucket bucket in new[] { LoadBucket.Heavy, LoadBucket.Medium, LoadBucket.Light, LoadBucket.Idle })
        {
            // Recent rows for this bucket (possibly several ambient bands) with enough data.
            var recentRows = input.Recent
                .Where(r => r.Bucket == bucket && r.DeltaAvg is not null && r.Minutes >= MinMinutes(bucket))
                .ToList();
            if (recentRows.Count == 0)
                continue;

            foreach (RecentBucketObs r in recentRows)
            {
                BaselineBucket? baseline =
                    input.Baseline.FirstOrDefault(b => b.Bucket == bucket && b.Band == r.Band)
                    ?? input.Baseline
                        .Where(b => b.Bucket == bucket && Math.Abs(b.Band - r.Band) == 1)
                        .OrderByDescending(b => b.Minutes)
                        .FirstOrDefault();
                if (baseline is null)
                    continue;

                bool adjacent = baseline.Band != r.Band;
                usedAdjacent |= adjacent;
                double w = Weight(bucket) * (adjacent ? AdjacentBandWeightFactor : 1.0)
                         * Math.Min(1.0, r.Minutes / 60.0 + 0.5); // thin data counts a bit less
                double excess = r.DeltaAvg!.Value - baseline.DeltaAvg;

                sumWeighted += excess * w;
                sumWeights += w;
                bucketsCompared++;
                if (excess > 2.5) bucketsInExcess++;

                if (bucket == LoadBucket.Heavy)
                    heavyExcess = Math.Max(heavyExcess ?? double.MinValue, excess);
                if (bucket == LoadBucket.Idle)
                    idleExcess = Math.Max(idleExcess ?? double.MinValue, excess);
            }
        }

        if (sumWeights <= 0)
            return (null, heavyExcess, idleExcess, false, usedAdjacent);

        bool broad = bucketsCompared >= 3 && bucketsInExcess >= 3;
        return (sumWeighted / sumWeights, heavyExcess, idleExcess, broad, usedAdjacent);
    }

    private static double AddAbsoluteObservations(ScoreInput input, List<ScoreReason> reasons, Func<double, string> fmtTemp, bool calibrating)
    {
        double penalty = 0;

        var heavyRows = input.Recent.Where(r => r.Bucket == LoadBucket.Heavy && r.Minutes >= 5).ToList();
        double? heavyAvg = heavyRows.Count > 0 ? heavyRows.Average(r => r.TempAvg) : null;
        double? heavyMax = heavyRows.Count > 0 ? heavyRows.Max(r => r.TempMax) : null;

        if (input.Profile is { } prof && heavyAvg is { } avg)
        {
            if (avg >= prof.ConcernC)
            {
                double p = calibrating ? 0 : MaxAbsolutePenalty;
                penalty += p;
                reasons.Add(new ScoreReason("beyond-chassis",
                    $"Averaging {fmtTemp(avg)} under heavy load — past the {fmtTemp(prof.ConcernC)} this chassis should ever sustain.", p));
            }
            else if (avg >= prof.SustainedNormC)
            {
                reasons.Add(new ScoreReason("chassis-norm",
                    $"Heavy-load average {fmtTemp(avg)} is warm but within what this chassis sustains by design ({fmtTemp(prof.SustainedNormC)}).", 0));
            }
        }

        if (input.LimitC is { } limit && heavyMax is { } max && max >= limit - 2 && input.ThrottleEvents == 0)
        {
            double p = calibrating ? 0 : 6;
            penalty += p;
            reasons.Add(new ScoreReason("headroom",
                $"Peaks within 2 °C of the {fmtTemp(limit)} silicon limit — no headroom left.", p));
        }

        if (calibrating && input.ThrottleEvents > 0)
        {
            reasons.Add(new ScoreReason("throttle-early",
                $"Already thermal-throttled {input.ThrottleEvents}× during calibration — expect a hard verdict once the baseline locks.", 0));
        }

        return penalty;
    }

    private static PatternHint InferPattern(ScoreInput input, double? heavyExcess, double? idleExcess, bool broadExcess)
    {
        double pasteSignal = 0, dustSignal = 0;

        if (input.SoakRateRecent is { } s && input.SoakRateBaseline is { } b && b > 1 && s / b > 1.25)
            pasteSignal += 2;
        if (input.ThrottleEvents >= 2)
            pasteSignal += 1;
        if (heavyExcess is { } he && (idleExcess is not { } ie || he - ie > 3))
            pasteSignal += he > 3 ? 2 : 0;

        if (broadExcess)
            dustSignal += 2;
        // Fan-speed corroboration when the machine exposes fans.
        var fansRecent = input.Recent.Where(r => r.FanAvg is not null).Select(r => r.FanAvg!.Value).ToList();
        var fansBase = input.Baseline.Where(bb => bb.FanAvg is not null).Select(bb => bb.FanAvg!.Value).ToList();
        if (fansRecent.Count > 0 && fansBase.Count > 0 && fansBase.Average() > 100
            && fansRecent.Average() / fansBase.Average() > 1.15)
            dustSignal += 2;

        return (pasteSignal, dustSignal) switch
        {
            ( >= 3, < 2) => PatternHint.LooksLikePaste,
            ( < 2, >= 2) => PatternHint.LooksLikeDust,
            ( >= 2, >= 2) => PatternHint.Mixed,
            _ => PatternHint.None,
        };
    }
}

using DeltaT.Core.Monitoring;

namespace DeltaT.Core.Scoring;

/// <summary>Turns telemetry into a 0–100 paste-health score. Pure function of its
/// input — no clocks, no I/O — so every rule here is unit-testable.
///
/// The philosophy: absolute temperatures are weather; *changes against this
/// machine's own ambient-corrected baseline* are paste. A CPU that always ran
/// 93 °C under heavy load in hot weather is healthy; one that used to run 85 °C
/// in the same weather and now runs 93 °C is drying out.
///
/// Fan speed is the third variable: airflow well above the learned baseline
/// suppresses the measured rise (cranked fans can make dying paste look fresh),
/// and airflow below it inflates the rise (quiet mode isn't paste failure).
/// Compared cells are normalized by (rpm now / rpm baseline)^0.5 — the
/// conservative end of forced-convection scaling — so the score reflects the
/// paste, not the fan dial.</summary>
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

    /// <summary>Fan speed within ±10% of baseline is normal EC wobble — no correction.</summary>
    public const double FanRatioDeadband = 0.10;

    /// <summary>ΔT scales roughly with airflow^-0.5..-0.8; use the conservative end.</summary>
    public const double FanNormalizationExponent = 0.5;

    /// <summary>Cap on how far a cell's delta may be shifted by fan normalization.</summary>
    public const double MaxFanCorrectionC = 8;

    /// <summary>Below this rpm a "fan reading" is noise or a stopped fan, not airflow data.</summary>
    public const double MinMeaningfulFanRpm = 300;

    /// <summary>Baseline data confidence a not-yet-locked score needs before DeltaT will
    /// show a provisional number instead of the learning dial. Keeps early, thin-data
    /// estimates (which can read as a false 100 or a false Aging) off the screen.</summary>
    public const double ProvisionalMinDataConfidence = 0.5;

    /// <summary>Minimum minutes of recent data in a bucket before it may judge.</summary>
    public static int MinMinutes(LoadBucket b) => b switch
    {
        LoadBucket.Max => 8,
        LoadBucket.Heavy => 8,
        LoadBucket.Medium => 10,
        LoadBucket.Light => 15,
        _ => 20,
    };

    /// <summary>Heavier load buckets say more about paste (that's when the heat
    /// actually has to cross it). Full load says the most of all.</summary>
    public static double Weight(LoadBucket b) => b switch
    {
        LoadBucket.Max => 0.55,
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
        bool locked = input.BaselineReady;

        // 1) Ambient-corrected delta vs baseline, load-bucket by load-bucket,
        //    fan-normalized so airflow overrides can't masquerade as paste health.
        //    This also decides whether there's anything to put a number on yet.
        (double? weightedExcess, double? heavyExcess, double? idleExcess, bool broadExcess, bool usedAdjacentBand, FanNormalization? fanNorm)
            = ComputeExcess(input);

        // Before the baseline locks, DeltaT shows a provisional number only once the
        // estimate is genuinely data-backed: a real like-for-like comparison exists AND
        // the baseline data confidence has crossed a floor. Otherwise a thin, half-learned
        // baseline could flash a misleading 100 (or a false Aging) that whipsaws as more
        // data lands — so below the floor we stay honest and show the learning dial.
        if (!locked && (input.Baseline.Count == 0 || weightedExcess is null
                        || input.CalibrationDataConfidence < ProvisionalMinDataConfidence))
        {
            AddAbsoluteObservations(input, reasons, fmtTemp, calibrating: true);
            return ComponentScore.CalibratingScore(input.Kind, input.Name, input.CalibrationProgress, reasons, input.CalibrationConstraint);
        }

        double penalty = 0;

        // Confidence note (no points): the baseline is old and unverified since the
        // machine sat dormant. Staleness isn't paste failure, so it never moves the
        // number — it tells the user why the number deserves a grain of salt.
        if (input.BaselineStale)
            reasons.Add(new ScoreReason("baseline-stale",
                $"This baseline is {DescribeDormancy(input.DormantDays)} old and unverified since - a lot can change while DeltaT is off (dust, a moved fan, a cleaning). Recalibrate for a score you can trust.",
                0));

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
                    $"Running {-excess:0.#} °C cooler than baseline - paste is doing great.", 0));
            }
            else
            {
                reasons.Add(new ScoreReason("delta-on-baseline", "Temperatures sit on baseline at comparable load and weather.", 0));
            }

            if (fanNorm is { } fn)
            {
                reasons.Add(new ScoreReason("fan-normalized", fn.CorrectionC > 0
                    ? $"Fans averaged {fn.RecentRpm:0} rpm against a {fn.BaselineRpm:0} rpm baseline - the extra airflow flatters the readings, so the comparison was corrected by +{fn.CorrectionC:0.#} °C."
                    : $"Fans averaged {fn.RecentRpm:0} rpm against a {fn.BaselineRpm:0} rpm baseline - quieter fans inflate the readings, so the comparison was corrected by {fn.CorrectionC:0.#} °C.",
                    0));
            }
        }
        else
        {
            reasons.Add(new ScoreReason("delta-no-data",
                "Not enough recent load to compare against baseline - run something demanding (or the fingerprint test) for a sharper score.", 0));
        }

        // 2) Thermal throttling — the paste failing at its actual job.
        if (input.ThrottleEvents > 0 && input.RecentWindowHours > 0)
        {
            // Floor the window at one day: right after a lock the recent window can be
            // an hour, and extrapolating one event to "24 a day" maxed the penalty.
            double perDay = input.ThrottleEvents / Math.Max(1.0, input.RecentWindowHours / 24.0);
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

        // A locked score is final; an unlocked one is a provisional estimate that
        // still carries its calibration confidence so the UI can show the band.
        return new ComponentScore(
            input.Kind, input.Name, score, Verdicts.FromScore(score),
            Calibrating: !locked,
            CalibrationProgress: locked ? 1.0 : input.CalibrationProgress,
            reasons, hint,
            locked ? "" : input.CalibrationConstraint,
            Provisional: !locked)
        { Fan = fanNorm };
    }

    /// <summary>Human phrasing for a dormancy gap — "~2 months", "~6 weeks", "45 days".</summary>
    private static string DescribeDormancy(int days) => days switch
    {
        >= 60 => $"~{days / 30} months",
        >= 21 => $"~{(int)Math.Round(days / 7.0)} weeks",
        _ => $"{days} days",
    };

    /// <summary>Weighted rpm context behind a fan-normalized comparison, for the reason line.</summary>
    public sealed record FanNormalization(double RecentRpm, double BaselineRpm, double CorrectionC);

    private static (double? Weighted, double? Heavy, double? Idle, bool Broad, bool Adjacent, FanNormalization? Fan) ComputeExcess(ScoreInput input)
    {
        double sumWeighted = 0, sumWeights = 0;
        double? heavyExcess = null, idleExcess = null;
        int bucketsInExcess = 0, bucketsCompared = 0;
        bool usedAdjacent = false;
        double fanCorrWeighted = 0, fanRecentWeighted = 0, fanBaseWeighted = 0, fanWeights = 0;

        foreach (LoadBucket bucket in new[] { LoadBucket.Max, LoadBucket.Heavy, LoadBucket.Medium, LoadBucket.Light, LoadBucket.Idle })
        {
            // Recent rows for this bucket (possibly several ambient bands) with enough data.
            var recentRows = input.Recent
                .Where(r => r.Bucket == bucket && r.DeltaAvg is not null && r.Minutes >= MinMinutes(bucket))
                .ToList();
            if (recentRows.Count == 0)
                continue;

            foreach (RecentBucketObs r in recentRows)
            {
                // Prefer the exact same weather band: a like-for-like rise comparison.
                BaselineBucket? sameBand = input.Baseline.FirstOrDefault(b => b.Bucket == bucket && b.Band == r.Band);
                BaselineBucket? baseline = sameBand ?? NearestBaselineBand(input.Baseline, bucket, r);
                if (baseline is null)
                    continue;

                bool adjacent = baseline.Band != r.Band;
                usedAdjacent |= adjacent;
                double w = Weight(bucket) * (adjacent ? AdjacentBandWeightFactor : 1.0)
                         * Math.Min(1.0, r.Minutes / 60.0 + 0.5); // thin data counts a bit less

                double delta = r.DeltaAvg!.Value;
                double correction = 0;
                if (delta > 0
                    && r.FanAvg is { } recentFan && baseline.FanAvg is { } baseFan
                    && recentFan >= MinMeaningfulFanRpm && baseFan >= MinMeaningfulFanRpm)
                {
                    double ratio = recentFan / baseFan;
                    if (Math.Abs(ratio - 1) >= FanRatioDeadband)
                    {
                        double normalized = delta * Math.Pow(ratio, FanNormalizationExponent);
                        correction = Math.Clamp(normalized - delta, -MaxFanCorrectionC, MaxFanCorrectionC);
                    }
                    fanRecentWeighted += recentFan * w;
                    fanBaseWeighted += baseFan * w;
                    fanWeights += w;
                }

                // Same band → compare the rise directly. Different band → don't borrow the
                // other band's rise (that's what inflates a cold-weather reading into false
                // "Aging"). Instead anchor on ABSOLUTE die temperature: paste degradation can
                // only make the die hotter, and colder outdoor air can't make a healthy die
                // hotter, so a reading at or below the healthy die temp for this load is
                // provably not degraded — whatever its rise-over-outside works out to.
                // Cross-band readings are clamped at "not degraded" (0): sitting below
                // the physical ceiling proves health but can't quantify improvement, so
                // letting a very negative cross-band number into the weighted sum would
                // mask genuine same-band excess elsewhere (e.g. a cold-snap idle hiding
                // a hot heavy bucket).
                double excess = (sameBand is not null || baseline.TempAvg is not { } baseTemp || r.TempAvg <= 0)
                    ? delta + correction - baseline.DeltaAvg
                    : Math.Max(0, CrossBandExcess(r, baseline, baseTemp) + correction);
                fanCorrWeighted += correction * w;

                sumWeighted += excess * w;
                sumWeights += w;
                bucketsCompared++;
                if (excess > 2.5) bucketsInExcess++;

                if (bucket is LoadBucket.Heavy or LoadBucket.Max)
                    heavyExcess = Math.Max(heavyExcess ?? double.MinValue, excess);
                if (bucket == LoadBucket.Idle)
                    idleExcess = Math.Max(idleExcess ?? double.MinValue, excess);
            }
        }

        if (sumWeights <= 0)
            return (null, heavyExcess, idleExcess, false, usedAdjacent, null);

        FanNormalization? fan = null;
        double corr = fanCorrWeighted / sumWeights;
        if (fanWeights > 0 && Math.Abs(corr) >= 1.0)
            fan = new FanNormalization(fanRecentWeighted / fanWeights, fanBaseWeighted / fanWeights, Math.Round(corr, 1));

        bool broad = bucketsCompared >= 3 && bucketsInExcess >= 3;
        return (sumWeighted / sumWeights, heavyExcess, idleExcess, broad, usedAdjacent, fan);
    }

    /// <summary>Nearest baseline cell in a *different* weather band for the same load
    /// bucket, chosen by how close its learned ambient (TempAvg − DeltaAvg) sits to the
    /// recent reading's ambient. Falls back to nearest band index for legacy rows that
    /// carry no absolute-temp anchor yet.</summary>
    private static BaselineBucket? NearestBaselineBand(IReadOnlyList<BaselineBucket> baseline, LoadBucket bucket, RecentBucketObs r)
    {
        BaselineBucket? best = null;
        double bestDist = double.MaxValue;
        double recentAmbient = r.TempAvg - (r.DeltaAvg ?? 0);
        foreach (BaselineBucket b in baseline)
        {
            if (b.Bucket != bucket)
                continue;
            double dist = b.TempAvg is { } t
                ? Math.Abs((t - b.DeltaAvg) - recentAmbient)
                : Math.Abs(b.Band - r.Band) * 100.0; // band-index proximity when no anchor
            if (dist < bestDist) { bestDist = dist; best = b; }
        }
        return best;
    }

    /// <summary>Excess for a reading whose exact weather band was never learned, judged on
    /// ABSOLUTE die temperature instead of rise-over-outside. Two physical facts make this
    /// bulletproof against the cold-weather false alarm: paste can only make the die hotter,
    /// and colder outdoor air can't make a healthy die hotter. So the healthy ceiling is the
    /// reference band's learned die temp, allowed to climb only when the current weather is
    /// *warmer* than that band (an expected, real rise); in colder weather the ceiling holds,
    /// and any reading at or below it reads as on-baseline-or-better — never a false Aging.
    /// Genuine degradation still pushes the die above the ceiling and is caught.</summary>
    private static double CrossBandExcess(RecentBucketObs r, BaselineBucket baseline, double baseTemp)
    {
        double recentAmbient = r.TempAvg - (r.DeltaAvg ?? 0);
        double baseAmbient = baseTemp - baseline.DeltaAvg;
        double ceiling = baseTemp + Math.Max(0, recentAmbient - baseAmbient);
        return r.TempAvg - ceiling;
    }

    private static double AddAbsoluteObservations(ScoreInput input, List<ScoreReason> reasons, Func<double, string> fmtTemp, bool calibrating)
    {
        double penalty = 0;

        var heavyRows = input.Recent.Where(r => r.Bucket is LoadBucket.Heavy or LoadBucket.Max && r.Minutes >= 5).ToList();
        double? heavyAvg = heavyRows.Count > 0 ? heavyRows.Average(r => r.TempAvg) : null;
        double? heavyMax = heavyRows.Count > 0 ? heavyRows.Max(r => r.TempMax) : null;

        if (input.Profile is { } prof && heavyAvg is { } avg)
        {
            if (avg >= prof.ConcernC)
            {
                double p = calibrating ? 0 : MaxAbsolutePenalty;
                penalty += p;
                reasons.Add(new ScoreReason("beyond-chassis",
                    $"Averaging {fmtTemp(avg)} under heavy load - past the {fmtTemp(prof.ConcernC)} this chassis should ever sustain.", p));
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
                $"Peaks within 2 °C of the {fmtTemp(limit)} silicon limit - no headroom left.", p));
        }

        if (calibrating && input.ThrottleEvents > 0)
        {
            reasons.Add(new ScoreReason("throttle-early",
                $"Already thermal-throttled {input.ThrottleEvents}× during calibration - expect a hard verdict once the baseline locks.", 0));
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

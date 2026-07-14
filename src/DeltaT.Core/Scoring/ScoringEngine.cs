using DeltaT.Core.Monitoring;

namespace DeltaT.Core.Scoring;

/// <summary>Turns telemetry into a 0–100 overall thermal-health score (the diagnosis
/// and per-aspect readout say WHY). Pure function of its
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
    public const double MaxCooldownPenalty = 12;
    public const double MaxAbsolutePenalty = 12;

    // Power (source-side) normalization. Die-to-ambient rise scales with dissipated
    // power (ΔT ≈ P × thermal-resistance), so comparing a rise learned at one wattage
    // against a rise measured at another silently blames the paste for a power change.
    // Scaling the recent rise by (baseline W / recent W) recovers the resistance — the
    // paste-only quantity — so an undervolt, a raised power limit, or a heavier real
    // workload at the same load% stops masquerading as paste drift.
    /// <summary>Power within ±8% of baseline is normal run-to-run variance — no correction.</summary>
    public const double PowerRatioDeadband = 0.08;

    /// <summary>A power ratio outside this band implies &gt;2× or &lt;0.5× dissipation — real
    /// hardware rarely swings that far, so treat it as a sensor glitch and clamp before it
    /// can move the score. Within the band, a genuine undervolt/overclock is fully corrected
    /// (no half-applied fix that would leave a false penalty).</summary>
    public const double PowerRatioClampLo = 0.5;
    public const double PowerRatioClampHi = 2.0;

    /// <summary>Final backstop on the °C a cell's rise may be shifted by power normalization,
    /// after the ratio clamp — a large but not unbounded correction for a big power swing.
    /// Sized against the physics, not intuition: a 50 °C load rise (an ordinary laptop under
    /// full load) with CPU boost switched on after a boost-off baseline needs the full 0.5–2.0
    /// ratio range to be expressible, which is a correction of tens of degrees. At 20 °C the
    /// cap clipped exactly those legitimate cases and left a residual "excess" that the score
    /// then charged to the paste; the ratio clamp above is what guards against sensor
    /// glitches, so this only needs to stop a runaway.</summary>
    public const double MaxPowerCorrectionC = 35;

    /// <summary>Below this wattage a "power reading" is idle/noise, not a load the paste
    /// is meaningfully conducting — don't normalize against it.</summary>
    public const double MinMeaningfulPowerW = 5;

    /// <summary>Recent cooldown rate this fraction of baseline (or slower) is a real
    /// slowdown worth penalizing — degraded paste sheds heat sluggishly.</summary>
    public const double CooldownSlowdownRatio = 0.85;

    // Hotspot-to-edge gap under load. Different GPU models run very different
    // natural gaps (8–15 °C is common, some run wider by design), so the PRIMARY
    // judgement is drift against this card's own learned gap; the absolute
    // thresholds only backstop paste that was already failing when DeltaT arrived
    // (a baseline learned on a bad mount would bless its gap as "normal").
    public const double MaxHotspotGapPenalty = 16;
    public const double HotspotDriftDeadbandC = 3;
    public const double HotspotDriftPointsPerDegree = 2.2;
    public const double HotspotGapNoticeC = 20;
    public const double HotspotGapPenaltyC = 24;

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
        ExcessResult ex = ComputeExcess(input);
        double? weightedExcess = ex.Weighted;
        double? heavyExcess = ex.Heavy;
        double? idleExcess = ex.Idle;
        bool broadExcess = ex.Broad;
        bool usedAdjacentBand = ex.Adjacent;
        FanNormalization? fanNorm = ex.Fan;
        PowerNormalization? powerNorm = ex.Power;
        bool fanUndershoot = ex.FanUndershoot;

        // Before the baseline locks, DeltaT shows a provisional number only once the
        // estimate is genuinely data-backed: a real like-for-like comparison exists AND
        // the baseline data confidence has crossed a floor. Otherwise a thin, half-learned
        // baseline could flash a misleading 100 (or a false Aging) that whipsaws as more
        // data lands — so below the floor we stay honest and show the learning dial.
        // The floor is an ENTRY gate only: once a provisional score has been shown this
        // epoch (ProvisionalEverShown), a later confidence dip — a new session widening
        // the variance estimate — updates the number instead of yanking it off screen.
        if (!locked && (input.Baseline.Count == 0 || weightedExcess is null
                        || (input.CalibrationDataConfidence < ProvisionalMinDataConfidence
                            && !input.ProvisionalEverShown)))
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
                $"This baseline is {DescribeDormancy(input.DormantDays)} old and unverified since. A lot can change while DeltaT is off (dust, a moved fan, a cleaning). Recalibrate for a score you can trust.",
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
                    $"Running {-excess:0.#} °C cooler than baseline. The paste is doing great.", 0));
            }
            else
            {
                reasons.Add(new ScoreReason("delta-on-baseline", "Temperatures sit on baseline at comparable load and weather.", 0));
            }

            if (powerNorm is { } pn)
            {
                reasons.Add(new ScoreReason("power-normalized", pn.RecentW > pn.BaselineW
                    ? $"Drawing {pn.RecentW:0} W against a {pn.BaselineW:0} W baseline (a boost mode, a power limit, or an overclock). More power means a hotter die for reasons that aren't the paste, so the comparison was corrected by {pn.CorrectionC:0.#} °C. Same wattage, the paste is judged fairly."
                    : $"Drawing {pn.RecentW:0} W against a {pn.BaselineW:0} W baseline (boost off, a lower power limit, or an undervolt). Less power cools the die on its own, so the comparison was corrected by +{pn.CorrectionC:0.#} °C rather than credited to the paste.",
                    0));
            }

            if (fanNorm is { } fn)
            {
                reasons.Add(new ScoreReason("fan-normalized", fn.CorrectionC > 0
                    ? $"Fans averaged {fn.RecentRpm:0} rpm against a {fn.BaselineRpm:0} rpm baseline. The extra airflow flatters the readings, so the comparison was corrected by +{fn.CorrectionC:0.#} °C."
                    : $"Fans averaged {fn.RecentRpm:0} rpm against a {fn.BaselineRpm:0} rpm baseline. Quieter fans inflate the readings, so the comparison was corrected by {fn.CorrectionC:0.#} °C.",
                    0));
            }

            if (fanUndershoot && ex.FanRecentMean is { } fanNow && ex.FanBaselineMean is { } fanWas)
            {
                reasons.Add(new ScoreReason("fan-undershoot",
                    $"Fans are running about {(1 - fanNow / fanWas) * 100:0}% slower than this machine's baseline at the same load ({fanNow:0} vs {fanWas:0} rpm). The score already accounts for the airflow, but if you didn't set a quieter profile, a fan that can't reach its old speed points at a failing fan or a clogged intake worth checking.",
                    0));
            }
        }
        else
        {
            reasons.Add(new ScoreReason("delta-no-data",
                "Not enough recent load to compare against baseline. Run something demanding (or the fingerprint test) for a sharper score.", 0));
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

        // The soak and cooldown rates are power-dependent too, so they get the same
        // source-side correction as the rise itself (see PowerRateScale). Without it,
        // switching CPU boost/turbo on after a boost-off baseline reads as "heat-soaks
        // faster" (a paste tell), and switching it off reads as "sheds heat slower".
        double rateScale = PowerRateScale(ex);
        double? soakRecent = input.SoakRateRecent * rateScale;
        double? cooldownRecent = input.CooldownRateRecent * rateScale;

        // 3) Heat-soak rate: drying paste makes temperature spike faster on load onset.
        if (soakRecent is { } soakNow && input.SoakRateBaseline is { } soakBase && soakBase > 1)
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

        // 3b) Cooldown rate: the same resistance that slows heat IN slows heat OUT, so
        // degraded paste also sheds heat sluggishly when a load ends. This is the opposite
        // edge of the soak signal and shows up whenever a game or render finishes, so a
        // machine used in long steady sessions still yields it. Slower-than-baseline = worse.
        if (cooldownRecent is { } coolNow && input.CooldownRateBaseline is { } coolBase && coolBase > 1)
        {
            double ratio = coolNow / coolBase;
            if (ratio < CooldownSlowdownRatio)
            {
                double p = Math.Min(MaxCooldownPenalty, (1.0 - ratio) * 40);
                penalty += p;
                reasons.Add(new ScoreReason("cooldown",
                    $"Sheds heat {(1 - ratio) * 100:0}% slower than baseline when load drops ({coolNow:0.#} vs {coolBase:0.#} °C/min). Heat lingering in the die is the same resistance that makes paste run hot.", p));
            }
        }

        // 4) Absolute sanity vs silicon limit and chassis norms.
        penalty += AddAbsoluteObservations(input, reasons, fmtTemp, calibrating: false);

        PatternHint hint = InferPattern(input, heavyExcess, idleExcess, broadExcess, fanUndershoot);
        DiagnosisInputs evidence = GatherDiagnosisInputs(input, ex, weightedExcess, heavyExcess, idleExcess, broadExcess, fanUndershoot, powerNorm, soakRecent, cooldownRecent);
        IReadOnlyList<AspectHealth> aspects = ThermalDiagnostician.AssessAspects(evidence);

        // The score starts at 100 and comes down on evidence, so no evidence at all is not
        // a 100 — it's an unanswered question. A locked baseline with nothing comparable
        // measured against it (the state right after a lock, before the next real load)
        // would otherwise report a confident "100, Excellent" while every aspect it claims
        // to judge honestly reads "--". Say "waiting for load" instead of inventing health.
        // Any real evidence at all (a throttle event, peaks at the silicon wall) still
        // scores: a fault is never hidden behind a waiting state.
        if (weightedExcess is null && penalty <= 0)
            return ComponentScore.AwaitingDataScore(input.Kind, input.Name, reasons, aspects);

        int score = (int)Math.Round(Math.Clamp(100 - penalty, 0, 100));
        ThermalDiagnosis diagnosis = ThermalDiagnostician.Diagnose(evidence);

        // A locked score is final; an unlocked one is a provisional estimate that
        // still carries its calibration confidence so the UI can show the band.
        return new ComponentScore(
            input.Kind, input.Name, score, Verdicts.FromScore(score),
            Calibrating: !locked,
            CalibrationProgress: locked ? 1.0 : input.CalibrationProgress,
            reasons, hint,
            locked ? "" : input.CalibrationConstraint,
            Provisional: !locked)
        { Fan = fanNorm, Power = powerNorm, Diagnosis = diagnosis, Aspects = aspects, ExcessC = weightedExcess };
    }

    /// <summary>Gather the evidence the scoring pass already computed for the diagnosis
    /// and the per-aspect health readout, so both judge from the same facts.</summary>
    private static DiagnosisInputs GatherDiagnosisInputs(
        ScoreInput input, ExcessResult ex, double? weightedExcess, double? heavyExcess,
        double? idleExcess, bool broadExcess, bool fanUndershoot, PowerNormalization? powerNorm,
        double? soakRecent, double? cooldownRecent)
    {
        (double? gap, double? baseGap) = HotspotGap(input);

        var heavyRows = input.Recent.Where(r => r.Bucket is LoadBucket.Heavy or LoadBucket.Max && r.Minutes >= 5).ToList();
        double? heavyAvg = heavyRows.Count > 0 ? heavyRows.Average(r => r.TempAvg) : null;
        double? heavyMax = heavyRows.Count > 0 ? heavyRows.Max(r => r.TempMax) : null;
        bool nearLimit = input.LimitC is { } lim && heavyMax is { } hm && hm >= lim - 2;
        bool beyondNorm = input.Profile is { } prof && heavyAvg is { } ha && ha >= prof.SustainedNormC;

        double? fanRatio = ex.FanRecentMean is { } fr && ex.FanBaselineMean is { } fb && fb > 0 ? fr / fb : null;
        double? soakRatio = soakRecent is { } sr && input.SoakRateBaseline is { } sb && sb > 1 ? sr / sb : null;
        double? coolRatio = cooldownRecent is { } cr && input.CooldownRateBaseline is { } cb && cb > 1 ? cr / cb : null;
        double? powerRatio = ex.PowerRecentMean is { } pw && ex.PowerBaselineMean is { } pb && pb >= MinMeaningfulPowerW
            ? pw / pb : null;

        return new DiagnosisInputs(
            input.Kind, weightedExcess, heavyExcess, idleExcess, broadExcess,
            soakRatio, coolRatio, fanUndershoot, fanRatio,
            powerNorm?.CorrectionC ?? 0, gap, baseGap,
            input.ThrottleEvents, input.RecentWindowHours, nearLimit, beyondNorm,
            powerRatio);
    }

    /// <summary>Human phrasing for a dormancy gap — "~2 months", "~6 weeks", "45 days".</summary>
    private static string DescribeDormancy(int days) => days switch
    {
        >= 60 => $"~{days / 30} months",
        >= 21 => $"~{(int)Math.Round(days / 7.0)} weeks",
        _ => $"{days} days",
    };

    /// <summary>Factor that expresses a recent RATE (°C/min) at baseline power, the rate-side
    /// twin of the °C correction applied to the rise. Both the heat-soak rate (dT/dt ≈ P/C)
    /// and the post-load cooldown rate (which starts from a die temperature that itself
    /// scales with P) move roughly linearly with dissipated power, so a power change alters
    /// them for reasons that have nothing to do with the paste: disabling Intel turbo/boost
    /// slows the soak AND the cooldown, enabling it speeds both. Uncorrected, that fires the
    /// "sheds heat slower" penalty on boost-off and the "heat-soaks faster" paste tell on
    /// boost-on. Returns 1.0 when there's no usable power reading on both sides, or when the
    /// difference is inside the normal run-to-run deadband.</summary>
    private static double PowerRateScale(ExcessResult ex)
    {
        if (ex.PowerBaselineMean is not { } baseW || ex.PowerRecentMean is not { } recentW
            || baseW < MinMeaningfulPowerW || recentW < MinMeaningfulPowerW)
            return 1.0;
        double ratio = Math.Clamp(baseW / recentW, PowerRatioClampLo, PowerRatioClampHi);
        return Math.Abs(ratio - 1) >= PowerRatioDeadband ? ratio : 1.0;
    }

    /// <summary>Weighted rpm context behind a fan-normalized comparison, for the reason line.</summary>
    public sealed record FanNormalization(double RecentRpm, double BaselineRpm, double CorrectionC);

    /// <summary>Weighted wattage context behind a power-normalized comparison, for the reason line.</summary>
    public sealed record PowerNormalization(double RecentW, double BaselineW, double CorrectionC);

    private static ExcessResult ComputeExcess(ScoreInput input)
    {
        double sumWeighted = 0, sumWeights = 0;
        double? heavyExcess = null, idleExcess = null;
        int bucketsInExcess = 0, bucketsCompared = 0;
        bool usedAdjacent = false;
        double fanCorrWeighted = 0, fanRecentWeighted = 0, fanBaseWeighted = 0, fanWeights = 0;
        double powerCorrWeighted = 0, powerRecentWeighted = 0, powerBaseWeighted = 0, powerWeights = 0;

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

                // Source-side normalization: express the recent rise at BASELINE power.
                // ΔT ≈ P × thermal-resistance, so scaling by (baseline W / recent W)
                // recovers the resistance — the paste-only quantity. A power-limit,
                // undervolt, overclock, or heavier real workload at the same load% moves
                // the die temperature for reasons that aren't the paste; this removes it.
                double powerCorrection = 0;
                if (delta > 0
                    && r.PowerAvg is { } recentPower && baseline.PowerAvg is { } basePower
                    && recentPower >= MinMeaningfulPowerW && basePower >= MinMeaningfulPowerW)
                {
                    double pratio = Math.Clamp(basePower / recentPower, PowerRatioClampLo, PowerRatioClampHi);
                    if (Math.Abs(pratio - 1) >= PowerRatioDeadband)
                        powerCorrection = Math.Clamp(delta * pratio - delta, -MaxPowerCorrectionC, MaxPowerCorrectionC);
                    powerRecentWeighted += recentPower * w;
                    powerBaseWeighted += basePower * w;
                    powerWeights += w;
                }

                // Sink-side normalization: fan/airflow, applied on the power-normalized rise.
                double fanBase = delta + powerCorrection;
                double fanCorrection = 0;
                if (delta > 0
                    && r.FanAvg is { } recentFan && baseline.FanAvg is { } baseFan
                    && recentFan >= MinMeaningfulFanRpm && baseFan >= MinMeaningfulFanRpm)
                {
                    double ratio = recentFan / baseFan;
                    if (Math.Abs(ratio - 1) >= FanRatioDeadband)
                    {
                        double normalized = fanBase * Math.Pow(ratio, FanNormalizationExponent);
                        fanCorrection = Math.Clamp(normalized - fanBase, -MaxFanCorrectionC, MaxFanCorrectionC);
                    }
                    fanRecentWeighted += recentFan * w;
                    fanBaseWeighted += baseFan * w;
                    fanWeights += w;
                }

                double correction = powerCorrection + fanCorrection;

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
                fanCorrWeighted += fanCorrection * w;
                powerCorrWeighted += powerCorrection * w;

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
            return new ExcessResult(null, heavyExcess, idleExcess, false, usedAdjacent, null, null, null, null, null, null);

        FanNormalization? fan = null;
        double corr = fanCorrWeighted / sumWeights;
        if (fanWeights > 0 && Math.Abs(corr) >= 1.0)
            fan = new FanNormalization(fanRecentWeighted / fanWeights, fanBaseWeighted / fanWeights, Math.Round(corr, 1));

        PowerNormalization? power = null;
        double pcorr = powerCorrWeighted / sumWeights;
        if (powerWeights > 0 && Math.Abs(pcorr) >= 1.0)
            power = new PowerNormalization(powerRecentWeighted / powerWeights, powerBaseWeighted / powerWeights, Math.Round(pcorr, 1));

        double? fanRecentMean = fanWeights > 0 ? fanRecentWeighted / fanWeights : null;
        double? fanBaseMean = fanWeights > 0 ? fanBaseWeighted / fanWeights : null;
        double? powerRecentMean = powerWeights > 0 ? powerRecentWeighted / powerWeights : null;
        double? powerBaseMean = powerWeights > 0 ? powerBaseWeighted / powerWeights : null;

        bool broad = bucketsCompared >= 3 && bucketsInExcess >= 3;
        return new ExcessResult(sumWeighted / sumWeights, heavyExcess, idleExcess, broad, usedAdjacent, fan, power, fanRecentMean, fanBaseMean, powerRecentMean, powerBaseMean);
    }

    private readonly record struct ExcessResult(
        double? Weighted, double? Heavy, double? Idle, bool Broad, bool Adjacent,
        FanNormalization? Fan, PowerNormalization? Power,
        double? FanRecentMean, double? FanBaselineMean,
        double? PowerRecentMean, double? PowerBaselineMean)
    {
        /// <summary>The fan is turning well below where this machine used to run it at the
        /// same load — a hint at cause (failing/seizing fan, clogged intake, or a quieter
        /// profile the user chose). Not itself a penalty: fan normalization already reflects
        /// the airflow in the number, so scoring on it again would double-count.</summary>
        public bool FanUndershoot =>
            FanBaselineMean is { } b && b >= MinMeaningfulFanRpm
            && FanRecentMean is { } r && r / b <= 0.80;
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
            // The concern threshold is user-overridable: a rig that runs hot by design
            // (overclock, aggressive power limit) shouldn't be nagged by a stock number.
            double concern = input.ConcernOverrideC ?? prof.ConcernC;
            if (avg >= concern)
            {
                double p = calibrating ? 0 : MaxAbsolutePenalty;
                penalty += p;
                reasons.Add(new ScoreReason("beyond-chassis",
                    $"Averaging {fmtTemp(avg)} under heavy load, past the {fmtTemp(concern)} you've set as this machine's sustained ceiling.", p));
            }
            else if (avg >= prof.SustainedNormC && input.ConcernOverrideC is null)
            {
                reasons.Add(new ScoreReason("chassis-norm",
                    $"Heavy-load average {fmtTemp(avg)} is warm but within what this chassis sustains by design ({fmtTemp(prof.SustainedNormC)}).", 0));
            }
        }

        if (input.HeadroomWarnings && input.LimitC is { } limit && heavyMax is { } max && max >= limit - 2 && input.ThrottleEvents == 0)
        {
            double p = calibrating ? 0 : 6;
            penalty += p;
            reasons.Add(new ScoreReason("headroom",
                $"Peaks within 2 °C of the {fmtTemp(limit)} silicon limit, no headroom left.", p));
        }

        if (calibrating && input.ThrottleEvents > 0)
        {
            reasons.Add(new ScoreReason("throttle-early",
                $"Already thermal-throttled {input.ThrottleEvents}× during calibration. Expect a hard verdict once the baseline locks.", 0));
        }

        penalty += AddHotspotGap(input, reasons, calibrating);

        return penalty;
    }

    /// <summary>Hotspot-to-edge gap: heat that reaches the hotspot sensor but not the
    /// edge one hasn't crossed the paste. A widening gap against this card's OWN learned
    /// gap is the classic dried/pumped-out signature — often visible before the edge
    /// temperature moves at all. Loaded buckets only; idle gaps are noise.</summary>
    /// <summary>This machine's current loaded hotspot-to-edge gap and its own learned
    /// healthy gap, shared by the hotspot penalty and the cause diagnosis.</summary>
    private static (double? Gap, double? BaseGap) HotspotGap(ScoreInput input)
    {
        var rows = input.Recent
            .Where(r => r.Bucket is LoadBucket.Medium or LoadBucket.Heavy or LoadBucket.Max
                        && r.GapAvg is not null && r.Minutes >= 5)
            .ToList();
        if (rows.Count == 0)
            return (null, null);
        double gap = rows.Sum(r => r.GapAvg!.Value * r.Minutes) / rows.Sum(r => r.Minutes);

        var baseRows = input.Baseline
            .Where(b => b.GapAvg is not null && rows.Any(r => r.Bucket == b.Bucket))
            .ToList();
        double? baseGap = baseRows.Count > 0
            ? baseRows.Sum(b => b.GapAvg!.Value * b.Minutes) / baseRows.Sum(b => b.Minutes)
            : null;
        return (gap, baseGap);
    }

    private static double AddHotspotGap(ScoreInput input, List<ScoreReason> reasons, bool calibrating)
    {
        (double? gapMaybe, double? baseGap) = HotspotGap(input);
        if (gapMaybe is not { } gap)
            return 0;

        double driftPoints = 0;
        if (baseGap is { } bg && gap - bg > HotspotDriftDeadbandC)
            driftPoints = Math.Min(MaxHotspotGapPenalty, (gap - bg - HotspotDriftDeadbandC) * HotspotDriftPointsPerDegree);

        // Backstop: an absolute ceiling for mounts that were never healthy on record.
        double absPoints = gap >= HotspotGapPenaltyC
            ? Math.Min(MaxHotspotGapPenalty, 3 + (gap - HotspotGapPenaltyC) * 1.2)
            : 0;

        double p = calibrating ? 0 : Math.Max(driftPoints, absPoints);
        string label = input.Kind.Label();

        if (p > 0 && driftPoints >= absPoints && baseGap is { } b1)
        {
            reasons.Add(new ScoreReason("hotspot-gap",
                $"{label} hotspot gap widened to {gap:0.#}° from this card's own {b1:0.#}° baseline. Heat is spreading less evenly than it used to. That drift is the paste, not the weather.", p));
        }
        else if (p > 0)
        {
            reasons.Add(new ScoreReason("hotspot-gap",
                $"{label} hotspot runs {gap:0.#}° above the edge sensor under load. Heat is pooling at one spot instead of crossing the paste.", p));
        }
        else if (gap >= HotspotGapNoticeC && (baseGap is not { } b2 || gap - b2 > HotspotDriftDeadbandC))
        {
            reasons.Add(new ScoreReason("hotspot-gap",
                $"{label} hotspot gap is {gap:0.#}° under load, wider than the usual healthy spread. Worth watching.", 0));
        }
        return p;
    }

    private static PatternHint InferPattern(ScoreInput input, double? heavyExcess, double? idleExcess, bool broadExcess, bool fanUndershoot)
    {
        double pasteSignal = 0, dustSignal = 0;

        if (input.SoakRateRecent is { } s && input.SoakRateBaseline is { } b && b > 1 && s / b > 1.25)
            pasteSignal += 2;
        // Sluggish cooldown is the falling-edge twin of a fast soak — the same paste
        // resistance seen from the other side, so it corroborates a paste read.
        if (input.CooldownRateRecent is { } cr && input.CooldownRateBaseline is { } cb && cb > 1 && cr / cb < 1 - (1 - CooldownSlowdownRatio))
            pasteSignal += 1;
        if (input.ThrottleEvents >= 2)
            pasteSignal += 1;
        if (heavyExcess is { } he && (idleExcess is not { } ie || he - ie > 3))
            pasteSignal += he > 3 ? 2 : 0;

        if (broadExcess)
            dustSignal += 2;
        // A fan that can no longer reach its old speed (or a clogged intake) is an
        // airflow-side fault, which is the dust/airflow pattern, not the paste.
        if (fanUndershoot)
            dustSignal += 1;
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

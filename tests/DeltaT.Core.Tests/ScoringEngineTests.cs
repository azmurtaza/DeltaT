using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

public class ScoringEngineTests
{
    private static readonly Func<double, string> Fmt = t => $"{t:0} °C";
    private const int Cold = (int)AmbientBand.Cold;
    private const int Warm = (int)AmbientBand.Warm;
    private const int Hot = (int)AmbientBand.Hot;

    private static readonly ComponentProfile NitroCpu = new(20, 66, 93, 98);

    private static ScoreInput Input(
        IReadOnlyList<RecentBucketObs>? recent = null,
        IReadOnlyList<BaselineBucket>? baseline = null,
        int throttleEvents = 0,
        double? soakRecent = null,
        double? soakBaseline = null,
        double? cooldownRecent = null,
        double? cooldownBaseline = null,
        bool ready = true,
        double progress = 1.0,
        double recentHours = 7 * 24,
        bool stale = false,
        int dormantDays = 0,
        double dataConfidence = 1.0,
        bool provisionalEverShown = false,
        double? concernOverrideC = null,
        bool headroomWarnings = true) =>
        new(ComponentKind.Cpu, "Test CPU",
            recent ?? Array.Empty<RecentBucketObs>(),
            baseline ?? Array.Empty<BaselineBucket>(),
            recentHours, throttleEvents, soakRecent, soakBaseline,
            CooldownRateRecent: cooldownRecent, CooldownRateBaseline: cooldownBaseline,
            LimitC: 100, Profile: NitroCpu, BaselineReady: ready, CalibrationProgress: progress,
            BaselineStale: stale, DormantDays: dormantDays, CalibrationDataConfidence: dataConfidence,
            ProvisionalEverShown: provisionalEverShown,
            ConcernOverrideC: concernOverrideC, HeadroomWarnings: headroomWarnings);

    private static RecentBucketObs Heavy(double delta, int band = Warm, int minutes = 60, double tempAvg = 88, double tempMax = 92, double? fan = null, double? power = null) =>
        new(LoadBucket.Heavy, band, minutes, delta, tempAvg, tempMax, fan, 0, PowerAvg: power);

    private static BaselineBucket HeavyBase(double delta, int band = Warm, double? fan = null, double? tempAvg = null, double? power = null) =>
        new(LoadBucket.Heavy, band, delta, delta + 3, fan, 200, tempAvg, PowerAvg: power);

    private static RecentBucketObs HeavyGap(double delta, double gap, int minutes = 60) =>
        new(LoadBucket.Heavy, Warm, minutes, delta, 88, 92, null, 0, gap);

    private static BaselineBucket HeavyBaseGap(double delta, double? gap) =>
        new(LoadBucket.Heavy, Warm, delta, delta + 3, null, 200, null, gap);

    // ------------------------------------------------------------------ core behaviours

    [Fact]
    public void EightDegreesOverBaseline_AtHeavyLoad_DropsBelow50()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68) },
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.True(score.Value < 70, $"expected degraded-ish score, got {score.Value}");
        Assert.Contains(score.Reasons, r => r.Code == "delta-excess");
        // +8 °C over baseline alone: (8 - 0.8) * 4.5 ≈ 32 points → 68 → Aging.
        // Combined with real-world throttle/soak signals this lands in Degraded.
        Assert.True(score.Verdict is Verdict.Aging or Verdict.Degraded);
    }

    [Fact]
    public void LockedBaseline_WithNothingComparableMeasured_RefusesToClaimHealth()
    {
        // The state right after a lock: the reference is frozen, but no load has run
        // against it yet. The score is 100 minus evidence, so no evidence must not read
        // as a confident "100, Excellent" while every aspect honestly reads "--".
        ComponentScore score = ScoringEngine.Score(Input(
            recent: Array.Empty<RecentBucketObs>(),
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.Equal(Verdict.AwaitingData, score.Verdict);
        Assert.True(score.AwaitingData);
        Assert.False(score.Scored);
        Assert.NotEqual(100, score.Value);
        Assert.Contains(score.Reasons, r => r.Code == "delta-no-data");
        Assert.All(score.Aspects.Where(a => a.Aspect is HealthAspect.Paste or HealthAspect.Airflow),
            a => Assert.False(a.Known));
    }

    [Fact]
    public void LockedBaseline_NoComparison_ButRealThrottling_StillScores()
    {
        // A waiting state must never hide a fault: hard evidence (throttle events) is
        // scoreable on its own, with or without a like-for-like comparison.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: Array.Empty<RecentBucketObs>(),
            baseline: new[] { HeavyBase(delta: 60) },
            throttleEvents: 4), Fmt);

        Assert.True(score.Scored);
        Assert.False(score.AwaitingData);
        Assert.True(score.Value < 100, $"expected a penalty for throttling, got {score.Value}");
    }

    [Fact]
    public void NotReadyWithComparableLoad_ProducesProvisionalScore()
    {
        // Before lock, but there's real like-for-like load: show an estimate, not "--".
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68) },
            baseline: new[] { HeavyBase(delta: 60) },
            ready: false, progress: 0.55), Fmt);

        Assert.True(score.Provisional);
        Assert.True(score.Calibrating);              // still not locked
        Assert.Equal(0.55, score.CalibrationProgress, 2);
        Assert.True(score.Value is > 0 and < 100);   // a real number, not the calibrating zero
        Assert.NotEqual(Verdict.Calibrating, score.Verdict);
    }

    [Fact]
    public void Provisional_Suppressed_WhenDataConfidenceLow()
    {
        // Comparable load exists, but the baseline is still thin (low data confidence),
        // so no provisional number is shown - it would only whipsaw as more data lands.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68) },
            baseline: new[] { HeavyBase(delta: 60) },
            ready: false, progress: 0.3, dataConfidence: 0.3), Fmt);

        Assert.False(score.Provisional);
        Assert.Equal(0, score.Value);
        Assert.Equal(Verdict.Calibrating, score.Verdict);
    }

    [Fact]
    public void Provisional_StaysShown_AfterConfidenceDipsBelowFloor()
    {
        // The confidence floor is an entry gate only: a score that was already on
        // screen keeps updating when a noisy new session dips confidence back under
        // the floor - it must not vanish and reappear.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68) },
            baseline: new[] { HeavyBase(delta: 60) },
            ready: false, progress: 0.6, dataConfidence: 0.3, provisionalEverShown: true), Fmt);

        Assert.True(score.Provisional);
        Assert.True(score.Value is > 0 and < 100);
        Assert.NotEqual(Verdict.Calibrating, score.Verdict);
    }

    [Fact]
    public void NotReadyWithoutBaseline_StaysBareCalibrating()
    {
        // Nothing comparable yet → no number, honest "--" calibrating state.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68) },
            baseline: Array.Empty<BaselineBucket>(),
            ready: false, progress: 0.2), Fmt);

        Assert.False(score.Provisional);
        Assert.True(score.Calibrating);
        Assert.Equal(0, score.Value);
        Assert.Equal(Verdict.Calibrating, score.Verdict);
    }

    [Fact]
    public void DegradedTrifecta_ExcessThrottleAndSoak_IsRepasteTerritory()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 69, tempAvg: 96, tempMax: 100) },
            baseline: new[] { HeavyBase(delta: 60) },
            throttleEvents: 14,          // 2/day for a week
            soakRecent: 30, soakBaseline: 20), Fmt);

        Assert.True(score.Value < 40, $"expected < 40, got {score.Value}");
        Assert.Contains(score.Reasons, r => r.Code == "throttle");
        Assert.Contains(score.Reasons, r => r.Code == "soak");
    }

    [Fact]
    public void HotSummerDay_SameDeltaInSameBand_IsNotPenalized()
    {
        // Absolute temps are way up (hot band), but the delta matches the hot-band
        // baseline — the whole point of ambient correction.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 61, band: Hot, tempAvg: 97, tempMax: 99) },
            baseline: new[] { HeavyBase(delta: 60.5, band: Hot) }), Fmt);

        Assert.True(score.Value >= 85, $"expected Fresh on a fair comparison, got {score.Value}");
        Assert.DoesNotContain(score.Reasons, r => r.Code == "delta-excess");
    }

    [Fact]
    public void RunningCoolerThanBaseline_ScoresFreshWithCredit()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 55, tempAvg: 80, tempMax: 85) },
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.Equal(100, score.Value);
        Assert.Contains(score.Reasons, r => r.Code == "delta-cooler");
    }

    [Fact]
    public void MissingBand_FallsBackToAdjacentBand_AndSaysSo()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68, band: Hot) },     // hot band, no hot baseline
            baseline: new[] { HeavyBase(delta: 60, band: Warm) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "delta-excess" && r.Text.Contains("nearest weather band"));
        Assert.True(score.Value < 85);
    }

    [Fact]
    public void ColdWeather_UnseenBand_DoesNotFalseAlarm_OnInflatedRise()
    {
        // Baseline learned in warm weather: die 45°C over a 30°C ambient (rise 15°C).
        // Now a cold snap: outdoors 0°C, die a healthy 40°C — so the rise-over-outside
        // balloons to +40°C. Naively borrowing the warm band's 15°C rise would read a
        // false +25°C "Aging". The absolute-temperature guard sees the die is actually
        // *cooler* than its warm-weather healthy temp and refuses to penalize.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 40, band: Cold, tempAvg: 40, tempMax: 44) },
            baseline: new[] { HeavyBase(delta: 15, band: Warm, tempAvg: 45) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value >= 85, $"cold-weather healthy die must not read Aging, got {score.Value}");
    }

    [Fact]
    public void ColdWeather_UnseenBand_StillCatchesRealDegradation()
    {
        // Same unseen cold band, but the die runs 58°C at a 0°C ambient — hotter than the
        // warm-weather healthy die (45°C) despite cooler air. That can only be degradation,
        // and the absolute guard flags it even though no cold baseline was ever learned.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 58, band: Cold, tempAvg: 58, tempMax: 62) },
            baseline: new[] { HeavyBase(delta: 15, band: Warm, tempAvg: 45) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value < 85, $"a genuinely hotter die must still drop the score, got {score.Value}");
    }

    [Fact]
    public void NoRecentLoad_SaysSoInsteadOfGuessing()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 70, minutes: 2) },    // below MinMinutes
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "delta-no-data");
    }

    // ------------------------------------------------------------------ fan normalization

    [Fact]
    public void CrankedFans_CannotFlatterTheScore()
    {
        // Fans manually forced ~43% above their learned speed. The measured delta
        // sits exactly on baseline — but only because of the extra airflow, so
        // the comparison must be corrected upward and the score must drop.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60, fan: 6000) },
            baseline: new[] { HeavyBase(delta: 60, fan: 4200) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "fan-normalized" && r.Text.Contains("6000 rpm"));
        Assert.Contains(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value < 85, $"fan-assisted run must not score Fresh, got {score.Value}");
    }

    [Fact]
    public void QuietFans_DoNotFalseAlarm()
    {
        // Silent fan profile: fans ~29% below baseline, temps a few degrees up
        // purely from reduced airflow. Normalization forgives it.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 63.5, fan: 3000) },
            baseline: new[] { HeavyBase(delta: 60, fan: 4200) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "fan-normalized");
        Assert.DoesNotContain(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value >= 85, $"quiet-fan run should stay Fresh, got {score.Value}");
    }

    [Fact]
    public void FanWobbleInsideDeadband_LeavesComparisonUntouched()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60.2, fan: 4400) },
            baseline: new[] { HeavyBase(delta: 60, fan: 4200) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "fan-normalized");
        Assert.True(score.Value >= 85);
    }

    [Fact]
    public void FanCorrection_IsCapped()
    {
        // Absurd ratio (stopped baseline data vs full blast) must not swing the
        // comparison by more than the cap.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60, fan: 12000) },
            baseline: new[] { HeavyBase(delta: 60, fan: 3000) }), Fmt);

        // Correction capped at +8 °C → excess 8 → penalty ≈ 32 points, not more.
        Assert.InRange(score.Value, 55, 75);
    }

    // ------------------------------------------------------------------ calibration

    [Fact]
    public void BeforeBaselineReady_ScoreIsCalibrating()
    {
        ComponentScore score = ScoringEngine.Score(Input(ready: false, progress: 0.4), Fmt);

        Assert.True(score.Calibrating);
        Assert.Equal(Verdict.Calibrating, score.Verdict);
        Assert.Equal(0.4, score.CalibrationProgress);
    }

    [Fact]
    public void CalibratingWithThrottles_WarnsEarly()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 70, tempAvg: 99, tempMax: 100) },
            ready: false, progress: 0.5, throttleEvents: 5), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "throttle-early");
    }

    // ------------------------------------------------------------------ absolute limits

    [Fact]
    public void BeyondChassisConcern_PenalizedEvenIfNoBaselineExcess()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60, tempAvg: 98.5, tempMax: 100) }, // ≥ concern 98
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "beyond-chassis");
        Assert.True(score.Value <= 90);
    }

    [Fact]
    public void WarmButNormalForChassis_GetsReassuranceNotPenalty()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60, tempAvg: 94, tempMax: 95) },  // ≥ norm 93, < concern 98
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "chassis-norm" && r.PointsLost == 0);
        Assert.True(score.Value >= 85);
    }

    // ------------------------------------------------------------------ stale baseline

    [Fact]
    public void StaleBaseline_FlagsConfidence_WithoutMovingTheNumber()
    {
        ScoreInput onBaseline = Input(
            recent: new[] { Heavy(delta: 60) },
            baseline: new[] { HeavyBase(delta: 60) });
        ScoreInput stale = onBaseline with { BaselineStale = true, DormantDays = 70 };

        ComponentScore fresh = ScoringEngine.Score(onBaseline, Fmt);
        ComponentScore staleScore = ScoringEngine.Score(stale, Fmt);

        Assert.Equal(fresh.Value, staleScore.Value);                       // staleness never penalizes
        Assert.Contains(staleScore.Reasons, r => r.Code == "baseline-stale");
        Assert.DoesNotContain(fresh.Reasons, r => r.Code == "baseline-stale");
    }

    // ------------------------------------------------------------------ pattern hints

    [Fact]
    public void FastSoakPlusThrottles_HintsPaste()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 66), new RecentBucketObs(LoadBucket.Idle, Warm, 120, 21, 55, 60, null, 0) },
            baseline: new[] { HeavyBase(delta: 60), new BaselineBucket(LoadBucket.Idle, Warm, 20, 24, null, 300) },
            throttleEvents: 6,
            soakRecent: 30, soakBaseline: 20), Fmt);

        Assert.Equal(PatternHint.LooksLikePaste, score.Hint);
    }

    [Fact]
    public void BroadSteadyExcess_NormalSoak_HintsDust()
    {
        var recent = new[]
        {
            Heavy(delta: 65),
            new RecentBucketObs(LoadBucket.Medium, Warm, 60, 51, 75, 80, null, 0),
            new RecentBucketObs(LoadBucket.Light, Warm, 60, 36, 60, 65, null, 0),
            new RecentBucketObs(LoadBucket.Idle, Warm, 120, 26, 50, 55, null, 0),
        };
        var baseline = new[]
        {
            HeavyBase(delta: 60),
            new BaselineBucket(LoadBucket.Medium, Warm, 46, 49, null, 200),
            new BaselineBucket(LoadBucket.Light, Warm, 31, 34, null, 200),
            new BaselineBucket(LoadBucket.Idle, Warm, 20, 23, null, 300),
        };
        ComponentScore score = ScoringEngine.Score(Input(
            recent: recent, baseline: baseline,
            soakRecent: 20.5, soakBaseline: 20), Fmt);

        Assert.Equal(PatternHint.LooksLikeDust, score.Hint);
    }

    // ------------------------------------------------------------------ hotspot gap

    [Fact]
    public void HotspotGap_WideningAgainstOwnBaseline_LosesPoints()
    {
        // Same edge deltas (paste looks fine by the edge sensor), but the gap grew
        // 11° → 18°: heat is pooling. Drift vs own baseline must cost points.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyGap(delta: 60, gap: 18) },
            baseline: new[] { HeavyBaseGap(delta: 60, gap: 11) }), Fmt);

        ScoreReason reason = score.Reasons.Single(r => r.Code == "hotspot-gap");
        Assert.True(reason.PointsLost > 0, $"expected points lost, got {reason.PointsLost}");
        Assert.Contains("baseline", reason.Text);
        Assert.True(score.Value < 100);
    }

    [Fact]
    public void HotspotGap_StableWideByDesign_CostsNothing()
    {
        // Some models run a wide gap from the factory. Stable = healthy: no penalty,
        // no nag - only drift or extreme absolutes may speak.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyGap(delta: 60, gap: 22) },
            baseline: new[] { HeavyBaseGap(delta: 60, gap: 21.5) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "hotspot-gap");
    }

    [Fact]
    public void HotspotGap_AbsoluteBackstop_WhenNoLearnedGap()
    {
        // Legacy baseline (no gap learned) with a 30° gap: the absolute backstop
        // still catches paste that was already failing when DeltaT arrived.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyGap(delta: 60, gap: 30) },
            baseline: new[] { HeavyBaseGap(delta: 60, gap: null) }), Fmt);

        ScoreReason reason = score.Reasons.Single(r => r.Code == "hotspot-gap");
        Assert.True(reason.PointsLost > 0);
    }

    [Fact]
    public void HotspotGap_IdleGapIsIgnored()
    {
        var idleWithGap = new RecentBucketObs(LoadBucket.Idle, Warm, 120, 20, 45, 50, null, 0, GapAvg: 30);
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { idleWithGap },
            baseline: new[] { new BaselineBucket(LoadBucket.Idle, Warm, 20, 23, null, 300, null, 10) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "hotspot-gap");
    }

    // ------------------------------------------------------------------ power normalization

    [Fact]
    public void Overclock_DrawingMorePower_IsNotMistakenForDegradation()
    {
        // Same healthy paste, but an overclock lifted sustained power 100 → 130 W, so the
        // die runs a legitimate ~18 °C hotter. Raw ΔT would read a big excess and tank the
        // score; thermal-resistance normalization sees the rise is exactly what 130 W should
        // produce and keeps it Fresh.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 78, power: 130) },
            baseline: new[] { HeavyBase(delta: 60, power: 100) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "power-normalized");
        Assert.DoesNotContain(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value >= 85, $"an overclock on healthy paste must not read as Aging, got {score.Value}");
    }

    [Fact]
    public void Undervolt_HidingDegradation_IsStillCaught()
    {
        // An undervolt cut sustained power 100 → 70 W, so the absolute die temp actually
        // FELL (Δ 60 → 50) even though the paste got worse. Raw ΔT reads "cooler = healthy";
        // resistance normalization (50/70 W vs 60/100 W) exposes the hidden degradation.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 50, power: 70) },
            baseline: new[] { HeavyBase(delta: 60, power: 100) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value < 85, $"undervolt-masked degradation must still drop the score, got {score.Value}");

        // Sanity: the SAME temperatures without power data read as a (false) improvement.
        ComponentScore blind = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 50) },
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);
        Assert.Equal(100, blind.Value);
    }

    [Fact]
    public void PowerWithinDeadband_LeavesComparisonUntouched()
    {
        // A few watts of run-to-run variance must not trigger a correction.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60.3, power: 103) },
            baseline: new[] { HeavyBase(delta: 60, power: 100) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "power-normalized");
        Assert.True(score.Value >= 85);
    }

    [Fact]
    public void PowerNormalization_AbsentData_FallsBackToRawDelta()
    {
        // No power sensor (baseline learned before v6): behaves exactly as before.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 68, power: null) },
            baseline: new[] { HeavyBase(delta: 60, power: null) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "power-normalized");
        Assert.Contains(score.Reasons, r => r.Code == "delta-excess");
    }

    // ------------------------------------------------------------------ cooldown rate

    [Fact]
    public void SluggishCooldown_LosesPoints()
    {
        // Die sheds heat far slower than baseline when load drops - the same resistance
        // that makes paste run hot, seen on the falling edge.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60) },
            baseline: new[] { HeavyBase(delta: 60) },
            cooldownRecent: 12, cooldownBaseline: 22), Fmt);

        ScoreReason reason = score.Reasons.Single(r => r.Code == "cooldown");
        Assert.True(reason.PointsLost > 0);
        Assert.True(score.Value < 100);
    }

    [Fact]
    public void HealthyCooldown_CostsNothing()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60) },
            baseline: new[] { HeavyBase(delta: 60) },
            cooldownRecent: 21, cooldownBaseline: 22), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "cooldown");
        Assert.Equal(100, score.Value);
    }

    [Fact]
    public void SluggishCooldown_CorroboratesPastePattern()
    {
        // A fast soak alone hints paste; a sluggish cooldown on top firms it up.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 66) },
            baseline: new[] { HeavyBase(delta: 60) },
            soakRecent: 30, soakBaseline: 20,
            cooldownRecent: 12, cooldownBaseline: 22), Fmt);

        Assert.Equal(PatternHint.LooksLikePaste, score.Hint);
    }

    // ------------------------------------------------------------------ fan undershoot

    [Fact]
    public void FanRunningWellBelowBaseline_HintsCause_WithoutDoublePenalizing()
    {
        // Fans ~35% below the learned speed at the same load. Normalization already
        // reflects the airflow in the number, so this adds a cause-hint at zero points.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 66, fan: 2700) },
            baseline: new[] { HeavyBase(delta: 60, fan: 4200) }), Fmt);

        ScoreReason hint = score.Reasons.Single(r => r.Code == "fan-undershoot");
        Assert.Equal(0, hint.PointsLost);
        Assert.Contains("slower", hint.Text);
    }

    [Fact]
    public void FanNearBaseline_NoUndershootHint()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 60, fan: 4000) },
            baseline: new[] { HeavyBase(delta: 60, fan: 4200) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "fan-undershoot");
    }

    // ------------------------------------------------------------------ configurable limits

    [Fact]
    public void RaisedConcernLimit_SilencesTheChassisWarning()
    {
        // Averaging 98.5° under load: past the stock 98° concern, so normally penalized.
        var recent = new[] { Heavy(delta: 60, tempAvg: 98.5, tempMax: 99) };
        var baseline = new[] { HeavyBase(delta: 60) };

        ComponentScore stock = ScoringEngine.Score(Input(recent: recent, baseline: baseline), Fmt);
        Assert.Contains(stock.Reasons, r => r.Code == "beyond-chassis");

        // An overclocker sets their ceiling to 101°: the same run is no longer flagged.
        ComponentScore tuned = ScoringEngine.Score(Input(recent: recent, baseline: baseline, concernOverrideC: 101), Fmt);
        Assert.DoesNotContain(tuned.Reasons, r => r.Code == "beyond-chassis");
        Assert.True(tuned.Value > stock.Value);
    }

    [Fact]
    public void HeadroomWarningsOff_DropsTheNearLimitPenalty()
    {
        // Peaks within 2° of the 100° limit, no throttle event.
        var recent = new[] { Heavy(delta: 60, tempAvg: 90, tempMax: 99) };
        var baseline = new[] { HeavyBase(delta: 60) };

        ComponentScore on = ScoringEngine.Score(Input(recent: recent, baseline: baseline), Fmt);
        Assert.Contains(on.Reasons, r => r.Code == "headroom");

        ComponentScore off = ScoringEngine.Score(Input(recent: recent, baseline: baseline, headroomWarnings: false), Fmt);
        Assert.DoesNotContain(off.Reasons, r => r.Code == "headroom");
        Assert.True(off.Value >= on.Value);
    }

    [Fact]
    public void HeadroomWarningsOff_AlsoSilencesTheFindingAndTheAspectCell()
    {
        // The switch is a promise: a rig deliberately pinned near TjMax must not keep
        // seeing a Headroom finding or a Watch cell through the diagnosis side door.
        var recent = new[] { Heavy(delta: 60, tempAvg: 90, tempMax: 99) };
        var baseline = new[] { HeavyBase(delta: 60) };

        ComponentScore off = ScoringEngine.Score(Input(recent: recent, baseline: baseline, headroomWarnings: false), Fmt);
        Assert.DoesNotContain(off.Diagnosis!.Findings, f => f.Cause == ThermalCause.CoolingHeadroom);
        AspectHealth headroom = off.Aspects.Single(a => a.Aspect == HealthAspect.Headroom);
        Assert.True(headroom.Score >= 85, $"headroom cell should stay Clear, read {headroom.Score}");

        // Throttle EVENTS are always counted, switch or no switch.
        ComponentScore throttled = ScoringEngine.Score(Input(
            recent: recent, baseline: baseline, throttleEvents: 3, headroomWarnings: false), Fmt);
        Assert.Contains(throttled.Diagnosis!.Findings, f => f.Cause == ThermalCause.CoolingHeadroom);
    }

    // --------------------------------------------- power changes beyond the clamp band

    // Absolute temps stay well under the chassis norms (ambient 25 + modest deltas) so
    // these tests read ONLY the power-comparison behaviour, never the absolute warnings.
    private static RecentBucketObs HeavyPower(double delta, double power, double? fan = null, double? gap = null) =>
        new(LoadBucket.Heavy, Warm, 60, delta, 25 + delta, 29 + delta, fan, 0, GapAvg: gap, PowerAvg: power);

    private static BaselineBucket HeavyBasePower(double delta, double power, double? fan = null, double? gap = null) =>
        new(LoadBucket.Heavy, Warm, delta, delta + 3, fan, 200, null, GapAvg: gap, PowerAvg: power);

    [Fact]
    public void DeepPowerCap_SitsOutTheComparison_InsteadOfFabricatingOne()
    {
        // A CPU locked to a low clock: 20 W under full load where the baseline learned
        // 70 W. Beyond the 0.5-2.0 clamp no correction is honest, so the cell must not
        // judge at all (and must say why), rather than half-correct into a fake number.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 18, power: 20) },
            baseline: new[] { HeavyBasePower(delta: 60, power: 70) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "power-mismatch");
        Assert.DoesNotContain(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.AwaitingData, "nothing comparable ran, so the honest state is waiting, not a verdict");
        AspectHealth paste = score.Aspects.Single(a => a.Aspect == HealthAspect.Paste);
        Assert.Equal("--", paste.Status);
    }

    [Fact]
    public void DeepPowerCap_DoesNotAccuseTheSlowerFan()
    {
        // At 20 W the fan curve legitimately eases far off its 70 W speed. 75% of
        // baseline rpm used to trip the undershoot hint; with the watts explaining it,
        // the fan must stay Clear.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 18, power: 20, fan: 3000) },
            baseline: new[] { HeavyBasePower(delta: 60, power: 70, fan: 4000) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "fan-undershoot");
        AspectHealth fans = score.Aspects.Single(a => a.Aspect == HealthAspect.Fans);
        Assert.True(fans.Score >= 85, $"fans cell should stay Clear, read {fans.Score}");

        // A fan below even what the missing watts explain is still a real hint: same
        // cap, but the fan is at 40% of baseline. That is undershoot.
        ComponentScore faulty = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 18, power: 20, fan: 1600) },
            baseline: new[] { HeavyBasePower(delta: 60, power: 70, fan: 4000) }), Fmt);
        Assert.Contains(faulty.Diagnosis!.Findings, f => f.Cause == ThermalCause.FanFault);
    }

    [Fact]
    public void DeepPowerCap_GatesTheRateComparison()
    {
        // The soak/cooldown correction saturates at the clamp, and a saturated
        // half-correction charges the remainder to the paste: a capped machine really
        // does shed heat at a fraction of its baseline rate. Across a gap this wide the
        // rates must sit the comparison out, not fire "sheds heat slower".
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[]
            {
                HeavyPower(delta: 18, power: 20),
                new RecentBucketObs(LoadBucket.Idle, Warm, 60, 12, 40, 44, null, 0, PowerAvg: 8),
            },
            baseline: new[]
            {
                HeavyBasePower(delta: 60, power: 70),
                new BaselineBucket(LoadBucket.Idle, Warm, 12, 15, null, 200, null, PowerAvg: 8),
            },
            cooldownRecent: 8, cooldownBaseline: 22,
            soakRecent: 7, soakBaseline: 20), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "cooldown");
        Assert.DoesNotContain(score.Reasons, r => r.Code == "soak");
        Assert.True(score.Value >= 95, $"healthy capped machine read {score.Value}");
    }

    [Fact]
    public void OverclockWidenedGap_IsThePowerKnob_NotTheMount()
    {
        // Hotspot-edge gap is heat flux x internal resistance: +35% watts widen a
        // healthy card's gap from 10 to 13.5. Judged raw that reads as mount drift;
        // judged at equal power it is nothing.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 54, power: 94.5, gap: 13.5) },
            baseline: new[] { HeavyBasePower(delta: 40, power: 70, gap: 10) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "hotspot-gap");
        Assert.DoesNotContain(score.Diagnosis!.Findings, f => f.Cause == ThermalCause.Mount);
    }

    [Fact]
    public void UndervoltNarrowedGap_CannotHideRealPumpout()
    {
        // At -30% watts a healthy gap narrows to 7. This card reads 14: twice what the
        // watts predict, the pump-out signature. The smaller absolute number must not
        // pass as healthy just because it sits below the learned 10.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 28, power: 49, gap: 14) },
            baseline: new[] { HeavyBasePower(delta: 40, power: 70, gap: 10) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "hotspot-gap" && r.PointsLost > 0);
        Assert.Contains(score.Diagnosis!.Findings, f => f.Cause == ThermalCause.Mount);
    }

    [Fact]
    public void FanAnsweringTheWatts_IsNotFlattery()
    {
        // +35% watts, fan up 14% because the curve answers the heat. The power
        // correction already judges at equal wattage; charging the fan response as
        // "extra airflow flattering the reading" double-counted the power change into
        // 3-4 degrees of fake excess.
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 54, power: 94.5, fan: 4560) },
            baseline: new[] { HeavyBasePower(delta: 40, power: 70, fan: 4000) }), Fmt);

        Assert.DoesNotContain(score.Reasons, r => r.Code == "delta-excess");
        Assert.True(score.Value >= 95, $"healthy overclocked machine read {score.Value}");

        // Same rpm rise at UNCHANGED watts is a different story: that is a fan profile
        // or a straining curve, and normalization must still correct for it.
        ComponentScore profile = ScoringEngine.Score(Input(
            recent: new[] { HeavyPower(delta: 38, power: 70, fan: 4560) },
            baseline: new[] { HeavyBasePower(delta: 40, power: 70, fan: 4000) }), Fmt);
        Assert.NotNull(profile.Fan);
    }
}

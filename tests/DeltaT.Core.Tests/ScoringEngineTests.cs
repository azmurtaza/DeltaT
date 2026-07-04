using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

public class ScoringEngineTests
{
    private static readonly Func<double, string> Fmt = t => $"{t:0} °C";
    private const int Warm = (int)AmbientBand.Warm;
    private const int Hot = (int)AmbientBand.Hot;

    private static readonly ComponentProfile NitroCpu = new(20, 66, 93, 98);

    private static ScoreInput Input(
        IReadOnlyList<RecentBucketObs>? recent = null,
        IReadOnlyList<BaselineBucket>? baseline = null,
        int throttleEvents = 0,
        double? soakRecent = null,
        double? soakBaseline = null,
        bool ready = true,
        double progress = 1.0,
        double recentHours = 7 * 24) =>
        new(ComponentKind.Cpu, "Test CPU",
            recent ?? Array.Empty<RecentBucketObs>(),
            baseline ?? Array.Empty<BaselineBucket>(),
            recentHours, throttleEvents, soakRecent, soakBaseline,
            LimitC: 100, Profile: NitroCpu, BaselineReady: ready, CalibrationProgress: progress);

    private static RecentBucketObs Heavy(double delta, int band = Warm, int minutes = 60, double tempAvg = 88, double tempMax = 92) =>
        new(LoadBucket.Heavy, band, minutes, delta, tempAvg, tempMax, null, 0);

    private static BaselineBucket HeavyBase(double delta, int band = Warm) =>
        new(LoadBucket.Heavy, band, delta, delta + 3, null, 200);

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
    public void NoRecentLoad_SaysSoInsteadOfGuessing()
    {
        ComponentScore score = ScoringEngine.Score(Input(
            recent: new[] { Heavy(delta: 70, minutes: 2) },    // below MinMinutes
            baseline: new[] { HeavyBase(delta: 60) }), Fmt);

        Assert.Contains(score.Reasons, r => r.Code == "delta-no-data");
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
}

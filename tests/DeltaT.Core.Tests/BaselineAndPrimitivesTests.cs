using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using Xunit;

namespace DeltaT.Core.Tests;

public class BaselineBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    // A "now" far enough past the epoch start that the cure term is 1.0 and can't cap
    // confidence — so these tests exercise the data-confidence half in isolation.
    private static readonly DateTimeOffset Cured = Start.AddHours(120);

    private static BucketStat Stat(LoadBucket bucket, int minutes, double? delta = 60, int band = 2) =>
        new(bucket, band, true, minutes, minutes * 30, 85, 60, 95, 90, delta, null, 0);

    /// <summary>Session-mean provider that returns <paramref name="means"/> for the
    /// Heavy/band-2 cell and nothing for anything else.</summary>
    private static Func<LoadBucket, int, IReadOnlyList<double>> HeavyMeans(params double[] means) =>
        (bucket, band) => bucket == LoadBucket.Heavy && band == 2 ? means : Array.Empty<double>();

    [Fact]
    public void NotReady_WhileCuring_EvenWithPerfectData()
    {
        // Great, tight data on day one — but the freshly applied paste hasn't settled,
        // so confidence is capped. Only applies to repaste epochs (pasteIsFresh).
        var stats = new[] { Stat(LoadBucket.Heavy, 500) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Start.AddHours(24), stats,
            HeavyMeans(60, 60.1, 59.9, 60.05), independentLoadedSessions: 4, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.True(cal.Confidence <= 0.11);           // held down by the cure floor
        Assert.Contains("settling", cal.Constraint);
    }

    [Fact]
    public void Ready_OnDayOne_WhenPasteIsNotFresh()
    {
        // First install / recalibration: the paste is as cured as it will ever be, so
        // the clock must not gate an otherwise-earned lock.
        var stats = new[] { Stat(LoadBucket.Heavy, 500) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Start.AddHours(24), stats,
            HeavyMeans(60, 60.1, 59.9, 60.05), independentLoadedSessions: 4, pasteIsFresh: false);

        Assert.True(cal.Ready);
    }

    [Fact]
    public void NotReady_WithoutLoadedSessions_EvenAfterCuring()
    {
        // Idle-only history can never define a paste baseline.
        var stats = new[] { Stat(LoadBucket.Idle, 5000) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Start.AddDays(31), stats,
            (_, _) => Array.Empty<double>(), independentLoadedSessions: 0, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.Equal(0, cal.DataConfidence);
        Assert.Contains("load", cal.Constraint);
    }

    [Fact]
    public void NotReady_WithTooFewSessions()
    {
        // Cured and tight, but only two independent sessions — variance can't be trusted yet.
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            HeavyMeans(60, 60.1), independentLoadedSessions: 2, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.Equal(2, cal.LoadedSessions);
        Assert.Contains("session", cal.Constraint);
    }

    [Fact]
    public void Ready_WhenCuredAndPrecise()
    {
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            HeavyMeans(60, 60.2, 59.8, 60.1, 59.9), independentLoadedSessions: 5, pasteIsFresh: true); // 5 tight sessions

        Assert.True(cal.Ready);
        Assert.True(cal.Confidence >= BaselineBuilder.ReadyConfidence);
    }

    [Fact]
    public void SessionGate_UsesDeduplicatedCount_NotPerCellSums()
    {
        // One evening of gaming shows up as sessions in BOTH the Heavy and Medium
        // cells; the independence gate must count it once. With only one real bout,
        // even tight per-cell means can't unlock.
        var stats = new[] { Stat(LoadBucket.Heavy, 200), Stat(LoadBucket.Medium, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            (_, _) => new[] { 60.0, 60.1, 59.9 }, independentLoadedSessions: 1, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.Equal(1, cal.LoadedSessions);
    }

    [Fact]
    public void Confidence_IsHigherForTighterData()
    {
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        double tight = BaselineBuilder.Assess(Start, Cured, stats, HeavyMeans(60, 60.2, 59.8, 60.1),
            independentLoadedSessions: 4, pasteIsFresh: true).DataConfidence;
        double noisy = BaselineBuilder.Assess(Start, Cured, stats, HeavyMeans(50, 60, 70, 55),
            independentLoadedSessions: 4, pasteIsFresh: true).DataConfidence;

        Assert.True(tight > noisy);
        Assert.True(noisy < BaselineBuilder.ReadyConfidence); // scattered readings never lock
    }

    [Theory]
    [InlineData(24, 0.10)]   // before the floor: capped low
    [InlineData(72, 0.55)]   // halfway through the ramp
    [InlineData(96, 1.00)]   // fully cured
    [InlineData(200, 1.00)]  // stays at 1
    public void CureMaturity_RampsFromFloorToFull(double hours, double expected) =>
        Assert.Equal(expected, BaselineBuilder.CureMaturity(Start, Start.AddHours(hours)), 2);

    [Fact]
    public void StandardError_NullBelowTwoSamples_ElseShrinksWithN()
    {
        Assert.Null(BaselineBuilder.StandardError(Array.Empty<double>()));
        Assert.Null(BaselineBuilder.StandardError(new[] { 42.0 }));
        // {58,60,62}: sd = 2, SE = 2/sqrt(3) ≈ 1.1547
        Assert.Equal(1.1547, BaselineBuilder.StandardError(new[] { 58.0, 60, 62 })!.Value, 3);
    }

    [Fact]
    public void Build_SkipsThinCells_UnknownBands_AndNullDeltas_AndCarriesSe()
    {
        var stats = new[]
        {
            Stat(LoadBucket.Heavy, 60),                       // kept
            Stat(LoadBucket.Medium, 3),                       // too thin
            Stat(LoadBucket.Light, 60, delta: null),          // no ambient → no delta
            Stat(LoadBucket.Idle, 60, band: -1),              // unknown band
        };
        var rows = BaselineBuilder.Build(0, ComponentKind.Cpu, "cpu", stats,
            minuteDeltasFor: (_, _) => new[] { 58.0, 59, 60, 61, 62 },
            sessionMeansFor: (_, _) => new[] { 58.0, 60, 62 },
            soakRateAvg: 18, Cured);

        BaselineRow row = Assert.Single(rows);
        Assert.Equal(LoadBucket.Heavy, row.Bucket);
        Assert.Equal(62, row.DeltaP95); // p95 of the 5-value distribution
        Assert.Equal(18, row.SoakRate);
        Assert.Equal(1.1547, row.DeltaSe!.Value, 3); // SE of the 3 session means
    }

    [Fact]
    public void Percentile_Empty_IsNull_AndSingle_IsThatValue()
    {
        Assert.Null(BaselineBuilder.Percentile(Array.Empty<double>(), 0.95));
        Assert.Equal(42, BaselineBuilder.Percentile(new[] { 42.0 }, 0.95));
    }
}

public class PrimitivesTests
{
    [Theory]
    [InlineData(0, LoadBucket.Idle)]
    [InlineData(9.9, LoadBucket.Idle)]
    [InlineData(10, LoadBucket.Light)]
    [InlineData(39.9, LoadBucket.Light)]
    [InlineData(40, LoadBucket.Medium)]
    [InlineData(69.9, LoadBucket.Medium)]
    [InlineData(70, LoadBucket.Heavy)]
    [InlineData(100, LoadBucket.Heavy)]
    public void LoadBuckets_EdgesLandWhereDocumented(double pct, LoadBucket expected) =>
        Assert.Equal(expected, LoadBuckets.FromPercent(pct));

    [Theory]
    [InlineData(-5, AmbientBand.Cold)]
    [InlineData(14.9, AmbientBand.Cold)]
    [InlineData(15, AmbientBand.Mild)]
    [InlineData(24.9, AmbientBand.Mild)]
    [InlineData(25, AmbientBand.Warm)]
    [InlineData(34.9, AmbientBand.Warm)]
    [InlineData(35, AmbientBand.Hot)]
    [InlineData(48, AmbientBand.Hot)]
    public void AmbientBands_EdgesLandWhereDocumented(double c, AmbientBand expected) =>
        Assert.Equal(expected, AmbientBands.FromCelsius(c));

    [Fact]
    public void OnlyCpuAndDiscreteGpu_HavePaste()
    {
        Assert.True(ComponentKind.Cpu.HasPaste());
        Assert.True(ComponentKind.GpuDiscrete.HasPaste());
        Assert.False(ComponentKind.GpuIntegrated.HasPaste());
        Assert.False(ComponentKind.Storage.HasPaste());
        Assert.False(ComponentKind.Battery.HasPaste());
    }
}

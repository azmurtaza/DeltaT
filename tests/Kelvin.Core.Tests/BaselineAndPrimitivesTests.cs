using Kelvin.Core.Monitoring;
using Kelvin.Core.Scoring;
using Kelvin.Core.Storage;
using Xunit;

namespace Kelvin.Core.Tests;

public class BaselineBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static BucketStat Stat(LoadBucket bucket, int minutes, double? delta = 60, int band = 2) =>
        new(bucket, band, true, minutes, minutes * 30, 85, 60, 95, 90, delta, null, 0);

    [Fact]
    public void NotReady_BeforeMinDays_EvenWithPlentyOfLoad()
    {
        var stats = new[] { Stat(LoadBucket.Heavy, 500) };
        Assert.False(BaselineBuilder.IsReady(Start, Start.AddDays(3), stats));
    }

    [Fact]
    public void NotReady_WithoutEnoughLoadedMinutes_EvenAfterAWeek()
    {
        var stats = new[] { Stat(LoadBucket.Idle, 5000), Stat(LoadBucket.Heavy, 20) };
        Assert.False(BaselineBuilder.IsReady(Start, Start.AddDays(9), stats));
    }

    [Fact]
    public void Ready_WhenDaysAndLoadBothSuffice()
    {
        var stats = new[] { Stat(LoadBucket.Heavy, 60), Stat(LoadBucket.Medium, 40) };
        Assert.True(BaselineBuilder.IsReady(Start, Start.AddDays(6), stats));
    }

    [Fact]
    public void Progress_BlendsDaysAndLoad()
    {
        var none = Array.Empty<BucketStat>();
        Assert.Equal(0.6, BaselineBuilder.Progress(Start, Start.AddDays(5), none), 2);
        var full = new[] { Stat(LoadBucket.Heavy, 90) };
        Assert.Equal(1.0, BaselineBuilder.Progress(Start, Start.AddDays(5), full), 2);
    }

    [Fact]
    public void Build_SkipsThinCells_UnknownBands_AndNullDeltas()
    {
        var stats = new[]
        {
            Stat(LoadBucket.Heavy, 60),                       // kept
            Stat(LoadBucket.Medium, 3),                       // too thin
            Stat(LoadBucket.Light, 60, delta: null),          // no ambient → no delta
            Stat(LoadBucket.Idle, 60, band: -1),              // unknown band
        };
        var rows = BaselineBuilder.Build(0, ComponentKind.Cpu, "cpu", stats,
            (_, _) => new[] { 58.0, 59, 60, 61, 62 }, soakRateAvg: 18, Start.AddDays(7));

        BaselineRow row = Assert.Single(rows);
        Assert.Equal(LoadBucket.Heavy, row.Bucket);
        Assert.Equal(62, row.DeltaP95); // p95 of the 5-value distribution
        Assert.Equal(18, row.SoakRate);
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

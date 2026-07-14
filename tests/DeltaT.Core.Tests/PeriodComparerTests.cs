using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using Xunit;

namespace DeltaT.Core.Tests;

public class PeriodComparerTests
{
    private const int Mild = (int)AmbientBand.Mild;
    private const int Warm = (int)AmbientBand.Warm;

    private static BucketStat Stat(LoadBucket bucket, int band, double delta, int minutes,
        double? fan = null, bool onAc = true) =>
        new(bucket, band, onAc, minutes, SampleCount: minutes * 30,
            TempAvg: 20 + delta, TempMin: 20 + delta - 2, TempMax: 20 + delta + 2,
            LoadAvg: 80, DeltaAvg: delta, FanAvg: fan, ThrottleCount: 0);

    [Fact]
    public void HotterRecent_ReportsPositiveChange()
    {
        var earlier = new[] { Stat(LoadBucket.Heavy, Mild, 40, 60), Stat(LoadBucket.Max, Mild, 48, 60) };
        var recent = new[] { Stat(LoadBucket.Heavy, Mild, 46, 60), Stat(LoadBucket.Max, Mild, 54, 60) };

        PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

        Assert.Equal(Mild, cmp.Band);
        Assert.NotNull(cmp.WeightedChangeC);
        Assert.True(cmp.WeightedChangeC > 4, $"expected a clear positive drift, got {cmp.WeightedChangeC}");
        Assert.True(cmp.MatchedLoadedMinutes >= PeriodComparer.MinMatchedLoadedMinutes);
    }

    [Fact]
    public void UnchangedPeriods_ReportNearZero()
    {
        var earlier = new[] { Stat(LoadBucket.Heavy, Mild, 42, 60) };
        var recent = new[] { Stat(LoadBucket.Heavy, Mild, 42, 60) };

        PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

        Assert.NotNull(cmp.WeightedChangeC);
        Assert.True(Math.Abs(cmp.WeightedChangeC!.Value) < 0.5);
    }

    [Fact]
    public void TooLittleMatchedLoad_ReturnsNullChange()
    {
        // Only a handful of matched loaded minutes, below the confidence floor.
        var earlier = new[] { Stat(LoadBucket.Heavy, Mild, 40, 5) };
        var recent = new[] { Stat(LoadBucket.Heavy, Mild, 50, 5) };

        PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

        Assert.Null(cmp.WeightedChangeC);
    }

    [Fact]
    public void MoreAirflowSameRawDelta_ReadsAsHotter_WhenFanNormalized()
    {
        // Same measured rise, but the recent period spun the fan much harder to hold it
        // there — normalised to the old airflow, the machine is actually running hotter.
        var earlier = new[] { Stat(LoadBucket.Heavy, Mild, 40, 60, fan: 3000) };
        var recent = new[] { Stat(LoadBucket.Heavy, Mild, 40, 60, fan: 4200) };

        PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

        Assert.True(cmp.FanCorrected);
        Assert.NotNull(cmp.WeightedChangeC);
        Assert.True(cmp.WeightedChangeC > 1, $"fan-normalised change should be positive, got {cmp.WeightedChangeC}");
    }

    [Fact]
    public void ChoosesTheBandBothPeriodsShareTheMostLoad()
    {
        // Loaded data overlaps only in Warm; Mild has loaded data in the earlier period
        // alone, so it is not a fair comparison band.
        var earlier = new[]
        {
            Stat(LoadBucket.Heavy, Mild, 44, 40),
            Stat(LoadBucket.Heavy, Warm, 50, 40),
        };
        var recent = new[] { Stat(LoadBucket.Heavy, Warm, 53, 40) };

        PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

        Assert.Equal(Warm, cmp.Band);
    }

    [Fact]
    public void NoLoadedData_IsInconclusive()
    {
        var earlier = new[] { Stat(LoadBucket.Idle, Mild, 12, 100) };
        var recent = new[] { Stat(LoadBucket.Light, Mild, 18, 100) };

        PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

        Assert.True(cmp.Band < 0);
        Assert.Null(cmp.WeightedChangeC);
        Assert.Empty(cmp.Points);
    }
}

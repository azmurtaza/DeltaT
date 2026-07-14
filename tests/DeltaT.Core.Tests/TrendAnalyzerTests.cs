using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using Xunit;

namespace DeltaT.Core.Tests;

public class TrendAnalyzerTests
{
    private const long Week = 604800;
    private const long Base = Week * 3000; // arbitrary anchor

    private static IReadOnlyList<WeeklyExcess> Series(params double[] excesses)
    {
        var list = new List<WeeklyExcess>();
        for (int i = 0; i < excesses.Length; i++)
            list.Add(new WeeklyExcess(Base + i * Week, excesses[i], WeightMinutes: 240));
        return list;
    }

    [Fact]
    public void RisingSeries_IsFlaggedAsTrend_WithProjection()
    {
        // +0.5 °C/week ≈ +2.1 °C/month, currently 2.5 °C over baseline.
        TrendResult r = TrendAnalyzer.Analyze(Series(0, 0.5, 1, 1.5, 2, 2.5));

        Assert.True(r.HasTrend);
        Assert.True(r.SlopePerMonthC > TrendAnalyzer.MinSlopePerMonthC);
        Assert.InRange(r.CurrentExcessC, 2.0, 3.0);
        Assert.NotNull(r.MonthsToConcern);
        Assert.True(r.MonthsToConcern > 0);
    }

    [Fact]
    public void FlatNoisySeries_IsNotATrend()
    {
        TrendResult r = TrendAnalyzer.Analyze(Series(1, -1, 0.5, -0.5, 1, -1, 0.3));

        Assert.False(r.HasTrend);
        Assert.Null(r.MonthsToConcern);
    }

    [Fact]
    public void CoolingSeries_TrendsButDoesNotProjectConcern()
    {
        TrendResult r = TrendAnalyzer.Analyze(Series(3, 2.5, 2, 1.5, 1));

        Assert.True(r.HasTrend);
        Assert.True(r.SlopePerMonthC < 0);
        Assert.Null(r.MonthsToConcern); // nothing to count down to when it's improving
    }

    [Fact]
    public void TooFewWeeks_YieldsNoTrend()
    {
        TrendResult r = TrendAnalyzer.Analyze(Series(0, 2, 4)); // below MinWeeks
        Assert.False(r.HasTrend);
        Assert.Null(r.Step);
    }

    [Fact]
    public void StepChange_IsDetectedAtTheRightWeek()
    {
        // Flat at ~0, then jumps to ~5 and holds: a discrete event, not a ramp.
        TrendResult r = TrendAnalyzer.Analyze(Series(0.2, -0.1, 0.1, 5.0, 5.1, 4.9));

        Assert.NotNull(r.Step);
        Assert.InRange(r.Step!.JumpC, 4.0, 6.0);
        Assert.Equal(Base + 3 * Week, r.Step.AtWeekStartTs); // split before the 4th week
    }

    [Fact]
    public void SmallWobble_IsNotAStep()
    {
        TrendResult r = TrendAnalyzer.Analyze(Series(0, 0.5, 0, 0.5, 1, 0.5, 1));
        Assert.Null(r.Step);
    }

    [Fact]
    public void BuildWeekly_MatchesBandsAndDropsUnlearnedWeather()
    {
        var baseline = new List<BaselineRow>
        {
            new(0, ComponentKind.Cpu, "CPU", (int)AmbientBand.Warm, LoadBucket.Heavy, 60, null, null, null, 200, 0),
        };
        var cells = new List<WeeklyLoadedCell>
        {
            new(Base, LoadBucket.Heavy, (int)AmbientBand.Warm, 120, 60),          // week 0, on baseline
            new(Base + Week, LoadBucket.Heavy, (int)AmbientBand.Warm, 120, 66),   // week 1, +6 °C
            new(Base + Week, LoadBucket.Heavy, (int)AmbientBand.Cold, 120, 40),   // no cold baseline → dropped
        };

        var weekly = TrendAnalyzer.BuildWeekly(cells, baseline);

        Assert.Equal(2, weekly.Count);
        Assert.Equal(0, weekly[0].ExcessC, 1);
        Assert.Equal(6, weekly[1].ExcessC, 1); // cold cell excluded, so week 1 is purely the +6 warm cell
    }
}

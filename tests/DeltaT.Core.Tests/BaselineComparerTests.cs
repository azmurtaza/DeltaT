using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using Xunit;

namespace DeltaT.Core.Tests;

public class BaselineComparerTests
{
    private const int Warm = (int)AmbientBand.Warm;

    private static BaselineRow Row(LoadBucket bucket, double delta, double? fan = null, int minutes = 200, int epoch = 0) =>
        new(epoch, ComponentKind.Cpu, "Test CPU", Warm, bucket, delta, delta + 3, null, fan, minutes, 0);

    private static BaselineRow RowSe(LoadBucket bucket, double delta, double se, int minutes = 200, int epoch = 0) =>
        new(epoch, ComponentKind.Cpu, "Test CPU", Warm, bucket, delta, delta + 3, null, null, minutes, 0, se);

    [Fact]
    public void NewPasteCooler_AtHeavyLoad_ReadsImproved()
    {
        var before = new[] { Row(LoadBucket.Heavy, 60) };
        var after = new[] { Row(LoadBucket.Heavy, 52, epoch: 1) };

        BaselineComparison cmp = BaselineComparer.Compare(before, after, ComponentKind.Cpu);

        Assert.Equal(RepasteVerdict.Improved, cmp.Verdict);
        Assert.True(cmp.WeightedDeltaChangeC < -1.5, $"expected cooler, got {cmp.WeightedDeltaChangeC}");
    }

    [Fact]
    public void NewPasteHotter_AtHeavyLoad_ReadsWorse()
    {
        var before = new[] { Row(LoadBucket.Heavy, 60) };
        var after = new[] { Row(LoadBucket.Heavy, 66, epoch: 1) };

        BaselineComparison cmp = BaselineComparer.Compare(before, after, ComponentKind.Cpu);

        Assert.Equal(RepasteVerdict.Worse, cmp.Verdict);
        Assert.True(cmp.WeightedDeltaChangeC >= 1.5);
    }

    [Fact]
    public void WithinNoise_ReadsUnchanged()
    {
        var before = new[] { Row(LoadBucket.Heavy, 60) };
        var after = new[] { Row(LoadBucket.Heavy, 60.5, epoch: 1) };

        Assert.Equal(RepasteVerdict.Unchanged, BaselineComparer.Compare(before, after, ComponentKind.Cpu).Verdict);
    }

    [Fact]
    public void ThinData_ReadsInconclusive()
    {
        // Only idle overlaps, and only a few minutes of it — nothing load-bearing to judge.
        var before = new[] { Row(LoadBucket.Idle, 20, minutes: 10) };
        var after = new[] { Row(LoadBucket.Idle, 12, minutes: 10, epoch: 1) };

        Assert.Equal(RepasteVerdict.Inconclusive, BaselineComparer.Compare(before, after, ComponentKind.Cpu).Verdict);
    }

    [Fact]
    public void OnlyCellsPresentInBothEpochs_AreCompared()
    {
        // 'after' knows a hot heavy cell the old epoch never saw — it must be ignored,
        // leaving the matched heavy cell (on baseline) as the verdict.
        var before = new[] { Row(LoadBucket.Heavy, 60) };
        var after = new[]
        {
            Row(LoadBucket.Heavy, 60, epoch: 1),
            new BaselineRow(1, ComponentKind.Cpu, "Test CPU", (int)AmbientBand.Hot, LoadBucket.Heavy, 90, 93, null, null, 200, 0),
        };

        Assert.Equal(RepasteVerdict.Unchanged, BaselineComparer.Compare(before, after, ComponentKind.Cpu).Verdict);
    }

    [Fact]
    public void NoisyBaselines_RaiseTheBar_SoASmallShiftReadsUnchanged()
    {
        // Under the fixed °C floor a 2.5° move reads as a regression...
        var before = new[] { Row(LoadBucket.Heavy, 60) };
        var after = new[] { Row(LoadBucket.Heavy, 62.5, epoch: 1) };
        Assert.Equal(RepasteVerdict.Worse, BaselineComparer.Compare(before, after, ComponentKind.Cpu).Verdict);

        // ...but when both baselines are that noisy (±2° SE each), 2.5° isn't significant.
        var beforeSe = new[] { RowSe(LoadBucket.Heavy, 60, se: 2.0) };
        var afterSe = new[] { RowSe(LoadBucket.Heavy, 62.5, se: 2.0, epoch: 1) };
        Assert.Equal(RepasteVerdict.Unchanged, BaselineComparer.Compare(beforeSe, afterSe, ComponentKind.Cpu).Verdict);
    }

    [Fact]
    public void TightBaselines_ConfirmARealRegression()
    {
        // Same 2.5° move, but both baselines are tight (±0.2° SE) → clearly real.
        var before = new[] { RowSe(LoadBucket.Heavy, 60, se: 0.2) };
        var after = new[] { RowSe(LoadBucket.Heavy, 62.5, se: 0.2, epoch: 1) };
        Assert.Equal(RepasteVerdict.Worse, BaselineComparer.Compare(before, after, ComponentKind.Cpu).Verdict);
    }

    [Fact]
    public void FanCurveChange_DoesNotFakeAnImprovement()
    {
        // New epoch's delta is 5° lower — but only because the fans now spin far harder.
        // Fair (fan-normalized) comparison must not call this an improvement.
        var before = new[] { Row(LoadBucket.Heavy, 60, fan: 4200) };
        var after = new[] { Row(LoadBucket.Heavy, 55, fan: 6000, epoch: 1) };

        BaselineComparison cmp = BaselineComparer.Compare(before, after, ComponentKind.Cpu);

        Assert.True(cmp.FanCorrected);
        Assert.NotEqual(RepasteVerdict.Improved, cmp.Verdict);
    }
}

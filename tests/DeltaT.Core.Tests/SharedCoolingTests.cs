using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>Locks the floors for the shared-cooling suite: a rise that carries a constant
/// offset and a neighbour's heat, which is what real hardware produces and what the older
/// proportional model had no room for.
///
/// <para>Before the fitted-response engine, scenario A (a healthy machine, boost off with the
/// GPU gaming, judged against a boost-on GPU-quiet baseline) surfaced a fault in <b>400/400</b>
/// trials with a mean score of 55.7 and the PASTE cell reading 0. It now surfaces none. The
/// same reading off the real dev laptop scored 55 with PASTE 0 on a measured rise that was
/// identical to its own baseline.</para></summary>
public class SharedCoolingTests
{
    private static readonly DetectionBenchmark.SharedCoolingResult R =
        DetectionBenchmark.RunSharedCooling(seed: 20260731, trials: 400);

    [Fact]
    public void HealthyMachineUnderARegimeChange_IsNotAccused()
    {
        Assert.True(R.HealthyFaultFindings <= 8,
            $"false fault findings {R.HealthyFaultFindings}/{R.Trials} (was 400/400 before the response model)");
        Assert.True(R.HealthyAspectNotClear <= 8,
            $"fault aspect cells below Clear {R.HealthyAspectNotClear}/{R.Trials}");
        Assert.True(R.HealthyMeanScore >= 92, $"mean score {R.HealthyMeanScore:0.0} (was 55.7)");
        Assert.True(R.HealthyMeanPaste >= 90, $"mean PASTE cell {R.HealthyMeanPaste:0.0} (was 0.0)");
    }

    [Fact]
    public void TheFixIsNotOneDirectional()
    {
        // The mirror regime change, and the same regime on both sides as the noise floor.
        Assert.True(R.MirrorFaultFindings <= 8, $"mirror false faults {R.MirrorFaultFindings}/{R.Trials}");
        Assert.True(R.MirrorMeanScore >= 92, $"mirror mean score {R.MirrorMeanScore:0.0}");
        Assert.True(R.ReferenceFaultFindings <= 4, $"reference floor {R.ReferenceFaultFindings}/{R.Trials}");
        Assert.True(R.ReferenceMeanScore >= 97, $"reference mean score {R.ReferenceMeanScore:0.0}");
    }

    [Fact]
    public void GenuineDegradationIsStillNamed()
    {
        // Anti-blinding, both directions. Clearing the healthy case is only worth anything if
        // real degradation under the same confounder is still caught: (D) with the regime change
        // running the way that inflates a raw reading, (E) the way that masks it.
        Assert.True(R.DegradedPasteNamed >= (int)(0.95 * R.Trials),
            $"degraded paste named {R.DegradedPasteNamed}/{R.Trials}");
        Assert.True(R.MaskedPasteNamed >= (int)(0.85 * R.Trials),
            $"masked degraded paste named {R.MaskedPasteNamed}/{R.Trials} (0/400 without the co-power term)");
    }
}

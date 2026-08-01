using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>Locks the floors for the synthetic-load suite: DeltaT's own stress test must never
/// be mistaken for a workload the machine does.
///
/// <para>Reported on a Predator PH315-55 (RTX 3060 Laptop, 140 W): the in-app GPU test draws
/// ~120 W where FurMark v2 draws 139-140 W, and the dashboard then reported the GPU 11% down on
/// POWER. Ten minutes of FurMark later it read MATCHED with nothing else changed. Measured, a
/// healthy card whose recent full-load evidence came from the burn misreported its power state
/// in <b>357/400</b> trials, and on a week with no gaming at all in <b>399/400</b>. With the
/// synthetic minutes never aggregated it is 2/400 (the power-metering noise floor), and a week
/// with no real load reads "--" rather than inventing a deficit.</para>
///
/// <para>The score itself was never wrong here: false fault findings were 0/400 either way,
/// because power normalization already transports the reference to the reading's operating
/// point. The defect was confined to the POWER readout, which is exactly what was reported.
/// The anti-blinding case guards the other direction.</para></summary>
public class SyntheticLoadTests
{
    private static readonly DetectionBenchmark.SyntheticLoadResult R =
        DetectionBenchmark.RunSyntheticLoad(seed: 20260801, trials: 400);

    [Fact]
    public void ExcludingTheAppsOwnBurn_StopsTheFalsePowerDeficit()
    {
        Assert.True(R.ExcludedPowerMisread <= 20,
            $"POWER misreads {R.ExcludedPowerMisread}/{R.Trials} (was {R.ContaminatedPowerMisread}/{R.Trials} with the burn aggregated)");
        // The suite is only meaningful while it can still see the original failure.
        Assert.True(R.ContaminatedPowerMisread >= 200,
            $"the contaminated arm no longer reproduces the reported failure ({R.ContaminatedPowerMisread}/{R.Trials})");
    }

    [Fact]
    public void AWeekWithNoRealLoad_ReportsAnUnknownPowerState_NotADeficit()
    {
        Assert.True(R.IdleOnlyExcludedUnknown >= 380,
            $"POWER read as known on {R.Trials - R.IdleOnlyExcludedUnknown}/{R.Trials} idle-only weeks");
        Assert.True(R.IdleOnlyContaminatedMisread >= 200,
            $"the idle-only contaminated arm no longer reproduces ({R.IdleOnlyContaminatedMisread}/{R.Trials})");
    }

    [Fact]
    public void NeitherArmInventsAFault()
    {
        Assert.True(R.ContaminatedFaultFindings <= 8, $"contaminated false faults {R.ContaminatedFaultFindings}/{R.Trials}");
        Assert.True(R.ExcludedFaultFindings <= 8, $"excluded false faults {R.ExcludedFaultFindings}/{R.Trials}");
        Assert.True(R.ExcludedMeanScore >= 97, $"excluded mean score {R.ExcludedMeanScore:0.0}");
    }

    [Fact]
    public void RealDegradationIsStillNamed()
    {
        // Dropping the synthetic minutes must not cost detection on a card that really is
        // degrading, measured on a week that included genuine gaming.
        Assert.True(R.DegradedNamed >= 370, $"degraded paste named {R.DegradedNamed}/{R.Trials}");
        Assert.True(R.DegradedMeanScore <= 80, $"degraded mean score {R.DegradedMeanScore:0.0}");
    }
}

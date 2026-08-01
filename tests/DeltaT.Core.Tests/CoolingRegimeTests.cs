using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>Locks the floors for the cooling-setup suite.
///
/// <para>Reported by a user with an adjustable sealed laptop cooler (Ilano V12): turning its
/// fans down raised every temperature and DeltaT read it as the CPU's and GPU's liquid metal
/// and paste degrading. Measured, a healthy machine with the pad eased off false-alarms on
/// <b>306/400</b> weeks at mean score 73.9 when judged against the full-airflow baseline, and
/// <b>1/400</b> at 93.7 against a baseline learned in the same setup, which is the suite's own
/// noise floor for fault findings.</para></summary>
public class CoolingRegimeTests
{
    private static readonly DetectionBenchmark.CoolingRegimeResult R =
        DetectionBenchmark.RunCoolingRegime(seed: 20260801, trials: 400);

    [Fact]
    public void ASetupOfItsOwn_StopsTheFalseDegradationVerdict()
    {
        Assert.True(R.SeparatedFalseFaults <= R.ReferenceFalseFaults + 8,
            $"separated false faults {R.SeparatedFalseFaults}/{R.Trials} against a floor of {R.ReferenceFalseFaults}/{R.Trials}");
        // The suite is only meaningful while it can still see the reported failure.
        Assert.True(R.CrossSetupFalseFaults >= 150,
            $"the setup-blind arm no longer reproduces the report ({R.CrossSetupFalseFaults}/{R.Trials})");
        Assert.True(R.SeparatedMeanScore - R.CrossSetupMeanScore >= 10,
            $"separation recovered only {R.SeparatedMeanScore - R.CrossSetupMeanScore:0.0} points");
    }

    [Fact]
    public void TheSeparationIsNotOneDirectional()
    {
        // A machine that got COOLER (full airflow measured against a reduced-airflow baseline)
        // must not read as a fault either.
        Assert.True(R.MirrorFalseFaults <= R.ReferenceFalseFaults + 8,
            $"mirror false faults {R.MirrorFalseFaults}/{R.Trials}");
        Assert.True(R.MirrorMeanScore >= 92, $"mirror mean score {R.MirrorMeanScore:0.0}");
        Assert.True(R.ReferenceMeanScore >= 97, $"reference floor {R.ReferenceMeanScore:0.0}");
    }

    [Fact]
    public void RealDegradationInThatSetupIsStillNamed()
    {
        // Giving the cooler setup its own reference must not blind the engine to a fault that
        // happens while the user is sitting in it.
        Assert.True(R.DegradedNamed >= 370, $"degraded paste named {R.DegradedNamed}/{R.Trials}");
        Assert.True(R.DegradedMeanScore <= 75, $"degraded mean score {R.DegradedMeanScore:0.0}");
    }
}

/// <summary>The encoding that keeps cooling setups from blending with each other, or with the
/// ambient-source modes that already shared this dimension.</summary>
public class RegimeEncodingTests
{
    [Fact]
    public void TheDefaultSetupEncodesToExactlyTheModesThatShipped()
    {
        // The whole safety argument for reusing the mode column rests on this: an install that
        // never declares a cooling setup must tag and query rows byte-identically to before.
        Assert.Equal(0, new Regime(0, CoolingProfile.DefaultId).Id);
        Assert.Equal(1, new Regime(1, CoolingProfile.DefaultId).Id);
        Assert.Equal("", new Regime(0, CoolingProfile.DefaultId).KeySuffix);
        Assert.Equal(".fixed", new Regime(1, CoolingProfile.DefaultId).KeySuffix);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(1, 97)]
    public void EveryRegimeRoundTripsThroughItsId(int ambient, int profile)
    {
        var regime = new Regime(ambient, profile);
        Assert.Equal(regime, Regime.FromId(regime.Id));
    }

    [Fact]
    public void NoTwoRegimesShareAnIdOrAKey()
    {
        var regimes = Enumerable.Range(0, 12)
            .SelectMany(p => new[] { new Regime(0, p), new Regime(1, p) })
            .ToList();
        Assert.Equal(regimes.Count, regimes.Select(r => r.Id).Distinct().Count());
        Assert.Equal(regimes.Count, regimes.Select(r => r.KeySuffix).Distinct().Count());
    }
}

using System.Text.Json;
using DeltaT.Core.Diagnostics;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>The fingerprint's recorded temperature depends on how long the load ran, so
/// runs may only be compared within one timing protocol. That guard rests entirely on old
/// stored fingerprints identifying themselves as protocol 1, which these lock down.</summary>
public class FingerprintProtocolTests
{
    // Exactly the shape written to the events table before protocols existed (note the
    // legacy Cpu* property names, kept via JsonPropertyName so pre-GPU history still loads).
    private const string LegacyJson = """
        {"AtUtc":"2026-05-01T10:00:00+00:00","AmbientC":24.0,"CpuStartC":45.0,"CpuPeakC":92.0,
         "CpuSustainedC":88.0,"CpuSustainedDeltaC":64.0,"SoakRatePerMin":12.5,"ThrottleSamples":3,
         "GpuPeakC":null,"GpuSustainedDeltaC":null,"GpuWasLoaded":false,"OnAcPower":true,"Target":"Cpu"}
        """;

    [Fact]
    public void AFingerprintStoredBeforeProtocolsExisted_ReadsAsProtocol1()
    {
        FingerprintResult? legacy = JsonSerializer.Deserialize<FingerprintResult>(LegacyJson);

        Assert.NotNull(legacy);
        Assert.Equal(1, legacy!.Protocol);
        Assert.Equal(88.0, legacy.SustainedC);   // the legacy Cpu* names still bind
        Assert.Equal("Cpu", legacy.Target);
    }

    [Fact]
    public void ANewFingerprint_IsNotComparableWithALegacyOne()
    {
        FingerprintResult legacy = JsonSerializer.Deserialize<FingerprintResult>(LegacyJson)!;
        FingerprintResult fresh = legacy with { Protocol = FingerprintTest.CurrentProtocol };

        // The comparison gate is "same protocol". A shortened load makes the new run read
        // cooler for reasons that have nothing to do with the paste, so these two must not
        // be matched against each other.
        Assert.NotEqual(legacy.Protocol, fresh.Protocol);
        Assert.True(FingerprintTest.CurrentProtocol > 1);
    }

    [Fact]
    public void TheLoadFloorAlwaysOutlastsTheMeasurementWindow()
    {
        // Sustained temperature is the mean of the last 45 s of load. The floor must leave
        // room for that window to sit past the opening transient, or the "plateau" would be
        // measured on the climb.
        Assert.True(FingerprintTest.LoadFloor >= FingerprintTest.PlateauWindow * 2);
        Assert.True(FingerprintTest.LoadCeiling > FingerprintTest.LoadFloor);
        Assert.True(FingerprintTest.Settle >= TimeSpan.FromSeconds(10));
    }
}

/// <summary>The load now runs until the component stops climbing, so the stopping rule is
/// the test. These drive it with synthetic curves shaped like the ones measured on real
/// hardware.</summary>
public class FingerprintPlateauTests
{
    private static List<(DateTimeOffset Ts, double Temp)> Curve(Func<double, double> tempAt, int seconds)
    {
        var start = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, seconds)
            .Select(i => (start.AddSeconds(i), tempAt(i)))
            .ToList();
    }

    [Fact]
    public void AStillClimbingGpu_IsNotAPlateau()
    {
        // Measured shape: the RTX 3050 was still rising ~2 C/min at 90 s.
        var climbing = Curve(t => 50 + 2.0 * (t / 60.0), 90);

        Assert.False(FingerprintTest.HasPlateaued(climbing));
        Assert.Equal(2.0, FingerprintTest.SlopePerMinute(climbing, FingerprintTest.PlateauWindow)!.Value, 1);
    }

    [Fact]
    public void ASettledCpuStillCreepingWithTheChassis_IsAPlateau()
    {
        // Measured shape: a settled laptop CPU keeps creeping ~+0.5 C/min as the chassis
        // soaks. That drift never ends, so treating it as "not settled" would run every
        // test to the ceiling.
        var creeping = Curve(t => 66 + 0.5 * (t / 60.0), 90);

        Assert.True(FingerprintTest.HasPlateaued(creeping));
    }

    [Fact]
    public void ATurboSpike_DoesNotFakeAClimb()
    {
        // This CPU throws +10 C single-sample spikes. An endpoint-to-endpoint slope would
        // read one as a violent climb; the least-squares fit shrugs it off.
        var flatWithSpike = Curve(t => t is 80 ? 78 : 66, 90);

        Assert.True(FingerprintTest.HasPlateaued(flatWithSpike));
    }

    [Fact]
    public void ACoolingCurve_CountsAsSettled_NotAsNegativeProgress()
    {
        // A falling temperature (turbo decaying to the sustained power limit) is |slope|,
        // so it must not read as "settled" while it is still moving fast.
        var decaying = Curve(t => 80 - 3.0 * (t / 60.0), 90);
        Assert.False(FingerprintTest.HasPlateaued(decaying));
    }

    [Fact]
    public void TooFewSamples_NeverClaimsAPlateau()
    {
        Assert.Null(FingerprintTest.SlopePerMinute(new List<(DateTimeOffset, double)>(), FingerprintTest.PlateauWindow));
        Assert.False(FingerprintTest.HasPlateaued(Curve(_ => 60, 3)));
    }

    [Fact]
    public void WholeDegreeQuantization_DoesNotHideARealClimb()
    {
        // GPU temperatures arrive as whole degrees. A median-based estimator can only ever
        // land on an integer, so a genuine +2 C/min climb reads as a flat 0.00 and the test
        // stops 1.4 C short of the plateau (this is exactly what replaying the captured
        // RTX 3050 curve exposed). Averaging has to see through the quantization.
        var quantizedClimb = Curve(t => Math.Floor(50 + 2.0 * (t / 60.0)), 120);

        double slope = FingerprintTest.SlopePerMinute(quantizedClimb, FingerprintTest.PlateauWindow)!.Value;
        Assert.True(slope > FingerprintTest.PlateauSlopeCPerMin,
            $"a quantized +2 C/min climb must not read as settled (got {slope:0.00} C/min)");
        Assert.False(FingerprintTest.HasPlateaued(quantizedClimb));
    }
}

/// <summary>The day-one absolute "thermally constrained" verdict. The whole point of the
/// AND-gate is that heat has to be the confirmed limiter, so a deliberately power-limited
/// machine (boost off, low power plan) is never called out as a fault. These lock every gate.</summary>
public class FingerprintThermalConstraintTests
{
    // TjMax 100 °C, PL2 60 W. A genuinely thermally-constrained run: peaked at TjMax, the
    // thermal limiter fired, and it held ~45 W, well under the 60 W budget.
    [Fact]
    public void AllGatesMet_IsThermallyConstrained()
    {
        Assert.True(FingerprintTest.IsThermallyConstrained(
            peakC: 99, tjMaxC: 100, thermalLimiterSeen: true, sustainedLoadW: 45, pl2W: 60));
    }

    [Fact]
    public void DeliberatelyPowerLimited_IsNotConstrained()
    {
        // The dev-laptop config: boost off, drawing ~16 W of a 60 W budget, die cool at ~60 °C,
        // so it never reached TjMax and the THERMAL limiter never fired. Must not trip.
        Assert.False(FingerprintTest.IsThermallyConstrained(
            peakC: 62, tjMaxC: 100, thermalLimiterSeen: false, sustainedLoadW: 16, pl2W: 60));
    }

    [Fact]
    public void ReachingTheBudgetWithoutHeat_IsNotConstrained()
    {
        // Drawing the full PL2: it is using its whole budget, so a power deficit doesn't exist
        // even if it happened to run warm. Not "held back by cooling".
        Assert.False(FingerprintTest.IsThermallyConstrained(
            peakC: 99, tjMaxC: 100, thermalLimiterSeen: true, sustainedLoadW: 58, pl2W: 60));
    }

    [Fact]
    public void NoThermalLimiter_EvenWithADeficit_IsNotConstrained()
    {
        // Below PL2 AND near TjMax, but the CPU's own register says the thermal limiter is not
        // the one asserting (a current/VRM or power limit is). Heat is not the confirmed cause.
        Assert.False(FingerprintTest.IsThermallyConstrained(
            peakC: 99, tjMaxC: 100, thermalLimiterSeen: false, sustainedLoadW: 40, pl2W: 60));
    }

    [Fact]
    public void MissingRegisters_NeverConstrained()
    {
        // AMD / no driver: no PL2, no TjMax, no watts. Silent, never a fake verdict.
        Assert.False(FingerprintTest.IsThermallyConstrained(99, null, true, 40, null));
        Assert.False(FingerprintTest.IsThermallyConstrained(99, 100, true, null, 60));
    }
}

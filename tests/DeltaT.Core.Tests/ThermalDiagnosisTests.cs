using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>The diagnosis is the heart of the "not a paste gimmick" direction: it must
/// name the RIGHT cause and, above all, must not blame the paste for a fan, dust, or a
/// deliberate power change.</summary>
public class ThermalDiagnosisTests
{
    private static DiagnosisInputs Base(
        double? excess = null, double? heavy = null, double? idle = null, bool broad = false,
        double? soak = null, double? cool = null, bool fanUnder = false, double? fanRatio = null,
        double power = 0, double? gap = null, double? gapBase = null,
        int throttle = 0, bool nearLimit = false, bool beyondNorm = false) =>
        new(ComponentKind.GpuDiscrete, excess, heavy, idle, broad, soak, cool, fanUnder, fanRatio,
            power, gap, gapBase, throttle, 24 * 7, nearLimit, beyondNorm);

    private static ThermalCause Primary(DiagnosisInputs e) => ThermalDiagnostician.Diagnose(e).Primary.Cause;

    [Fact]
    public void HealthyMachine_DiagnosesHealthy()
    {
        Assert.True(ThermalDiagnostician.Diagnose(Base(excess: 0.2, heavy: 0.3, idle: 0.1)).IsHealthy);
    }

    [Fact]
    public void LoadDependentExcess_WithFastSoak_IsPaste()
    {
        // Hot under load, flat at idle, heat-soaks fast and sheds slow: textbook paste.
        Assert.Equal(ThermalCause.Paste, Primary(Base(
            excess: 6, heavy: 8, idle: 0.5, soak: 1.4, cool: 0.7)));
    }

    [Fact]
    public void BroadOffset_FansHarder_NormalSoak_IsAirflowNotPaste()
    {
        // Every bucket up (idle included), fans working harder, soak normal: dust/airflow.
        // The whole point of the reframe is that this is NOT called paste.
        ThermalCause cause = Primary(Base(
            excess: 5, heavy: 5.5, idle: 3, broad: true, soak: 1.0, cool: 1.0, fanRatio: 1.2));
        Assert.Equal(ThermalCause.Airflow, cause);
    }

    [Fact]
    public void FanBelowBaseline_IsFanFault()
    {
        Assert.Equal(ThermalCause.FanFault, Primary(Base(
            excess: 4, heavy: 4, idle: 1, fanUnder: true)));
    }

    [Fact]
    public void WideningHotspotGap_IsMount()
    {
        Assert.Equal(ThermalCause.Mount, Primary(Base(
            excess: 1, heavy: 1.5, idle: 0.5, gap: 24, gapBase: 10)));
    }

    [Fact]
    public void BigPowerCorrection_IsPowerConfig_NotAFault()
    {
        // An overclock: most of the temperature change is watts, not cooling.
        ThermalCause cause = Primary(Base(excess: 0.5, heavy: 1, idle: 0.3, power: -8));
        Assert.Equal(ThermalCause.PowerConfig, cause);
    }

    [Fact]
    public void Throttling_ShowsCoolingHeadroom()
    {
        ThermalDiagnosis dx = ThermalDiagnostician.Diagnose(Base(excess: 1, heavy: 1, throttle: 4, nearLimit: true));
        Assert.Contains(dx.Findings, f => f.Cause == ThermalCause.CoolingHeadroom);
    }

    [Fact]
    public void Confidences_AreRanked_AndBounded()
    {
        ThermalDiagnosis dx = ThermalDiagnostician.Diagnose(Base(
            excess: 6, heavy: 8, idle: 0.5, soak: 1.4, cool: 0.7));
        for (int i = 1; i < dx.Findings.Count; i++)
            Assert.True(dx.Findings[i - 1].Confidence >= dx.Findings[i].Confidence);
        Assert.All(dx.Findings, f => Assert.InRange(f.Confidence, 0, 1));
    }
}

/// <summary>The per-aspect readout is the dashboard's health matrix: every subsystem
/// gets its own number from the same evidence as the ranked diagnosis, and a sensor
/// that doesn't exist yields "--", never a fake 100 or 0.</summary>
public class AspectHealthTests
{
    private static DiagnosisInputs Base(
        double? excess = null, double? heavy = null, double? idle = null, bool broad = false,
        double? soak = null, double? cool = null, bool fanUnder = false, double? fanRatio = null,
        double power = 0, double? gap = null, double? gapBase = null,
        int throttle = 0, bool nearLimit = false, bool beyondNorm = false, double? powerRatio = null) =>
        new(ComponentKind.GpuDiscrete, excess, heavy, idle, broad, soak, cool, fanUnder, fanRatio,
            power, gap, gapBase, throttle, 24 * 7, nearLimit, beyondNorm, powerRatio);

    private static AspectHealth Get(DiagnosisInputs e, HealthAspect a) =>
        ThermalDiagnostician.AssessAspects(e).Single(x => x.Aspect == a);

    [Fact]
    public void HealthyMachine_ScoresEveryMeasurableAspectHigh()
    {
        var e = Base(excess: 0.2, heavy: 0.3, idle: 0.1, soak: 1.0, cool: 1.0,
                     fanRatio: 1.0, gap: 9, gapBase: 9, powerRatio: 1.0);
        IReadOnlyList<AspectHealth> aspects = ThermalDiagnostician.AssessAspects(e);
        Assert.Equal(6, aspects.Count);
        foreach (AspectHealth a in aspects.Where(a => a.Aspect != HealthAspect.Power))
        {
            Assert.True(a.Known);
            Assert.True(a.Score >= 85, $"{a.Aspect} should read healthy, got {a.Score}");
        }
        Assert.Equal("MATCHED", Get(e, HealthAspect.Power).Status);
    }

    [Fact]
    public void PasteProblem_LowersPaste_NotAirflow()
    {
        var e = Base(excess: 6, heavy: 8, idle: 0.5, soak: 1.4, cool: 0.7, fanRatio: 1.0);
        Assert.True(Get(e, HealthAspect.Paste).Score < 40);
        Assert.True(Get(e, HealthAspect.Airflow).Score >= 85);
    }

    [Fact]
    public void DustProblem_LowersAirflow_SparesPaste()
    {
        var e = Base(excess: 5, heavy: 5.5, idle: 3, broad: true, soak: 1.0, cool: 1.0, fanRatio: 1.2);
        Assert.True(Get(e, HealthAspect.Airflow).Score < 40);
        Assert.True(Get(e, HealthAspect.Paste).Score > Get(e, HealthAspect.Airflow).Score);
    }

    [Fact]
    public void MissingSensors_ReadAsUnknown_NeverAsANumber()
    {
        // No fan sensor, no hotspot, no power: those aspects say "--".
        var e = Base(excess: 1, heavy: 1);
        Assert.False(Get(e, HealthAspect.Fans).Known);
        Assert.Null(Get(e, HealthAspect.Fans).Score);
        Assert.False(Get(e, HealthAspect.Mount).Known);
        Assert.False(Get(e, HealthAspect.Power).Known);
        // But headroom is always judgeable (throttle events count from day one).
        Assert.True(Get(e, HealthAspect.Headroom).Known);
    }

    [Fact]
    public void NoComparableLoad_PasteAndAirflowUnknown()
    {
        var e = Base();
        Assert.False(Get(e, HealthAspect.Paste).Known);
        Assert.False(Get(e, HealthAspect.Airflow).Known);
    }

    [Fact]
    public void Throttling_DrainsHeadroom()
    {
        Assert.True(Get(Base(throttle: 6), HealthAspect.Headroom).Score < 60);
        Assert.Equal(100, Get(Base(), HealthAspect.Headroom).Score);
    }

    [Fact]
    public void PowerState_StatesTheMeasuredDifference_NotAnAccusation()
    {
        // A boost/turbo mode change is the common reason watts move, so the cell reports
        // the measured difference rather than calling the machine overclocked.
        Assert.Equal("+25%", Get(Base(powerRatio: 1.25), HealthAspect.Power).Status);
        Assert.Equal("-20%", Get(Base(powerRatio: 0.8), HealthAspect.Power).Status);
        Assert.Null(Get(Base(powerRatio: 1.25), HealthAspect.Power).Score);
        Assert.DoesNotContain("overclock", Get(Base(powerRatio: 0.8), HealthAspect.Power).Detail);
    }

    [Fact]
    public void WeakPasteSignal_BelowSurfaceFloor_ReadsClear_NotWatch()
    {
        // The field report: a power-state change (POWER −21%) left a small residual excess
        // that was too weak to name as a cause (no chip) and below the overall score's
        // deadband (verdict stayed Excellent), yet the paste cell showed 81 "Watch". A
        // signal the diagnosis won't surface must not visibly ding the matrix either.
        var e = Base(excess: 0.5, heavy: 3.7, idle: 0.4);  // load-dependent but faint
        ThermalDiagnosis dx = ThermalDiagnostician.Diagnose(e);
        Assert.DoesNotContain(dx.Findings, f => f.Cause == ThermalCause.Paste); // not named
        AspectHealth paste = Get(e, HealthAspect.Paste);
        Assert.True(paste.Score >= 85, $"weak sub-floor paste should read Clear, got {paste.Score}");
        Assert.Equal("Clear", paste.Status);
    }

    [Fact]
    public void Matrix_EntersProblemZone_ExactlyWhenACauseIsSurfaced()
    {
        // The consistency guarantee: an aspect meter drops below Clear (85) if and only if
        // its confidence is high enough for the ranked diagnosis to name that cause. Sweep
        // paste severity across the surface floor and check the two agree at every step.
        for (double heavy = 2.0; heavy <= 12.0; heavy += 0.5)
        {
            var e = Base(excess: heavy * 0.5, heavy: heavy, idle: 0.3, soak: 1.3, cool: 0.8);
            bool named = ThermalDiagnostician.Diagnose(e).Findings.Any(f => f.Cause == ThermalCause.Paste);
            bool dinged = Get(e, HealthAspect.Paste).Score < 85;
            Assert.True(named == dinged, $"heavy={heavy}: named={named} but meter dinged={dinged}");
        }
    }

    [Fact]
    public void AspectScores_AgreeWithRankedDiagnosis()
    {
        // Whatever the diagnosis names as primary must also be the worst aspect meter.
        var e = Base(excess: 5, heavy: 5.5, idle: 3, broad: true, soak: 1.0, cool: 1.0, fanRatio: 1.2);
        ThermalDiagnosis dx = ThermalDiagnostician.Diagnose(e);
        Assert.Equal(ThermalCause.Airflow, dx.Primary.Cause);
        AspectHealth worst = ThermalDiagnostician.AssessAspects(e)
            .Where(a => a.Score is not null).MinBy(a => a.Score)!;
        Assert.Equal(HealthAspect.Airflow, worst.Aspect);
    }
}

/// <summary>The detection benchmark is a guardrail: the whole app's value proposition is
/// that it is accurate, so we lock in floors on that accuracy. If a change regresses the
/// engine's ability to tell causes apart, these fail.</summary>
public class DetectionBenchmarkTests
{
    private static readonly BenchmarkReport Report = DetectionBenchmark.Run(seed: 12345, trialsPerCondition: 200);

    [Fact]
    public void FaultsAreDetected_ConfoundersAreNot()
    {
        Assert.True(Report.FaultDetectionRate >= 0.90, $"fault detection {Report.FaultDetectionRate:P1}");
        Assert.True(Report.ConfounderClearRate >= 0.90, $"confounder clear {Report.ConfounderClearRate:P1}");
        Assert.True(Report.OverallAccuracy >= 0.90, $"overall {Report.OverallAccuracy:P1}");
    }

    [Fact]
    public void DustIsNotBlamedOnPaste()
    {
        ConditionResult dust = Report.Conditions.Single(c => c.Condition == Condition.DustAirflow);
        Assert.True(dust.Accuracy >= 0.85, $"dust attributed correctly {dust.Accuracy:P0}");
    }

    [Fact]
    public void OverclockAndUndervolt_DoNotFalseAlarm()
    {
        foreach (Condition c in new[] { Condition.Overclock, Condition.Undervolt, Condition.ColdSeason })
        {
            ConditionResult r = Report.Conditions.Single(x => x.Condition == c);
            Assert.True(r.Accuracy >= 0.90, $"{c} cleared {r.Accuracy:P0}");
        }
    }

    [Fact]
    public void PasteDegradation_IsFlaggedByAFewDegrees()
    {
        SensitivityCurve paste = Report.Sensitivity.Single(s => s.Fault == "Degraded paste");
        Assert.NotNull(paste.FlagsActionAtC);
        Assert.True(paste.FlagsActionAtC <= 8, $"paste action threshold {paste.FlagsActionAtC}°");
        Assert.NotNull(paste.NamesCauseAtC);
    }
}

/// <summary>Per-cell power-state tagging is a guardrail too: a load bucket learned across two
/// power regimes (CPU boost on and off) blends into one cell whose mean misrepresents both, and
/// the measured effect is a small systematic false-"aging" bias on a healthy machine. Keeping the
/// regimes in separate power-tagged sub-cells (so scoring's nearest-power match compares a reading
/// against its own regime) must remove that bias without inventing faults. These lock that in.</summary>
public class PowerContaminationTests
{
    private static readonly DetectionBenchmark.ContaminationResult C = DetectionBenchmark.RunPowerContamination(seed: 999, trials: 400);

    [Fact]
    public void PowerTagging_RemovesTheBlendBias_WithoutAddingFalseFaults()
    {
        // The blended cell carries a real negative (toward-fault) bias; tagging must cut it hard.
        Assert.True(Math.Abs(C.TaggedSignedErr) <= Math.Abs(C.BlendedSignedErr) * 0.5,
            $"tagged bias {C.TaggedSignedErr:+0.0;-0.0} should be well under blended {C.BlendedSignedErr:+0.0;-0.0}");
        // Tagged must be at least as faithful as blended overall...
        Assert.True(C.TaggedMeanAbsErr <= C.BlendedMeanAbsErr,
            $"tagged mean err {C.TaggedMeanAbsErr:0.00} vs blended {C.BlendedMeanAbsErr:0.00}");
        // ...and it must not invent faults a same-regime (reference) baseline wouldn't also see.
        Assert.True(C.TaggedFalseFaults <= C.ReferenceFalseFaults + 2,
            $"tagged false faults {C.TaggedFalseFaults} vs reference floor {C.ReferenceFalseFaults}");
    }
}

/// <summary>Phase 0 go/no-go for the guided-calibration (workout) idea, measured not
/// intuited: does a baseline acquired from controlled synthetic loads yield the SAME
/// fault attribution as one acquired organically? The finding these lock in is that the
/// answer hinges entirely on how closely the workout matches each bucket's real operating
/// point. A naive burner that just pins the bucket ceiling is catastrophic; a workout that
/// targets the representative operating point stays close to organic. If either of those
/// facts ever stops being true, the go/no-go has lost its meaning and these fail.</summary>
public class AcquisitionFidelityTests
{
    // Targets each bucket's representative operating point (small power offset from real use).
    private static readonly FidelityReport WellTargeted = DetectionBenchmark.RunAcquisitionFidelity(
        seed: 4242, trialsPerCondition: 200, synBias: DetectionBenchmark.WellTargetedWorkoutBias);

    // Just pins the bucket ceiling, learning every cell at materially higher watts than the
    // user's real loads ever reproduce.
    private static readonly FidelityReport NaiveBurner = DetectionBenchmark.RunAcquisitionFidelity(
        seed: 4242, trialsPerCondition: 200, synBias: DetectionBenchmark.NaiveBurnerBias);

    [Fact]
    public void Benchmark_CatchesTheNaiveBurnerAsUnfaithful()
    {
        // The whole point of Phase 0: if built carelessly the feature destroys accuracy, and
        // the benchmark must SEE that. A naive burner should be flagged unfaithful and should
        // add many multiples of organic's fault-class flips. If this ever passes clean, the
        // fidelity model has gone blind and every "GO" it prints is worthless.
        Assert.False(NaiveBurner.SyntheticNoWorseThanOrganic(),
            "naive burner must be rejected by the go/no-go");
        Assert.True(NaiveBurner.SyntheticFlips > 5 * Math.Max(1, NaiveBurner.OrganicFlips),
            $"naive burner fault-flips {NaiveBurner.SyntheticFlips} should dwarf organic {NaiveBurner.OrganicFlips}");
    }

    [Fact]
    public void WellTargetedWorkout_PreservesFaultAttribution()
    {
        // The achievable target: IF the real workout targets each bucket's operating point,
        // the fault-class attribution it produces stays within the app's own noise of organic.
        // Score-point drift is allowed to be a touch worse (a synthetic baseline is tighter and
        // sits in the steep part of the penalty curve on the paste/dust conditions), but the
        // fault-flip RATE must stay tiny in absolute terms.
        int totalTrials = WellTargeted.Conditions.Sum(c => c.Trials);
        Assert.True(WellTargeted.SyntheticFlips <= 0.015 * totalTrials,
            $"well-targeted fault-flip rate {(double)WellTargeted.SyntheticFlips / totalTrials:P2} (flips {WellTargeted.SyntheticFlips}/{totalTrials})");
        Assert.True(WellTargeted.SyntheticMeanAbsErr <= WellTargeted.OrganicMeanAbsErr + 1.5,
            $"well-targeted score drift {WellTargeted.SyntheticMeanAbsErr:0.00} vs organic {WellTargeted.OrganicMeanAbsErr:0.00} pts");
    }

    [Fact]
    public void WellTargetedWorkout_IsPerfectOnNonPasteFaults()
    {
        // The residual risk is entirely in the subtlest call (paste vs dust). Fan, mount, and
        // every confounder must be untouched by synthetic acquisition, or the feature is unsafe
        // even for the buckets it should trivially handle.
        foreach (Condition c in new[] { Condition.FanFault, Condition.MountPumpout,
                                        Condition.Overclock, Condition.Undervolt, Condition.ColdSeason })
        {
            FidelityResult f = WellTargeted.Conditions.Single(x => x.Condition == c);
            Assert.True(f.SyntheticFlips <= f.OrganicFlips,
                $"{c}: synthetic flips {f.SyntheticFlips} vs organic {f.OrganicFlips}");
        }
    }
}

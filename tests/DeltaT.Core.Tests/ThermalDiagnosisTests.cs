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

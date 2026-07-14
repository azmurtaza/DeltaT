using DeltaT.Core.Diagnostics;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Remarks;
using DeltaT.Core.Scoring;
using Xunit;

namespace DeltaT.Core.Tests;

public class RemarksEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    private static ComponentReading Reading(ComponentKind kind, double? temp = null, double? hotspot = null,
        double? load = null, double? fan = null, double? wear = null, double? cycles = null) =>
        new(kind, kind.ToString(), temp, hotspot, load, fan, null, wear, false, null, cycles);

    private static RemarkContext Ctx(params ComponentReading[] readings) => new(
        NowUtc: Now,
        FirstEver: false,
        Latest: readings.Length > 0 ? new SensorSnapshot(Now, true, readings) : null,
        ShortTrend: null,
        AmbientC: null,
        AmbientStale: false,
        City: null,
        Scores: null,
        ScoreDropThisWeek: null,
        ThrottleEventsLastHour: 0,
        AllTimeMax: null,
        OnAcPower: true,
        BaselineReady: true,
        BaselineJustBecameReady: false,
        CalibrationProgress: 1,
        LearningDay: 10);

    private static IReadOnlyList<Remark> Fire(RemarkContext ctx, string rule) =>
        new RemarksEngine().Evaluate(ctx).Where(r => r.RuleId == rule).ToList();

    [Fact]
    public void HotspotGapEscalatesWithWidth()
    {
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.GpuDiscrete, temp: 70, hotspot: 80, load: 80)), "gpu-hotspot-gap"));
        Assert.Equal(RemarkSeverity.Notice,
            Fire(Ctx(Reading(ComponentKind.GpuDiscrete, temp: 70, hotspot: 92, load: 80)), "gpu-hotspot-gap").Single().Severity);
        Assert.Equal(RemarkSeverity.Warning,
            Fire(Ctx(Reading(ComponentKind.GpuDiscrete, temp: 70, hotspot: 100, load: 80)), "gpu-hotspot-gap").Single().Severity);
    }

    [Fact]
    public void HotspotGapIgnoredAtIdle()
    {
        // Idle gaps are noisy and meaningless - only a loaded gap indicts the paste.
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.GpuDiscrete, temp: 40, hotspot: 70, load: 5)), "gpu-hotspot-gap"));
    }

    [Fact]
    public void SilentFanUnderHeavyLoadIsCalledOut()
    {
        Assert.Single(Fire(Ctx(Reading(ComponentKind.Cpu, temp: 86, load: 95, fan: 800)), "fan-silent-load"));
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.Cpu, temp: 86, load: 95, fan: 3200)), "fan-silent-load"));
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.Cpu, temp: 86, load: 95, fan: null)), "fan-silent-load"));
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.Cpu, temp: 60, load: 95, fan: 800)), "fan-silent-load"));
    }

    [Fact]
    public void BatteryWearFiresPastThreshold()
    {
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.Battery, wear: 7)), "battery-wear"));
        Remark r = Fire(Ctx(Reading(ComponentKind.Battery, wear: 18, cycles: 300)), "battery-wear").Single();
        Assert.Contains("charge cycles", r.Text);
    }

    [Fact]
    public void SsdWearFiresPastHalfEndurance()
    {
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.Storage, temp: 45, wear: 5)), "ssd-wear"));
        Assert.Equal(RemarkSeverity.Info,
            Fire(Ctx(Reading(ComponentKind.Storage, temp: 45, wear: 55)), "ssd-wear").Single().Severity);
        Assert.Equal(RemarkSeverity.Notice,
            Fire(Ctx(Reading(ComponentKind.Storage, temp: 45, wear: 85)), "ssd-wear").Single().Severity);
    }

    [Fact]
    public void NightOwlNeedsSmallHoursAndLoad()
    {
        var busy = Reading(ComponentKind.Cpu, temp: 80, load: 95);
        Assert.Single(Fire(Ctx(busy) with { LocalHour = 2 }, "night-owl"));
        Assert.Empty(Fire(Ctx(busy) with { LocalHour = 14 }, "night-owl"));
        Assert.Empty(Fire(Ctx(Reading(ComponentKind.Cpu, temp: 40, load: 3)) with { LocalHour = 2 }, "night-owl"));
        Assert.Empty(Fire(Ctx(busy), "night-owl")); // LocalHour unknown (-1)
    }

    [Fact]
    public void ThrottleFreeMonthNeedsCleanCountAndTenure()
    {
        Assert.Single(Fire(Ctx() with { ThrottleEventsLast30Days = 0, DaysTogether = 45 }, "throttle-free-month"));
        Assert.Empty(Fire(Ctx() with { ThrottleEventsLast30Days = 2, DaysTogether = 45 }, "throttle-free-month"));
        Assert.Empty(Fire(Ctx() with { ThrottleEventsLast30Days = 0, DaysTogether = 10 }, "throttle-free-month"));
        Assert.Empty(Fire(Ctx() with { DaysTogether = 45 }, "throttle-free-month")); // count unknown
    }

    [Fact]
    public void DaysTogetherFiresOnMilestoneWindowsOnly()
    {
        Assert.Single(Fire(Ctx() with { DaysTogether = 31 }, "days-together"));
        Assert.Empty(Fire(Ctx() with { DaysTogether = 60 }, "days-together"));
        Assert.Single(Fire(Ctx() with { DaysTogether = 100 }, "days-together"));
        Assert.Equal(RemarkSeverity.Notice, Fire(Ctx() with { DaysTogether = 366 }, "days-together").Single().Severity);
    }

    [Fact]
    public void TempFallingCreditsAnImprovement()
    {
        var trend = new Dictionary<ComponentKind, (double, double, bool)>
        {
            [ComponentKind.Cpu] = (60, 66.5, true),
        };
        Assert.Single(Fire(Ctx() with { ShortTrend = trend }, "temp-falling"));

        var dissimilar = new Dictionary<ComponentKind, (double, double, bool)>
        {
            [ComponentKind.Cpu] = (60, 66.5, false),
        };
        Assert.Empty(Fire(Ctx() with { ShortTrend = dissimilar }, "temp-falling"));
    }

    [Fact]
    public void CityMoveIsAnnouncedOnce()
    {
        var ctx = Ctx() with { CityChanged = true, City = "Lahore" };
        var engine = new RemarksEngine();
        Assert.Single(engine.Evaluate(ctx), r => r.RuleId == "city-moved");
        // Cooldown holds even if the flag were somehow still set a minute later.
        Assert.DoesNotContain(engine.Evaluate(ctx with { NowUtc = Now.AddMinutes(1) }), r => r.RuleId == "city-moved");
    }

    private static FingerprintEcho Echo(ComponentKind kind, double? deltaVsPrev, int throttles = 0) =>
        new(kind, new FingerprintResult(
            Now, 25, 45, 92, 89, 64, 12, throttles, null, null, false, true,
            kind == ComponentKind.GpuDiscrete ? "Gpu" : "Cpu"), deltaVsPrev);

    [Fact]
    public void FingerprintEcho_FirstRunIsAnnounced()
    {
        Remark r = Fire(Ctx() with { Fingerprint = Echo(ComponentKind.Cpu, null) }, "fingerprint-echo").Single();
        Assert.Equal(RemarkSeverity.Notice, r.Severity);
        Assert.Contains("First CPU fingerprint", r.Text);
    }

    [Fact]
    public void FingerprintEcho_SeverityTracksTheDrift()
    {
        Assert.Equal(RemarkSeverity.Warning,
            Fire(Ctx() with { Fingerprint = Echo(ComponentKind.GpuDiscrete, 4.2) }, "fingerprint-echo").Single().Severity);
        Assert.Equal(RemarkSeverity.Notice,
            Fire(Ctx() with { Fingerprint = Echo(ComponentKind.Cpu, -5) }, "fingerprint-echo").Single().Severity);
        Assert.Equal(RemarkSeverity.Info,
            Fire(Ctx() with { Fingerprint = Echo(ComponentKind.Cpu, 0.8) }, "fingerprint-echo").Single().Severity);
        Assert.Empty(Fire(Ctx(), "fingerprint-echo"));
    }

    [Fact]
    public void CooldownSuppressesRepeats()
    {
        var ctx = Ctx(Reading(ComponentKind.Battery, wear: 18));
        var engine = new RemarksEngine();
        Assert.Single(engine.Evaluate(ctx), r => r.RuleId == "battery-wear");
        Assert.DoesNotContain(engine.Evaluate(ctx with { NowUtc = Now.AddDays(30) }), r => r.RuleId == "battery-wear");
        Assert.Single(engine.Evaluate(ctx with { NowUtc = Now.AddDays(91) }), r => r.RuleId == "battery-wear");
    }

    // ------------------------------------------------------------------ new detection rules

    [Fact]
    public void PasteDrift_FiresOnARisingTrend_WithProjection()
    {
        var trends = new Dictionary<ComponentKind, TrendResult>
        {
            [ComponentKind.Cpu] = new(HasTrend: true, SlopePerMonthC: 1.4, Weeks: 8, CurrentExcessC: 2.5, MonthsToConcern: 2.1, Step: null),
        };
        Remark r = Fire(Ctx() with { Trends = trends }, "paste-drift").Single();
        Assert.Equal(RemarkSeverity.Warning, r.Severity);
        Assert.Contains("month", r.Text);
    }

    [Fact]
    public void PasteDrift_SilentWhenNotTrendingOrCooling()
    {
        var flat = new Dictionary<ComponentKind, TrendResult> { [ComponentKind.Cpu] = TrendResult.None };
        Assert.Empty(Fire(Ctx() with { Trends = flat }, "paste-drift"));

        var cooling = new Dictionary<ComponentKind, TrendResult>
        {
            [ComponentKind.Cpu] = new(true, SlopePerMonthC: -1.2, 8, CurrentExcessC: -1, null, null),
        };
        Assert.Empty(Fire(Ctx() with { Trends = cooling }, "paste-drift"));
    }

    [Fact]
    public void ThermalStep_FlagsADiscreteJumpWithTiming()
    {
        long weekTs = Now.AddDays(-21).ToUnixTimeSeconds();
        var trends = new Dictionary<ComponentKind, TrendResult>
        {
            [ComponentKind.GpuDiscrete] = new(false, 0, 8, 5, null, new StepChange(weekTs, 0.5, 5.5)),
        };
        Remark r = Fire(Ctx() with { Trends = trends }, "thermal-step").Single();
        Assert.Equal(RemarkSeverity.Warning, r.Severity);
        Assert.Contains("weeks ago", r.Text);
    }

    [Fact]
    public void FanSlowing_SurfacesTheScoresUndershootHint()
    {
        var reasons = new List<ScoreReason>
        {
            new("fan-undershoot", "Fans are running about 35% slower than this machine's baseline at the same load.", 0),
        };
        var scores = new Dictionary<ComponentKind, ComponentScore>
        {
            [ComponentKind.Cpu] = new(ComponentKind.Cpu, "CPU", 88, Verdict.Good, false, 1.0, reasons, PatternHint.None),
        };
        Remark r = Fire(Ctx() with { Scores = scores }, "fan-slowing").Single();
        Assert.Equal(RemarkSeverity.Notice, r.Severity);
        Assert.Contains("slower", r.Text);

        // No such reason → no remark.
        var clean = new Dictionary<ComponentKind, ComponentScore>
        {
            [ComponentKind.Cpu] = new(ComponentKind.Cpu, "CPU", 95, Verdict.Fresh, false, 1.0, Array.Empty<ScoreReason>(), PatternHint.None),
        };
        Assert.Empty(Fire(Ctx() with { Scores = clean }, "fan-slowing"));
    }

    [Fact]
    public void CheckupDue_NudgesAfterAMonthWithABaseline()
    {
        Assert.Empty(Fire(Ctx() with { DaysSinceLastFingerprint = 10 }, "checkup-due"));
        Assert.Single(Fire(Ctx() with { DaysSinceLastFingerprint = 35 }, "checkup-due"));
        // Needs a baseline first — no point nudging a machine still calibrating.
        Assert.Empty(Fire(Ctx() with { BaselineReady = false, DaysSinceLastFingerprint = 40 }, "checkup-due"));
    }
}

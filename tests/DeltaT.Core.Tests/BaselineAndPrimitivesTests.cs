using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using Xunit;

namespace DeltaT.Core.Tests;

public class BaselineBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    // A "now" far enough past the epoch start that the cure term is 1.0 and can't cap
    // confidence — so these tests exercise the data-confidence half in isolation.
    private static readonly DateTimeOffset Cured = Start.AddHours(120);

    private static BucketStat Stat(LoadBucket bucket, int minutes, double? delta = 60, int band = 2) =>
        new(bucket, band, true, minutes, minutes * 30, 85, 60, 95, 90, delta, null, 0);

    /// <summary>Session-mean provider that returns <paramref name="means"/> for the
    /// Heavy/band-2 cell and nothing for anything else.</summary>
    private static Func<LoadBucket, int, IReadOnlyList<double>> HeavyMeans(params double[] means) =>
        (bucket, band) => bucket == LoadBucket.Heavy && band == 2 ? means : Array.Empty<double>();

    private static readonly DateTimeOffset Now = new(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);

    private static PowerBandStat PBand(LoadBucket bucket, int pband, double power, int minutes = 30, double delta = 60, int band = 2) =>
        new(bucket, band, pband, minutes, delta, 4000, 85, null, power);

    [Fact]
    public void PowerSubcells_SplitAMultiModalBucketIntoItsRegimes()
    {
        // Heavy learned across boost-off (~24 W) and boost-on (~44 W), each with enough minutes
        // and well separated: two sub-cells, one per regime.
        var stats = new[]
        {
            PBand(LoadBucket.Heavy, 3, 24, delta: 50),
            PBand(LoadBucket.Heavy, 5, 44, delta: 66),
        };
        List<BaselinePowerRow> rows = BaselineBuilder.BuildPowerSubcells(1, ComponentKind.Cpu, "cpu", stats, Now);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Bucket == LoadBucket.Heavy && r.PowerAvg == 24 && r.DeltaAvg == 50);
        Assert.Contains(rows, r => r.Bucket == LoadBucket.Heavy && r.PowerAvg == 44 && r.DeltaAvg == 66);
    }

    [Fact]
    public void PowerSubcells_LeaveAUnimodalBucketAlone()
    {
        // One power band only: the blended cell already compares like-for-like, so no sub-cell.
        var stats = new[] { PBand(LoadBucket.Heavy, 5, 44) };
        Assert.Empty(BaselineBuilder.BuildPowerSubcells(1, ComponentKind.Cpu, "cpu", stats, Now));
    }

    [Fact]
    public void PowerSubcells_IgnoreRegimesTooCloseToMatter()
    {
        // 42 W vs 44 W is under the separation threshold: the power-normalizer already handles a
        // gap that small, so splitting would only add noise. No sub-cells.
        var stats = new[]
        {
            PBand(LoadBucket.Heavy, 5, 42),
            PBand(LoadBucket.Heavy, 5, 44),
        };
        Assert.Empty(BaselineBuilder.BuildPowerSubcells(1, ComponentKind.Cpu, "cpu", stats, Now));
    }

    [Fact]
    public void PowerSubcells_SkipThinRegimesAndIdle()
    {
        // A regime with too few minutes doesn't qualify, leaving a lone regime (no split); idle is
        // never split (it isn't a loaded bucket). Both yield nothing.
        var stats = new[]
        {
            PBand(LoadBucket.Heavy, 3, 24, minutes: 3),   // too thin
            PBand(LoadBucket.Heavy, 5, 44, minutes: 30),
            PBand(LoadBucket.Idle, 1, 8, minutes: 90),
            PBand(LoadBucket.Idle, 2, 14, minutes: 90),
        };
        Assert.Empty(BaselineBuilder.BuildPowerSubcells(1, ComponentKind.Cpu, "cpu", stats, Now));
    }

    [Fact]
    public void NotReady_WhileCuring_EvenWithPerfectData()
    {
        // Great, tight data on day one — but the freshly applied paste hasn't settled,
        // so confidence is capped. Only applies to repaste epochs (pasteIsFresh).
        var stats = new[] { Stat(LoadBucket.Heavy, 500) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Start.AddHours(24), stats,
            HeavyMeans(60, 60.1, 59.9, 60.05), independentLoadedSessions: 4, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.True(cal.Confidence <= 0.11);           // held down by the cure floor
        Assert.Contains("settling", cal.Constraint);
    }

    [Fact]
    public void Ready_OnDayOne_WhenPasteIsNotFresh()
    {
        // First install / recalibration: the paste is as cured as it will ever be, so
        // the clock must not gate an otherwise-earned lock.
        var stats = new[] { Stat(LoadBucket.Heavy, 500) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Start.AddHours(24), stats,
            HeavyMeans(60, 60.1, 59.9, 60.05), independentLoadedSessions: 4, pasteIsFresh: false);

        Assert.True(cal.Ready);
    }

    [Fact]
    public void NotReady_WithoutLoadedSessions_EvenAfterCuring()
    {
        // Idle-only history can never define a paste baseline.
        var stats = new[] { Stat(LoadBucket.Idle, 5000) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Start.AddDays(31), stats,
            (_, _) => Array.Empty<double>(), independentLoadedSessions: 0, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.Equal(0, cal.DataConfidence);
        Assert.Contains("load", cal.Constraint);
    }

    [Fact]
    public void NotReady_WithTooFewSessions()
    {
        // Cured and tight, but only two independent sessions — variance can't be trusted yet.
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            HeavyMeans(60, 60.1), independentLoadedSessions: 2, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.Equal(2, cal.LoadedSessions);
        Assert.Contains("session", cal.Constraint);
    }

    [Fact]
    public void Ready_WhenCuredAndPrecise()
    {
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            HeavyMeans(60, 60.2, 59.8, 60.1, 59.9), independentLoadedSessions: 5, pasteIsFresh: true); // 5 tight sessions

        Assert.True(cal.Ready);
        Assert.True(cal.Confidence >= BaselineBuilder.ReadyConfidence);
    }

    [Fact]
    public void SessionGate_UsesDeduplicatedCount_NotPerCellSums()
    {
        // One evening of gaming shows up as sessions in BOTH the Heavy and Medium
        // cells; the independence gate must count it once. With only one real bout,
        // even tight per-cell means can't unlock.
        var stats = new[] { Stat(LoadBucket.Heavy, 200), Stat(LoadBucket.Medium, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            (_, _) => new[] { 60.0, 60.1, 59.9 }, independentLoadedSessions: 1, pasteIsFresh: true);

        Assert.False(cal.Ready);
        Assert.Equal(1, cal.LoadedSessions);
    }

    [Fact]
    public void Confidence_IsHigherForTighterData()
    {
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        double tight = BaselineBuilder.Assess(Start, Cured, stats, HeavyMeans(60, 60.2, 59.8, 60.1),
            independentLoadedSessions: 4, pasteIsFresh: true).DataConfidence;
        double noisy = BaselineBuilder.Assess(Start, Cured, stats, HeavyMeans(50, 60, 70, 55),
            independentLoadedSessions: 4, pasteIsFresh: true).DataConfidence;

        Assert.True(tight > noisy);
        Assert.True(noisy < BaselineBuilder.ReadyConfidence); // scattered readings never lock
    }

    [Theory]
    [InlineData(24, 0.10)]   // before the floor: capped low
    [InlineData(72, 0.55)]   // halfway through the ramp
    [InlineData(96, 1.00)]   // fully cured
    [InlineData(200, 1.00)]  // stays at 1
    public void CureMaturity_RampsFromFloorToFull(double hours, double expected) =>
        Assert.Equal(expected, BaselineBuilder.CureMaturity(Start, Start.AddHours(hours)), 2);

    [Fact]
    public void FullLoadSession_DoesNotPoisonHeavyBaseline()
    {
        // The regression that cratered CPU confidence: a 100%-pinned load (fingerprint /
        // stress / GPU-bound game) used to pool into the same Heavy bucket as organic
        // 70-90% gaming, and its hotter mean blew up the standard error. With Max split
        // out, the two are separate cells — the hot full-load session can't touch the
        // Heavy cell's spread, so an otherwise-confident baseline stays confident.
        var stats = new[] { Stat(LoadBucket.Heavy, 200), Stat(LoadBucket.Max, 40) };
        Func<LoadBucket, int, IReadOnlyList<double>> means = (b, band) =>
            band != 2 ? Array.Empty<double>()
            : b == LoadBucket.Heavy ? new[] { 46.0, 46.5, 45.5 }       // organic gaming, tight
            : b == LoadBucket.Max ? new[] { 55.0, 55.4, 54.6 }         // fingerprint, hotter but tight
            : Array.Empty<double>();

        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats, means,
            independentLoadedSessions: 4, pasteIsFresh: false);

        Assert.True(cal.Ready, $"split baseline should stay confident, got {cal.Confidence}");
    }

    [Fact]
    public void MeterProgress_MovesOffZero_AsSoonAsLoadedDataArrives()
    {
        // The old meter (raw Confidence) sat at exactly 0 until a full loaded cell +
        // sessions existed. A few loaded minutes with no confidence yet must already show
        // visible motion — this is the "don't look stuck" fix.
        var stats = new[] { Stat(LoadBucket.Heavy, 4) };
        double p = BaselineBuilder.MeterProgress(stats, loadedSessions: 1,
            confidence: 0, cure: 1.0, pasteIsFresh: false);
        Assert.True(p > 0.10, $"expected visible early motion, got {p}");
        Assert.True(p < 0.5, "but not near-done on four minutes of data");
    }

    [Fact]
    public void MeterProgress_ClimbsMonotonically_ThenCompletesAtLock()
    {
        // Same machine, increasing evidence: a few minutes → a couple of sessions →
        // fully confident. The meter must rise at each step and only hit 1.0 when real
        // confidence reaches the lock threshold, never before.
        var early = new[] { Stat(LoadBucket.Heavy, 8) };
        var mid = new[] { Stat(LoadBucket.Heavy, 40) };
        var late = new[] { Stat(LoadBucket.Heavy, 60) };

        double a = BaselineBuilder.MeterProgress(early, 1, confidence: 0.05, cure: 1.0, pasteIsFresh: false);
        double b = BaselineBuilder.MeterProgress(mid, 2, confidence: 0.30, cure: 1.0, pasteIsFresh: false);
        double c = BaselineBuilder.MeterProgress(late, 3, confidence: 0.60, cure: 1.0, pasteIsFresh: false);
        double locked = BaselineBuilder.MeterProgress(late, 3, confidence: BaselineBuilder.ReadyConfidence, cure: 1.0, pasteIsFresh: false);

        Assert.True(a < b && b < c, $"expected a monotone climb, got {a}, {b}, {c}");
        Assert.True(c < 1.0, "must not read 100% before the baseline actually locks");
        Assert.Equal(1.0, locked, 3);
    }

    [Fact]
    public void MeterProgress_IdleOnly_ShowsOnlyTheWarmupNub()
    {
        // Idle history can never calibrate paste, so the meter stays near a small
        // warm-up floor — alive, but honestly nowhere near done.
        var stats = new[] { Stat(LoadBucket.Idle, 5000) };
        double p = BaselineBuilder.MeterProgress(stats, loadedSessions: 0,
            confidence: 0, cure: 1.0, pasteIsFresh: false);
        Assert.True(p > 0 && p <= 0.09, $"expected a small warm-up nub, got {p}");
    }

    [Fact]
    public void MeterProgress_FreshPaste_TracksCureDuringBreakIn()
    {
        // Plenty of loaded data, but the paste is still curing: the meter must not claim
        // more progress than the cure ramp allows (it can't finish before the paste does).
        var stats = new[] { Stat(LoadBucket.Heavy, 500) };
        double p = BaselineBuilder.MeterProgress(stats, loadedSessions: 4,
            confidence: 0.10, cure: 0.10, pasteIsFresh: true);
        Assert.True(p <= 0.11, $"fresh paste should be gated by cure, got {p}");
    }

    [Fact]
    public void StandardError_NullBelowTwoSamples_ElseShrinksWithN()
    {
        Assert.Null(BaselineBuilder.StandardError(Array.Empty<double>()));
        Assert.Null(BaselineBuilder.StandardError(new[] { 42.0 }));
        // {58,60,62}: sd = 2, SE = 2/sqrt(3) ≈ 1.1547
        Assert.Equal(1.1547, BaselineBuilder.StandardError(new[] { 58.0, 60, 62 })!.Value, 3);
    }

    [Fact]
    public void Build_SkipsThinCells_UnknownBands_AndNullDeltas_AndCarriesSe()
    {
        var stats = new[]
        {
            Stat(LoadBucket.Heavy, 60),                       // kept
            Stat(LoadBucket.Medium, 3),                       // too thin
            Stat(LoadBucket.Light, 60, delta: null),          // no ambient → no delta
            Stat(LoadBucket.Idle, 60, band: -1),              // unknown band
        };
        var rows = BaselineBuilder.Build(0, ComponentKind.Cpu, "cpu", stats,
            minuteDeltasFor: (_, _) => new[] { 58.0, 59, 60, 61, 62 },
            sessionMeansFor: (_, _) => new[] { 58.0, 60, 62 },
            soakRateAvg: 18, Cured);

        BaselineRow row = Assert.Single(rows);
        Assert.Equal(LoadBucket.Heavy, row.Bucket);
        Assert.Equal(62, row.DeltaP95); // p95 of the 5-value distribution
        Assert.Equal(18, row.SoakRate);
        Assert.Equal(1.1547, row.DeltaSe!.Value, 3); // SE of the 3 session means
    }

    [Fact]
    public void Percentile_Empty_IsNull_AndSingle_IsThatValue()
    {
        Assert.Null(BaselineBuilder.Percentile(Array.Empty<double>(), 0.95));
        Assert.Equal(42, BaselineBuilder.Percentile(new[] { 42.0 }, 0.95));
    }

    // ---- calibration-stuck regression coverage (the "stuck at 80%" reports) ----

    /// <summary>Session-mean provider for a well-pinned Heavy/band-2 cell plus one
    /// thinly-sampled cell (a bucket/band the machine passed through once).</summary>
    private static Func<LoadBucket, int, IReadOnlyList<double>> WellPinnedPlusRare(
        LoadBucket rareBucket, int rareBand, params double[] rareMeans) =>
        (bucket, band) =>
            bucket == LoadBucket.Heavy && band == 2 ? new[] { 60.0, 60.2, 59.8, 60.1, 59.9 }
            : bucket == rareBucket && band == rareBand ? rareMeans
            : Array.Empty<double>();

    [Fact]
    public void RareThinCell_DoesNotBlock_AWellPinnedBaseline()
    {
        // The "stuck at 80%" mechanism: a Heavy baseline that is tightly pinned over five
        // sessions is dragged below the lock bar by a single cell the machine visited once
        // (an ambient-band edge, a brief Medium spike). That thin cell can't be trusted yet,
        // but it must not veto a baseline the loaded work already earned. Weighting each
        // cell by its evidence mass keeps the rare cell's tiny sample from tanking the whole
        // average. (Before evidence-mass weighting this asserted False at conf ~0.69.)
        var stats = new[] { Stat(LoadBucket.Heavy, 200), Stat(LoadBucket.Medium, 8, band: 0) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            WellPinnedPlusRare(LoadBucket.Medium, 0, 70.0),
            independentLoadedSessions: 6, pasteIsFresh: false);

        Assert.True(cal.Ready, $"a lone thin cell must not veto a well-pinned baseline, got conf {cal.Confidence}");
    }

    [Fact]
    public void GenuinelyNoisyBaseline_StillDoesNotLock_DespiteEvidenceMass()
    {
        // Guardrail against over-correcting A1: when the WELL-SAMPLED cells are themselves
        // noisy (wide session-to-session spread), evidence-mass weighting must not fake a
        // lock. Five scattered sessions on the dominant cell stay below the bar.
        var stats = new[] { Stat(LoadBucket.Heavy, 200) };
        CalibrationState cal = BaselineBuilder.Assess(Start, Cured, stats,
            HeavyMeans(50, 60, 70, 55, 64), independentLoadedSessions: 5, pasteIsFresh: false);

        Assert.False(cal.Ready, $"a truly noisy baseline must not lock, got conf {cal.Confidence}");
    }
}

public class PrimitivesTests
{
    [Theory]
    [InlineData(0, LoadBucket.Idle)]
    [InlineData(9.9, LoadBucket.Idle)]
    [InlineData(10, LoadBucket.Light)]
    [InlineData(39.9, LoadBucket.Light)]
    [InlineData(40, LoadBucket.Medium)]
    [InlineData(69.9, LoadBucket.Medium)]
    [InlineData(70, LoadBucket.Heavy)]
    [InlineData(89.9, LoadBucket.Heavy)]
    [InlineData(90, LoadBucket.Max)]
    [InlineData(100, LoadBucket.Max)]
    public void LoadBuckets_EdgesLandWhereDocumented(double pct, LoadBucket expected) =>
        Assert.Equal(expected, LoadBuckets.FromPercent(pct));

    [Theory]
    [InlineData(-5, AmbientBand.Cold)]
    [InlineData(14.9, AmbientBand.Cold)]
    [InlineData(15, AmbientBand.Mild)]
    [InlineData(24.9, AmbientBand.Mild)]
    [InlineData(25, AmbientBand.Warm)]
    [InlineData(34.9, AmbientBand.Warm)]
    [InlineData(35, AmbientBand.Hot)]
    [InlineData(48, AmbientBand.Hot)]
    public void AmbientBands_EdgesLandWhereDocumented(double c, AmbientBand expected) =>
        Assert.Equal(expected, AmbientBands.FromCelsius(c));

    [Fact]
    public void OnlyCpuAndDiscreteGpu_HavePaste()
    {
        Assert.True(ComponentKind.Cpu.HasPaste());
        Assert.True(ComponentKind.GpuDiscrete.HasPaste());
        Assert.False(ComponentKind.GpuIntegrated.HasPaste());
        Assert.False(ComponentKind.Storage.HasPaste());
        Assert.False(ComponentKind.Battery.HasPaste());
    }

    [Fact]
    public void Ram_IsLabeled_AndNeverScored()
    {
        // The RAM card is display-only: it must never be a paste component, or it would be
        // pulled into baseline learning and scoring.
        Assert.Equal("RAM", ComponentKind.Ram.Label());
        Assert.False(ComponentKind.Ram.HasPaste());
    }

    [Fact]
    public void SimulatedSource_EmitsRamCard_AfterBattery_WithUsage()
    {
        var snap = new SimulatedSensorSource().Read();
        int battery = snap.Components.ToList().FindIndex(c => c.Kind == ComponentKind.Battery);
        int ram = snap.Components.ToList().FindIndex(c => c.Kind == ComponentKind.Ram);

        Assert.True(ram > battery && battery >= 0, "the RAM card must come after the battery");
        ComponentReading mem = snap.Components[ram];
        Assert.Null(mem.TemperatureC);                 // usage only, no thermal
        Assert.NotNull(mem.LoadPercent);               // usage drives the load bar
        Assert.NotNull(mem.MemUsedGb);
        Assert.NotNull(mem.MemTotalGb);
        Assert.True(mem.MemUsedGb <= mem.MemTotalGb);
    }
}

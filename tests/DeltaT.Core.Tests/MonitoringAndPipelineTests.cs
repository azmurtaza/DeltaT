using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Weather;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>Feeds a scripted sequence of snapshots through the real service.</summary>
internal sealed class ScriptedSource : ISensorSource
{
    private readonly Queue<SensorSnapshot> _script = new();

    public void Enqueue(SensorSnapshot snap) => _script.Enqueue(snap);

    public SensorSnapshot Read() => _script.Dequeue();

    public int Remaining => _script.Count;

    public void Dispose() { }
}

internal static class Snap
{
    public static readonly DateTimeOffset T0 = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public static SensorSnapshot Cpu(DateTimeOffset ts, double temp, double load, bool throttling = false, bool onAc = true, double limit = 100) =>
        new(ts, onAc, new[]
        {
            new ComponentReading(ComponentKind.Cpu, "CPU", temp, null, load, null, null, null, throttling, limit),
        });
}

public class SoakTrackerTests
{
    [Fact]
    public void CalmThenSlam_MeasuresSoakRate()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        SoakMeasurement? measured = null;
        monitor.SoakMeasured += m => measured = m;

        DateTimeOffset t = Snap.T0;
        // 70 s of calm idle at ~50 °C…
        for (int i = 0; i < 35; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 50 + (i % 3) * 0.3, load: 5));
        // …then slam to heavy: 50 → 90 °C over 60 s, then plateau past the 90 s window.
        for (int i = 0; i < 55; i++, t += TimeSpan.FromSeconds(2))
        {
            double temp = Math.Min(90, 50 + i * (40 / 30.0));
            source.Enqueue(Snap.Cpu(t, temp, load: 95));
        }
        while (source.Remaining > 0)
            monitor.Capture();

        Assert.NotNull(measured);
        Assert.Equal(50, measured!.StartTempC, 1.0);
        Assert.Equal(90, measured.PeakTempC, 1.0);
        Assert.InRange(measured.RatePerMinute, 25, 50); // 40 °C in ~60–90 s
    }

    [Fact]
    public void NoCalmBeforeLoad_NoMeasurement()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        SoakMeasurement? measured = null;
        monitor.SoakMeasured += m => measured = m;

        DateTimeOffset t = Snap.T0;
        // Bouncing between medium and heavy — never calm.
        for (int i = 0; i < 120; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 70 + (i % 10), load: i % 2 == 0 ? 55 : 95));
        while (source.Remaining > 0)
            monitor.Capture();

        Assert.Null(measured);
    }
}

public class CooldownTrackerTests
{
    [Fact]
    public void HotThenDrop_MeasuresCooldownRate()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        CooldownMeasurement? measured = null;
        monitor.CooldownMeasured += m => measured = m;

        DateTimeOffset t = Snap.T0;
        // 70 s of sustained heavy load at 90 °C so the die is genuinely heat-soaked…
        for (int i = 0; i < 35; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 90, load: 95));
        // …then load drops to idle and the die sheds 90 → 55 °C over ~60 s, then plateaus.
        for (int i = 0; i < 55; i++, t += TimeSpan.FromSeconds(2))
        {
            double temp = Math.Max(55, 90 - i * (35 / 30.0));
            source.Enqueue(Snap.Cpu(t, temp, load: 3));
        }
        while (source.Remaining > 0)
            monitor.Capture();

        Assert.NotNull(measured);
        Assert.Equal(90, measured!.StartTempC, 1.0);
        Assert.Equal(55, measured.SettledTempC, 1.5);
        Assert.InRange(measured.RatePerMinute, 25, 50); // 35 °C shed in ~60–90 s
    }

    [Fact]
    public void DropWithoutSustainedHeat_NoMeasurement()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        CooldownMeasurement? measured = null;
        monitor.CooldownMeasured += m => measured = m;

        DateTimeOffset t = Snap.T0;
        // Only ~20 s of heavy load (below the 60 s heat-soak requirement)…
        for (int i = 0; i < 10; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 88, load: 95));
        // …then idle. Too little soak to have real heat to shed → no clean measurement.
        for (int i = 0; i < 40; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, Math.Max(52, 88 - i * 2.0), load: 3));
        while (source.Remaining > 0)
            monitor.Capture();

        Assert.Null(measured);
    }
}

public class ThrottleDetectionTests
{
    [Fact]
    public void ThrottleEdge_FiresOnce_ThenRespectsCooldown()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        var events = new List<ThrottleEvent>();
        monitor.ThrottleDetected += events.Add;

        DateTimeOffset t = Snap.T0;
        for (int i = 0; i < 60; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 99.5, load: 100, throttling: true));
        while (source.Remaining > 0)
            monitor.Capture();

        Assert.Single(events); // 2 minutes of continuous throttling = one event (10 min cooldown)
    }
}

public class TelemetryPipelineTests : IDisposable
{
    private sealed class FixedAmbient : IAmbientProvider
    {
        public double? CurrentAmbientC { get; set; } = 30; // Warm band
        public bool IsStale { get; set; }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"deltat-test-{Guid.NewGuid():N}.db");
    private readonly DeltaTDb _db;
    private readonly TelemetryRepository _repo;

    public TelemetryPipelineTests()
    {
        _db = new DeltaTDb(_dbPath);
        _repo = new TelemetryRepository(_db);
    }

    [Fact]
    public void MinuteAggregation_ComputesDeltasAndBuckets()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, new FixedAmbient(), _repo);

        DateTimeOffset t = Snap.T0;
        // 3 full minutes at heavy load, 90 °C, ambient 30 → delta 60.
        for (int i = 0; i < 90; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 90, load: 80));
        // One extra sample to roll the last minute over.
        source.Enqueue(Snap.Cpu(t.AddSeconds(2), 50, load: 5));
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();

        long from = Snap.T0.AddMinutes(-1).ToUnixTimeSeconds();
        long to = Snap.T0.AddMinutes(10).ToUnixTimeSeconds();
        var stats = _repo.GetBucketStats(ComponentKind.Cpu, "CPU", from, to);

        BucketStat heavy = Assert.Single(stats, s => s.Bucket == LoadBucket.Heavy);
        Assert.Equal((int)AmbientBand.Warm, heavy.Band);
        Assert.True(heavy.OnAc);
        Assert.Equal(60, heavy.DeltaAvg!.Value, 1.0);
        Assert.Equal(90, heavy.TempAvg, 1.0);
        Assert.True(heavy.Minutes >= 3);
    }

    [Fact]
    public void MinuteAggregation_AveragesPackagePower()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, new FixedAmbient(), _repo);

        DateTimeOffset t = Snap.T0;
        // 3 minutes at heavy load drawing a steady 45 W.
        for (int i = 0; i < 90; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(new SensorSnapshot(t, true, new[]
            {
                new ComponentReading(ComponentKind.Cpu, "CPU", 90, null, 80, null, 45, null, false, 100),
            }));
        source.Enqueue(new SensorSnapshot(t.AddSeconds(2), true, new[]
        {
            new ComponentReading(ComponentKind.Cpu, "CPU", 50, null, 5, null, 10, null, false, 100),
        }));
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();

        var stats = _repo.GetBucketStats(ComponentKind.Cpu, "CPU",
            Snap.T0.AddMinutes(-1).ToUnixTimeSeconds(), Snap.T0.AddMinutes(10).ToUnixTimeSeconds());
        BucketStat heavy = Assert.Single(stats, s => s.Bucket == LoadBucket.Heavy);
        Assert.NotNull(heavy.PowerAvg);
        Assert.Equal(45, heavy.PowerAvg!.Value, 1.0);
    }

    [Fact]
    public void RawSamples_FlushInBatches_AndSurviveFinalFlush()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        using (var pipeline = new TelemetryPipeline(monitor, new FixedAmbient(), _repo))
        {
            DateTimeOffset t = Snap.T0;
            for (int i = 0; i < 7; i++, t += TimeSpan.FromSeconds(2)) // fewer than a batch
                source.Enqueue(Snap.Cpu(t, 60, load: 20));
            while (source.Remaining > 0)
                monitor.Capture();
        } // dispose flushes

        var series = _repo.GetSeries(ComponentKind.Cpu, "CPU",
            Snap.T0.AddMinutes(-1).ToUnixTimeSeconds(), Snap.T0.AddMinutes(5).ToUnixTimeSeconds(), "raw");
        Assert.Equal(7, series.Count);
    }

    [Fact]
    public void HourRollup_MatchesMinuteSums()
    {
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, new FixedAmbient(), _repo);

        // Cross an hour boundary: 61 minutes of light load.
        DateTimeOffset hourStart = new(2026, 7, 1, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset t = hourStart;
        for (int i = 0; i < 61 * 6; i++, t += TimeSpan.FromSeconds(10))
            source.Enqueue(Snap.Cpu(t, 65, load: 25));
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();
        _repo.RollupHour(hourStart.ToUnixTimeSeconds());

        // Flush now also rolls the hour in progress, so the 61st minute's hour exists
        // too — query just the first hour.
        var hourly = _repo.GetSeries(ComponentKind.Cpu, "CPU",
            hourStart.ToUnixTimeSeconds(), hourStart.AddMinutes(59).ToUnixTimeSeconds(), "hour");
        SeriesPoint hour = Assert.Single(hourly);
        Assert.Equal(65, hour.TempAvg!.Value, 0.5);
    }

    [Fact]
    public void UnknownAmbient_LandsInBandMinusOne_AndIsExcludableByScoring()
    {
        var source = new ScriptedSource();
        var ambient = new FixedAmbient { CurrentAmbientC = null };
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, ambient, _repo);

        DateTimeOffset t = Snap.T0;
        for (int i = 0; i < 60; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 90, load: 80));
        source.Enqueue(Snap.Cpu(t.AddSeconds(2), 50, load: 5));
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();

        var stats = _repo.GetBucketStats(ComponentKind.Cpu, "CPU",
            Snap.T0.AddMinutes(-1).ToUnixTimeSeconds(), Snap.T0.AddMinutes(10).ToUnixTimeSeconds());
        Assert.All(stats.Where(s => s.Bucket == LoadBucket.Heavy), s =>
        {
            Assert.Equal(-1, s.Band);
            Assert.Null(s.DeltaAvg);
        });
    }

    [Fact]
    public void StaleAmbient_IsTreatedAsUnknown_NotBandedWithYesterdaysWeather()
    {
        // Weather unreachable for hours: the cached value must not band new samples —
        // a day-old 20° stamped onto a heatwave poisons the learned deltas.
        var source = new ScriptedSource();
        var ambient = new FixedAmbient { CurrentAmbientC = 20, IsStale = true };
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, ambient, _repo);

        DateTimeOffset t = Snap.T0;
        for (int i = 0; i < 60; i++, t += TimeSpan.FromSeconds(2))
            source.Enqueue(Snap.Cpu(t, 90, load: 80));
        source.Enqueue(Snap.Cpu(t.AddSeconds(2), 50, load: 5));
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();

        var stats = _repo.GetBucketStats(ComponentKind.Cpu, "CPU",
            Snap.T0.AddMinutes(-1).ToUnixTimeSeconds(), Snap.T0.AddMinutes(10).ToUnixTimeSeconds());
        Assert.All(stats.Where(s => s.Bucket == LoadBucket.Heavy), s =>
        {
            Assert.Equal(-1, s.Band);
            Assert.Null(s.DeltaAvg);
        });
    }

    [Fact]
    public void PasteComponentWithoutLoadReading_IsNotFiledAsIdle()
    {
        // A CPU minute with no load sensor can't be attributed to a bucket; filing it
        // under Idle would teach the idle baseline gaming temperatures.
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, new FixedAmbient(), _repo);

        DateTimeOffset t = Snap.T0;
        for (int i = 0; i < 60; i++, t += TimeSpan.FromSeconds(2))
        {
            source.Enqueue(new SensorSnapshot(t, true, new[]
            {
                new ComponentReading(ComponentKind.Cpu, "CPU", 90, null, LoadPercent: null, null, null, null, false, 100),
                new ComponentReading(ComponentKind.Storage, "SSD", 45, null, LoadPercent: null, null, null, null, false, null),
            }));
        }
        source.Enqueue(Snap.Cpu(t.AddSeconds(2), 50, load: 5));
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();

        long from = Snap.T0.AddMinutes(-1).ToUnixTimeSeconds();
        long to = Snap.T0.AddMinutes(10).ToUnixTimeSeconds();
        // CPU minutes with unknown load were skipped entirely…
        Assert.DoesNotContain(_repo.GetBucketStats(ComponentKind.Cpu, "CPU", from, to),
            s => s.Bucket == LoadBucket.Idle && s.TempAvg > 80);
        // …while the SSD (no load sensor by design) still accumulates history.
        Assert.NotEmpty(_repo.GetBucketStats(ComponentKind.Storage, "SSD", from, to));
    }

    [Fact]
    public void HourRollup_RepairsHolesFromEarlierSessions_OnRestart()
    {
        // Session 1 ends mid-hour and disposes without a rollup for that hour (a crash
        // wouldn't even flush); session 2 must repair it so 7d/30d charts have no hole.
        DateTimeOffset hourStart = new(2026, 7, 1, 13, 0, 0, TimeSpan.Zero);
        var source1 = new ScriptedSource();
        var monitor1 = new MonitoringService(source1);
        var pipeline1 = new TelemetryPipeline(monitor1, new FixedAmbient(), _repo);
        DateTimeOffset t = hourStart;
        for (int i = 0; i < 20 * 6; i++, t += TimeSpan.FromSeconds(10)) // 20 min of samples…
            source1.Enqueue(Snap.Cpu(t, 65, load: 25));
        while (source1.Remaining > 0)
            monitor1.Capture();
        GC.KeepAlive(pipeline1); // …then "crash": no Dispose, no Flush, no hour rollup

        var source2 = new ScriptedSource();
        var monitor2 = new MonitoringService(source2);
        using var pipeline2 = new TelemetryPipeline(monitor2, new FixedAmbient(), _repo);
        source2.Enqueue(Snap.Cpu(hourStart.AddHours(3), 60, load: 20)); // next launch
        monitor2.Capture();

        var hourly = _repo.GetSeries(ComponentKind.Cpu, "CPU",
            hourStart.ToUnixTimeSeconds(), hourStart.AddHours(1).ToUnixTimeSeconds(), "hour");
        SeriesPoint hour = Assert.Single(hourly);
        Assert.Equal(65, hour.TempAvg!.Value, 0.5);
    }

    [Fact]
    public void CpuMinutes_InheritChassisFan_WhenCpuHasNoFanSensor()
    {
        // Laptops rarely expose the CPU fan; the GPU fan is the chassis proxy so
        // fan-normalized scoring can see manual overrides on the CPU side too.
        var source = new ScriptedSource();
        var monitor = new MonitoringService(source);
        using var pipeline = new TelemetryPipeline(monitor, new FixedAmbient(), _repo);

        DateTimeOffset t = Snap.T0;
        for (int i = 0; i < 60; i++, t += TimeSpan.FromSeconds(2))
        {
            source.Enqueue(new SensorSnapshot(t, true, new[]
            {
                new ComponentReading(ComponentKind.Cpu, "CPU", 90, null, 80, null, null, null, false, 100),
                new ComponentReading(ComponentKind.GpuDiscrete, "GPU", 70, null, 40, 5000, null, null, false, 87),
            }));
        }
        source.Enqueue(Snap.Cpu(t.AddSeconds(2), 50, load: 5)); // roll the last minute over
        while (source.Remaining > 0)
            monitor.Capture();
        pipeline.Flush();

        var stats = _repo.GetBucketStats(ComponentKind.Cpu, "CPU",
            Snap.T0.AddMinutes(-1).ToUnixTimeSeconds(), Snap.T0.AddMinutes(10).ToUnixTimeSeconds());
        BucketStat heavy = Assert.Single(stats, s => s.Bucket == LoadBucket.Heavy);
        Assert.NotNull(heavy.FanAvg);
        Assert.Equal(5000, heavy.FanAvg!.Value, 1.0);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

public class SimulatedSourceTests
{
    private static double AvgHeavyDelta(SimScenario scenario)
    {
        var sim = new SimulatedSensorSource(scenario, fixedAmbientC: 32, startUtc: Snap.T0, seed: 7);
        var deltas = new List<double>();
        for (int i = 0; i < 3600; i++) // 2 simulated hours
        {
            SensorSnapshot snap = sim.Read();
            ComponentReading cpu = snap.Find(ComponentKind.Cpu)!;
            if (cpu.Bucket >= LoadBucket.Heavy && cpu.TemperatureC is { } temp)
                deltas.Add(temp - 32);
        }
        Assert.True(deltas.Count > 50, "simulation never reached heavy load");
        return deltas.Average();
    }

    [Fact]
    public void DegradedPaste_RunsMeaningfullyHotterThanHealthy_AtHeavyLoad()
    {
        double healthy = AvgHeavyDelta(SimScenario.Healthy);
        double degraded = AvgHeavyDelta(SimScenario.DegradedPaste);
        Assert.True(degraded - healthy > 5, $"healthy Δ{healthy:0.#}, degraded Δ{degraded:0.#}");
    }
}

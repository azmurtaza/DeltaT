using Kelvin.Core.Monitoring;
using Kelvin.Core.Storage;
using Kelvin.Core.Weather;
using Xunit;

namespace Kelvin.Core.Tests;

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
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"kelvin-test-{Guid.NewGuid():N}.db");
    private readonly KelvinDb _db;
    private readonly TelemetryRepository _repo;

    public TelemetryPipelineTests()
    {
        _db = new KelvinDb(_dbPath);
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
            source.Enqueue(Snap.Cpu(t, 90, load: 95));
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

        var hourly = _repo.GetSeries(ComponentKind.Cpu, "CPU",
            hourStart.ToUnixTimeSeconds(), hourStart.AddHours(1).ToUnixTimeSeconds(), "hour");
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
            source.Enqueue(Snap.Cpu(t, 90, load: 95));
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
            if (cpu.Bucket == LoadBucket.Heavy && cpu.TemperatureC is { } temp)
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

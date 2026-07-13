using System.Text.Json;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Weather;

namespace DeltaT.Core.Storage;

/// <summary>Glues the monitor to the database: enriches snapshots with the current
/// outside temperature, batches raw inserts (~30 s), closes minute aggregates,
/// rolls up hours, prunes old data, and persists throttle/soak events.
/// Runs entirely on the monitor's background thread.</summary>
public sealed class TelemetryPipeline : IDisposable
{
    private static readonly TimeSpan RawRetention = TimeSpan.FromHours(48);
    private static readonly TimeSpan MinuteRetention = TimeSpan.FromDays(90);
    private const int RawFlushEvery = 15; // snapshots (~30 s at the 2 s interval)

    private readonly MonitoringService _monitor;
    private readonly IAmbientProvider _ambient;
    private readonly TelemetryRepository _repo;

    private readonly List<RawSampleRow> _rawBatch = new();
    private readonly Dictionary<(long Minute, string Id, LoadBucket Bucket, int Band, bool OnAc), MinuteAccum> _openMinutes = new();
    private long _currentMinute = -1;
    private long _currentHour = -1;
    private long _lastPruneDay = -1;
    private int _snapshotsSinceFlush;

    public event Action<string, Exception>? Error;

    public TelemetryPipeline(MonitoringService monitor, IAmbientProvider ambient, TelemetryRepository repo)
    {
        _monitor = monitor;
        _ambient = ambient;
        _repo = repo;
        _monitor.SnapshotCaptured += OnSnapshot;
        _monitor.ThrottleDetected += OnThrottle;
        _monitor.SoakMeasured += OnSoak;
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        try
        {
            // A stale reading (weather unreachable for hours) is treated as unknown:
            // banding today's samples with yesterday's temperature would quietly poison
            // the learned deltas. Unknown ambient lands in band -1, which scoring skips.
            double? ambient = _ambient.IsStale ? null : _ambient.CurrentAmbientC;
            long ts = snap.TimestampUtc.ToUnixTimeSeconds();
            long minute = ts / 60 * 60;
            long hour = ts / 3600 * 3600;
            long day = ts / 86400;

            // First snapshot after a start: repair hour rollups over the whole minute
            // retention. A shutdown (or crash) mid-hour left that hour permanently
            // unrolled — nothing later ever triggered its rollover — which punched
            // holes in the 7d/30d/ALL charts at every quit ("my trends are gone").
            // Idempotent rebuild from agg_minute; runs once per launch, off the UI thread.
            if (_currentHour < 0)
                _repo.RollupHours(hour - (long)MinuteRetention.TotalSeconds, hour);

            // Chassis fan proxy: laptops rarely expose the CPU fan directly, but
            // the fans they do expose (GPU, board) share the heatsink assembly
            // and ramp together — including manual overrides. Paste components
            // without their own fan sensor aggregate under this proxy so scoring
            // can tell "genuinely cooler" from "fans cranked".
            double? chassisFan = null;
            foreach (ComponentReading c in snap.Components)
            {
                if (c.FanRpm is { } f && (chassisFan is not { } cf || f > cf))
                    chassisFan = f;
            }

            foreach (ComponentReading c in snap.Components)
            {
                // Raw rows stay honest per-sensor (null = hardware doesn't expose it).
                _rawBatch.Add(new RawSampleRow(
                    ts, c.Kind, c.Name, c.TemperatureC, c.HotspotC, c.LoadPercent,
                    c.FanRpm, c.PowerW, c.IsThrottling, ambient, snap.OnAcPower));
                Accumulate(minute, c, ambient, snap.OnAcPower, chassisFan);
            }

            // Minute rolled over → everything accumulated for earlier minutes is final.
            if (minute != _currentMinute)
            {
                FlushClosedMinutes(minute);
                _currentMinute = minute;
            }

            if (hour != _currentHour)
            {
                if (_currentHour >= 0)
                    _repo.RollupHour(_currentHour);
                _currentHour = hour;
            }

            if (day != _lastPruneDay)
            {
                _repo.Prune(snap.TimestampUtc, RawRetention, MinuteRetention);
                _lastPruneDay = day;
            }

            if (++_snapshotsSinceFlush >= RawFlushEvery)
                FlushRaw();
        }
        catch (Exception ex)
        {
            Error?.Invoke("telemetry write failed", ex);
        }
    }

    private void Accumulate(long minute, ComponentReading c, double? ambient, bool onAc, double? chassisFan)
    {
        if (c.TemperatureC is not { } temp)
            return;
        // A paste component with no load reading can't be attributed to a bucket.
        // Defaulting it to Idle would teach the idle baseline gaming temperatures
        // (and judge idle against them later) — skip the minute instead. Non-paste
        // parts (SSD, battery, board) have no meaningful load; they keep the Idle
        // default so their thermal history still accumulates.
        if (c.Bucket is null && c.Kind.HasPaste())
            return;
        LoadBucket bucket = c.Bucket ?? LoadBucket.Idle;
        int band = ambient is { } a ? (int)AmbientBands.FromCelsius(a) : -1;

        var key = (minute, c.Id, bucket, band, onAc);
        if (!_openMinutes.TryGetValue(key, out MinuteAccum? acc))
        {
            _openMinutes[key] = acc = new MinuteAccum
            {
                Minute = minute, Kind = c.Kind, Name = c.Name,
                Bucket = bucket, Band = band, OnAc = onAc,
            };
        }

        acc.N++;
        acc.TempSum += temp;
        acc.TempMin = Math.Min(acc.TempMin, temp);
        acc.TempMax = Math.Max(acc.TempMax, temp);
        acc.LoadSum += c.LoadPercent ?? 0;
        if (ambient is { } amb)
        {
            acc.DeltaSum += temp - amb;
            acc.DeltaN++;
        }
        if ((c.FanRpm ?? (c.Kind.HasPaste() ? chassisFan : null)) is { } fan)
        {
            acc.FanSum += fan;
            acc.FanN++;
        }
        if (c.HotspotC is { } hot)
        {
            acc.GapSum += hot - temp;
            acc.GapN++;
        }
        if (c.IsThrottling)
            acc.ThrottleN++;
    }

    private void FlushClosedMinutes(long newMinute)
    {
        var closed = _openMinutes.Where(kv => kv.Key.Minute < newMinute).ToList();
        if (closed.Count == 0) return;
        _repo.UpsertMinutes(closed.Select(kv => kv.Value).ToList());
        foreach (var kv in closed)
            _openMinutes.Remove(kv.Key);
    }

    private void FlushRaw()
    {
        if (_rawBatch.Count == 0)
        {
            _snapshotsSinceFlush = 0;
            return;
        }
        _repo.InsertSamples(_rawBatch);
        _rawBatch.Clear();
        _snapshotsSinceFlush = 0;
    }

    private void OnThrottle(ThrottleEvent e)
    {
        try
        {
            _repo.InsertEvent(
                e.TimestampUtc.ToUnixTimeSeconds(), "throttle", e.Kind.ToString(), e.Name,
                severity: 2,
                message: $"{e.Kind.Label()} touched {e.TemperatureC:0}°C (limit {e.LimitC:0}°C) and pulled back.",
                dataJson: JsonSerializer.Serialize(new { temp = e.TemperatureC, limit = e.LimitC }));
        }
        catch (Exception ex) { Error?.Invoke("throttle event write failed", ex); }
    }

    private void OnSoak(SoakMeasurement m)
    {
        try
        {
            _repo.InsertEvent(
                m.TimestampUtc.ToUnixTimeSeconds(), "soak", m.Kind.ToString(), m.Name,
                severity: 0,
                message: $"{m.Kind.Label()} heat-soak: {m.StartTempC:0}→{m.PeakTempC:0}°C in {m.SecondsToPeak:0}s ({m.RatePerMinute:0.0}°C/min).",
                dataJson: JsonSerializer.Serialize(new { start = m.StartTempC, peak = m.PeakTempC, seconds = m.SecondsToPeak, rate = m.RatePerMinute }));
        }
        catch (Exception ex) { Error?.Invoke("soak event write failed", ex); }
    }

    /// <summary>Flush everything buffered (call on shutdown). Also rolls up the hour in
    /// progress — without this, the hour a session ends in never reached agg_hour.</summary>
    public void Flush()
    {
        try
        {
            FlushRaw();
            FlushClosedMinutes(long.MaxValue);
            if (_currentHour >= 0)
                _repo.RollupHour(_currentHour);
        }
        catch (Exception ex) { Error?.Invoke("final flush failed", ex); }
    }

    public void Dispose()
    {
        _monitor.SnapshotCaptured -= OnSnapshot;
        _monitor.ThrottleDetected -= OnThrottle;
        _monitor.SoakMeasured -= OnSoak;
        Flush();
    }
}

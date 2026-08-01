using System.Text.Json;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
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
    private readonly Func<int> _coolingProfile;

    private readonly List<RawSampleRow> _rawBatch = new();
    private readonly Dictionary<(long Minute, string Id, LoadBucket Bucket, int Band, bool OnAc, int Mode), MinuteAccum> _openMinutes = new();
    private long _currentMinute = -1;
    private long _currentHour = -1;
    private long _lastPruneDay = -1;
    private int _snapshotsSinceFlush;

    public event Action<string, Exception>? Error;

    /// <param name="coolingProfile">Which cooling setup is in use, so learned rows carry the
    /// airflow arrangement they were measured under. Omitted (or 0) is the default setup, which
    /// tags rows with exactly the mode values that shipped before setups existed.</param>
    public TelemetryPipeline(MonitoringService monitor, IAmbientProvider ambient, TelemetryRepository repo,
        Func<int>? coolingProfile = null)
    {
        _monitor = monitor;
        _ambient = ambient;
        _repo = repo;
        _coolingProfile = coolingProfile ?? (() => CoolingProfile.DefaultId);
        _monitor.SnapshotCaptured += OnSnapshot;
        _monitor.ThrottleDetected += OnThrottle;
        _monitor.SoakMeasured += OnSoak;
        _monitor.CooldownMeasured += OnCooldown;
    }

    private int CurrentRegime => new Regime(_ambient.AmbientMode, _coolingProfile()).Id;

    private void OnSnapshot(SensorSnapshot snap)
    {
        try
        {
            // A stale reading (weather unreachable for hours) is treated as unknown:
            // banding today's samples with yesterday's temperature would quietly poison
            // the learned deltas. Unknown ambient lands in band -1, which scoring skips.
            double? ambient = _ambient.IsStale ? null : _ambient.CurrentAmbientC;
            // The reference regime this row belongs to: the ambient source paired with the
            // cooling setup in use. Every learned row is tagged with it so incomparable regimes'
            // baselines never blend. With no cooling setups declared this is the plain ambient
            // mode, exactly as before.
            int mode = CurrentRegime;
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

            // Total package watts across the paste components, so each one can record what
            // its NEIGHBOUR was drawing at the same instant (its own share subtracted below).
            // A laptop moves CPU and GPU heat through one heatpipe stack, so the neighbour's
            // watts are part of what set this component's rise and none of it is its paste:
            // measured on the dev laptop, holding CPU power at 13.5 W, the CPU's rise still
            // moves 22.1 to 30.6 °C as the GPU goes 5 to 35 W.
            double pasteWatts = 0;
            int pasteWithPower = 0;
            foreach (ComponentReading c in snap.Components)
            {
                if (c.Kind.HasPaste() && c.PowerW is { } w && w > 0)
                {
                    pasteWatts += w;
                    pasteWithPower++;
                }
            }

            // DeltaT's own load (the fingerprint burners) and its thermal tail are recorded as
            // raw history so the charts still show the run, but never AGGREGATED: a diagnostic
            // load is not a workload the machine does, and its operating point is its own. The
            // GPU burner reaches ~120 W on a 140 W card, so aggregating its minutes taught the
            // full-load bucket a wattage no game produces and left the recent window reporting
            // the machine drawing under its own baseline. Only the paste components are held
            // back; the SSD, battery and board keep accumulating.
            bool synthetic = _monitor.IsSyntheticLoad(snap.TimestampUtc);

            foreach (ComponentReading c in snap.Components)
            {
                // Raw rows stay honest per-sensor (null = hardware doesn't expose it).
                _rawBatch.Add(new RawSampleRow(
                    ts, c.Kind, c.Name, c.TemperatureC, c.HotspotC, c.LoadPercent,
                    c.FanRpm, c.PowerW, c.IsThrottling, ambient, snap.OnAcPower));

                if (synthetic && c.Kind.HasPaste())
                    continue;

                // The neighbour's watts: everything the other paste components drew. Null
                // unless a second paste component actually reported power this tick, so a
                // desktop CPU with no readable GPU power (and a single-component machine)
                // records nothing rather than a misleading zero.
                double own = c.Kind.HasPaste() && c.PowerW is { } p && p > 0 ? p : 0;
                double? coPower = c.Kind.HasPaste() && pasteWithPower > (own > 0 ? 1 : 0)
                    ? pasteWatts - own
                    : null;
                Accumulate(minute, c, ambient, snap.OnAcPower, chassisFan, mode, coPower);
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

    private void Accumulate(long minute, ComponentReading c, double? ambient, bool onAc, double? chassisFan, int mode, double? coPower = null)
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

        var key = (minute, c.Id, bucket, band, onAc, mode);
        if (!_openMinutes.TryGetValue(key, out MinuteAccum? acc))
        {
            _openMinutes[key] = acc = new MinuteAccum
            {
                Minute = minute, Kind = c.Kind, Name = c.Name,
                Bucket = bucket, Band = band, OnAc = onAc, Mode = mode,
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
        // Package power drives thermal-resistance scoring (ΔT ∝ P). Only positive,
        // physically-plausible readings; a zero or negative power sample is a sensor
        // dropout, not real dissipation, and would poison the cell's mean watts.
        if (c.PowerW is { } power && power > 0)
        {
            acc.PowerSum += power;
            acc.PowerN++;
        }
        if (coPower is { } co && co > 0)
        {
            acc.CoPowerSum += co;
            acc.CoPowerN++;
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
                dataJson: JsonSerializer.Serialize(new { temp = e.TemperatureC, limit = e.LimitC }),
                mode: CurrentRegime);
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
                dataJson: JsonSerializer.Serialize(new { start = m.StartTempC, peak = m.PeakTempC, seconds = m.SecondsToPeak, rate = m.RatePerMinute, on_ac = m.OnAcPower ? 1 : 0 }),
                mode: CurrentRegime);
        }
        catch (Exception ex) { Error?.Invoke("soak event write failed", ex); }
    }

    private void OnCooldown(CooldownMeasurement m)
    {
        try
        {
            _repo.InsertEvent(
                m.TimestampUtc.ToUnixTimeSeconds(), "cooldown", m.Kind.ToString(), m.Name,
                severity: 0,
                message: $"{m.Kind.Label()} cooldown: {m.StartTempC:0}→{m.SettledTempC:0}°C in {m.SecondsToSettle:0}s ({m.RatePerMinute:0.0}°C/min).",
                dataJson: JsonSerializer.Serialize(new { start = m.StartTempC, settled = m.SettledTempC, seconds = m.SecondsToSettle, rate = m.RatePerMinute, on_ac = m.OnAcPower ? 1 : 0 }),
                mode: CurrentRegime);
        }
        catch (Exception ex) { Error?.Invoke("cooldown event write failed", ex); }
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
        _monitor.CooldownMeasured -= OnCooldown;
        Flush();
    }
}

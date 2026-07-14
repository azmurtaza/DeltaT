namespace DeltaT.Core.Monitoring;

public sealed record ThrottleEvent(DateTimeOffset TimestampUtc, ComponentKind Kind, string Name, double TemperatureC, double LimitC);

/// <summary>How fast a component heat-soaks when load slams from quiet to heavy.
/// Drying paste transfers heat poorly, so the die spikes faster — this is one of
/// the strongest degradation signals we have.</summary>
public sealed record SoakMeasurement(
    DateTimeOffset TimestampUtc,
    ComponentKind Kind,
    string Name,
    double StartTempC,
    double PeakTempC,
    double SecondsToPeak,
    double RatePerMinute);

/// <summary>How fast a component sheds heat when a sustained load drops away. The
/// paste conducts heat OUT of the die just as it conducts it in, so degraded paste
/// cools slowly as well as heating fast — an independent corroboration of the soak
/// signal, observed on the opposite edge (whenever a game or render finishes), so a
/// machine used in long steady sessions still produces it. RatePerMinute is the
/// magnitude of the fall (°C/min, always positive).</summary>
public sealed record CooldownMeasurement(
    DateTimeOffset TimestampUtc,
    ComponentKind Kind,
    string Name,
    double StartTempC,
    double SettledTempC,
    double SecondsToSettle,
    double RatePerMinute);

/// <summary>Owns the sampling loop: reads the sensor source on a fixed interval,
/// keeps a recent in-memory window, and detects throttle/heat-soak events.
/// Persistence and scoring live elsewhere — this class only observes.</summary>
public sealed class MonitoringService : IAsyncDisposable
{
    private readonly ISensorSource _source;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly List<SensorSnapshot> _window = new();
    private readonly Dictionary<string, SoakTracker> _soakTrackers = new();
    private readonly Dictionary<string, CooldownTracker> _cooldownTrackers = new();
    private readonly Dictionary<string, DateTimeOffset> _lastThrottleEvent = new();
    private static readonly TimeSpan ThrottleEventCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan WindowSpan = TimeSpan.FromHours(1);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<SensorSnapshot>? SnapshotCaptured;
    public event Action<ThrottleEvent>? ThrottleDetected;
    public event Action<SoakMeasurement>? SoakMeasured;
    public event Action<CooldownMeasurement>? CooldownMeasured;
    public event Action<string, Exception>? Error;

    public MonitoringService(ISensorSource source, TimeSpan? interval = null)
    {
        _source = source;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public TimeSpan Interval => _interval;

    public bool IsPaused { get; set; }

    public SensorSnapshot? Latest
    {
        get { lock (_gate) return _window.Count > 0 ? _window[^1] : null; }
    }

    public IReadOnlyList<SensorSnapshot> RecentWindow(TimeSpan span)
    {
        lock (_gate)
        {
            if (_window.Count == 0) return Array.Empty<SensorSnapshot>();
            DateTimeOffset cutoff = _window[^1].TimestampUtc - span;
            // Timestamps are appended in order, so find the first in-range index
            // instead of filtering the whole hour.
            int lo = 0, hi = _window.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_window[mid].TimestampUtc < cutoff) lo = mid + 1;
                else hi = mid;
            }
            return _window.GetRange(lo, _window.Count - lo);
        }
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsPaused)
                    Capture();
            }
            catch (Exception ex)
            {
                Error?.Invoke("sensor read failed", ex);
            }

            try { await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One sampling step. Public so tests can drive the service without timers.</summary>
    public void Capture()
    {
        SensorSnapshot snap = _source.Read();

        lock (_gate)
        {
            _window.Add(snap);
            DateTimeOffset cutoff = snap.TimestampUtc - WindowSpan;
            while (_window.Count > 0 && _window[0].TimestampUtc < cutoff)
                _window.RemoveAt(0);
        }

        foreach (ComponentReading c in snap.Components)
        {
            DetectThrottle(snap.TimestampUtc, c);
            DetectSoak(snap.TimestampUtc, c);
            DetectCooldown(snap.TimestampUtc, c);
        }

        SnapshotCaptured?.Invoke(snap);
    }

    private void DetectThrottle(DateTimeOffset ts, ComponentReading c)
    {
        if (!c.IsThrottling || c.TemperatureC is not { } t || c.ThrottleLimitC is not { } limit)
            return;
        if (_lastThrottleEvent.TryGetValue(c.Id, out DateTimeOffset last) && ts - last < ThrottleEventCooldown)
            return;
        _lastThrottleEvent[c.Id] = ts;
        ThrottleDetected?.Invoke(new ThrottleEvent(ts, c.Kind, c.Name, t, limit));
    }

    private void DetectSoak(DateTimeOffset ts, ComponentReading c)
    {
        if (!c.Kind.HasPaste() || c.TemperatureC is not { } temp || c.Bucket is not { } bucket)
            return;

        if (!_soakTrackers.TryGetValue(c.Id, out SoakTracker? tracker))
            _soakTrackers[c.Id] = tracker = new SoakTracker();

        SoakMeasurement? done = tracker.Advance(ts, c.Kind, c.Name, temp, bucket);
        if (done is not null)
            SoakMeasured?.Invoke(done);
    }

    private void DetectCooldown(DateTimeOffset ts, ComponentReading c)
    {
        if (!c.Kind.HasPaste() || c.TemperatureC is not { } temp || c.Bucket is not { } bucket)
            return;

        if (!_cooldownTrackers.TryGetValue(c.Id, out CooldownTracker? tracker))
            _cooldownTrackers[c.Id] = tracker = new CooldownTracker();

        CooldownMeasurement? done = tracker.Advance(ts, c.Kind, c.Name, temp, bucket);
        if (done is not null)
            CooldownMeasured?.Invoke(done);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); }
                catch { /* shutting down */ }
            }
            _cts.Dispose();
        }
        _source.Dispose();
    }

    /// <summary>State machine per component: wait for a calm stretch (quiet load,
    /// stable temp), then when load jumps to heavy, time how fast the temperature
    /// races to its peak over the next 90 seconds.</summary>
    private sealed class SoakTracker
    {
        private static readonly TimeSpan CalmRequired = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MeasureSpan = TimeSpan.FromSeconds(90);
        private const double CalmTempSwing = 3.0;

        private enum Phase { Watching, Calm, Measuring }

        private Phase _phase = Phase.Watching;
        private DateTimeOffset _calmSince;
        private double _calmMin, _calmMax;
        private DateTimeOffset _measureStart;
        private double _startTemp, _peakTemp;
        private DateTimeOffset _peakAt;

        public SoakMeasurement? Advance(DateTimeOffset ts, ComponentKind kind, string name, double temp, LoadBucket bucket)
        {
            switch (_phase)
            {
                case Phase.Watching:
                    if (bucket is LoadBucket.Idle or LoadBucket.Light)
                    {
                        _phase = Phase.Calm;
                        _calmSince = ts;
                        _calmMin = _calmMax = temp;
                    }
                    return null;

                case Phase.Calm:
                    if (bucket is LoadBucket.Idle or LoadBucket.Light)
                    {
                        _calmMin = Math.Min(_calmMin, temp);
                        _calmMax = Math.Max(_calmMax, temp);
                        if (_calmMax - _calmMin > CalmTempSwing)
                        {
                            // Temperature is wandering — restart the calm clock.
                            _calmSince = ts;
                            _calmMin = _calmMax = temp;
                        }
                        return null;
                    }
                    if (bucket >= LoadBucket.Heavy && ts - _calmSince >= CalmRequired)
                    {
                        _phase = Phase.Measuring;
                        _measureStart = ts;
                        _startTemp = temp;
                        _peakTemp = temp;
                        _peakAt = ts;
                        return null;
                    }
                    _phase = Phase.Watching; // medium load, or heavy/full without enough calm — not a clean edge
                    return null;

                case Phase.Measuring:
                    if (temp > _peakTemp)
                    {
                        _peakTemp = temp;
                        _peakAt = ts;
                    }
                    bool loadDropped = bucket < LoadBucket.Medium;
                    bool timeUp = ts - _measureStart >= MeasureSpan;
                    if (!loadDropped && !timeUp)
                        return null;

                    _phase = Phase.Watching;
                    double seconds = Math.Max(1, (_peakAt - _measureStart).TotalSeconds);
                    double rise = _peakTemp - _startTemp;
                    if (rise < 5 || loadDropped && ts - _measureStart < TimeSpan.FromSeconds(20))
                        return null; // too small or too brief to mean anything
                    return new SoakMeasurement(ts, kind, name, _startTemp, _peakTemp, seconds, rise / seconds * 60.0);

                default:
                    return null;
            }
        }
    }

    /// <summary>The falling-edge mirror of <see cref="SoakTracker"/>: wait until the
    /// die has been genuinely heat-soaked under sustained load, then when the load
    /// drops away, time how fast it sheds that heat over the next 90 seconds. Healthy
    /// paste dumps heat quickly; dried or pumped-out paste lets it linger. The rate is
    /// reported as a positive °C/min magnitude of the fall.</summary>
    private sealed class CooldownTracker
    {
        private static readonly TimeSpan HotRequired = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MeasureSpan = TimeSpan.FromSeconds(90);

        private enum Phase { Watching, Hot, Measuring }

        private Phase _phase = Phase.Watching;
        private DateTimeOffset _hotSince;
        private double _hotMaxTemp;
        private DateTimeOffset _measureStart;
        private double _startTemp, _troughTemp;
        private DateTimeOffset _troughAt;

        public CooldownMeasurement? Advance(DateTimeOffset ts, ComponentKind kind, string name, double temp, LoadBucket bucket)
        {
            switch (_phase)
            {
                case Phase.Watching:
                    if (bucket >= LoadBucket.Heavy)
                    {
                        _phase = Phase.Hot;
                        _hotSince = ts;
                        _hotMaxTemp = temp;
                    }
                    return null;

                case Phase.Hot:
                    if (bucket >= LoadBucket.Medium)
                    {
                        // Still loaded — keep soaking. Track the hottest the die reached.
                        _hotMaxTemp = Math.Max(_hotMaxTemp, temp);
                        return null;
                    }
                    // Load dropped away. Only measure if the die was hot long enough to
                    // have actually heat-soaked — otherwise there's little heat to shed.
                    if (ts - _hotSince >= HotRequired)
                    {
                        _phase = Phase.Measuring;
                        _measureStart = ts;
                        _startTemp = temp;
                        _troughTemp = temp;
                        _troughAt = ts;
                        return null;
                    }
                    _phase = Phase.Watching;
                    return null;

                case Phase.Measuring:
                    if (temp < _troughTemp)
                    {
                        _troughTemp = temp;
                        _troughAt = ts;
                    }
                    bool loadReturned = bucket >= LoadBucket.Medium;
                    bool timeUp = ts - _measureStart >= MeasureSpan;
                    if (!loadReturned && !timeUp)
                        return null;

                    _phase = Phase.Watching;
                    double seconds = Math.Max(1, (_troughAt - _measureStart).TotalSeconds);
                    double fall = _startTemp - _troughTemp;
                    if (fall < 5 || loadReturned && ts - _measureStart < TimeSpan.FromSeconds(20))
                        return null; // too small or too brief to mean anything
                    return new CooldownMeasurement(ts, kind, name, _startTemp, _troughTemp, seconds, fall / seconds * 60.0);

                default:
                    return null;
            }
        }
    }
}

using System.Text.Json;
using System.Windows.Threading;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Remarks;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Weather;

namespace DeltaT.App.Services;

/// <summary>Assembles a RemarkContext once a minute, runs the rules, persists
/// what fired, and raises remarks to the UI on the dispatcher. Also snapshots
/// each paste score once a day (as a 'score' event) so week-over-week drops can
/// be detected.</summary>
public sealed class RemarksCoordinator : IDisposable
{
    private readonly RemarksEngine _engine = new();
    private readonly MonitoringService _monitor;
    private readonly AmbientService _ambient;
    private readonly TelemetryRepository _repo;
    private readonly ScoreCoordinator _scores;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public event Action<Remark>? RemarkRaised;

    public RemarksCoordinator(
        MonitoringService monitor, AmbientService ambient, TelemetryRepository repo,
        ScoreCoordinator scores, SettingsStore settings)
    {
        _monitor = monitor;
        _ambient = ambient;
        _repo = repo;
        _scores = scores;
        _settings = settings;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _engine.ImportCooldownState(_settings.Get("remarks.cooldowns"));

        // Quick first pass (so "hello" lands ~20 s after first launch), then steady 60 s.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(60);
            if (_busy) return;
            _busy = true;
            Task.Run(() =>
            {
                try { Evaluate(); }
                catch { /* remarks must never hurt monitoring */ }
                finally { _busy = false; }
            });
        };
        _timer.Start();
    }

    private void Evaluate()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SensorSnapshot? latest = _monitor.Latest;
        if (latest is null)
            return;

        long nowTs = now.ToUnixTimeSeconds();
        var scores = _scores.Latest;

        SnapshotDailyScores(scores, nowTs);

        var ctx = new RemarkContext(
            NowUtc: now,
            FirstEver: !_settings.GetBool("remarks.helloDone", false),
            Latest: latest,
            ShortTrend: BuildShortTrend(),
            AmbientC: _ambient.CurrentAmbientC,
            AmbientStale: _ambient.IsStale,
            City: _ambient.Location?.City,
            Scores: scores.Count > 0 ? scores : null,
            ScoreDropThisWeek: BuildScoreDrops(scores, nowTs),
            ThrottleEventsLastHour: _repo.CountEvents("throttle", null, nowTs - 3600, nowTs),
            AllTimeMax: new Dictionary<ComponentKind, double?>
            {
                [ComponentKind.Cpu] = _repo.GetAllTimeMax(ComponentKind.Cpu),
                [ComponentKind.GpuDiscrete] = _repo.GetAllTimeMax(ComponentKind.GpuDiscrete),
            }.Where(kv => kv.Value.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value!.Value),
            OnAcPower: latest.OnAcPower,
            BaselineReady: _scores.IsBaselineReady,
            BaselineJustBecameReady: _scores.BaselineJustBecameReady,
            CalibrationProgress: scores.Count > 0 ? scores.Values.Max(s => s.CalibrationProgress) : 0,
            LearningDay: Math.Max(1, (int)(now - _scores.EpochStart).TotalDays + 1),
            BaselineStale: _scores.BaselineStale,
            DormantDays: _scores.DormantDays,
            ScoreVsLastMonth: BuildMonthlyComparison(scores, nowTs),
            RepasteOutcome: _scores.ConsumeRepasteReport(),
            CalibrationConstraint: scores.Values
                .Where(s => s.Calibrating)
                .OrderByDescending(s => s.CalibrationProgress)
                .FirstOrDefault()?.CalibrationConstraint ?? "");

        IReadOnlyList<Remark> fired = _engine.Evaluate(ctx);
        foreach (Remark remark in fired)
        {
            _repo.InsertEvent(nowTs, "remark", remark.Kind?.ToString(), null, (int)remark.Severity, remark.Text,
                JsonSerializer.Serialize(new { rule = remark.RuleId }));
            if (remark.RuleId == "hello")
                _settings.SetBool("remarks.helloDone", true);
            Remark r = remark;
            _dispatcher.BeginInvoke(() => RemarkRaised?.Invoke(r));
        }
        if (fired.Count > 0)
            _settings.Set("remarks.cooldowns", _engine.ExportCooldownState());
    }

    private IReadOnlyDictionary<ComponentKind, (double, double, bool)>? BuildShortTrend()
    {
        IReadOnlyList<SensorSnapshot> hour = _monitor.RecentWindow(TimeSpan.FromHours(1));
        if (hour.Count < 900) // need most of an hour of 2 s samples
            return null;

        DateTimeOffset cut = hour[^1].TimestampUtc - TimeSpan.FromMinutes(10);
        var result = new Dictionary<ComponentKind, (double, double, bool)>();
        foreach (ComponentKind kind in new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete })
        {
            var recent = new List<(double Temp, double Load)>();
            var earlier = new List<(double Temp, double Load)>();
            foreach (SensorSnapshot snap in hour)
            {
                if (snap.Find(kind) is not { TemperatureC: { } t, LoadPercent: { } l })
                    continue;
                if (snap.TimestampUtc >= cut) recent.Add((t, l));
                else earlier.Add((t, l));
            }
            if (recent.Count < 60 || earlier.Count < 300)
                continue;
            bool similarLoad = Math.Abs(recent.Average(x => x.Load) - earlier.Average(x => x.Load)) <= 15;
            result[kind] = (recent.Average(x => x.Temp), earlier.Average(x => x.Temp), similarLoad);
        }
        return result.Count > 0 ? result : null;
    }

    private void SnapshotDailyScores(IReadOnlyDictionary<ComponentKind, ComponentScore> scores, long nowTs)
    {
        foreach ((ComponentKind kind, ComponentScore score) in scores)
        {
            if (score.Calibrating)
                continue;
            string key = $"remarks.lastScoreSnap.{kind}";
            long last = long.TryParse(_settings.Get(key), out long l) ? l : 0;
            if (nowTs - last < 20 * 3600)
                continue;
            _repo.InsertEvent(nowTs, "score", kind.ToString(), null, 0, $"{kind.Label()} paste score {score.Value}",
                JsonSerializer.Serialize(new { value = score.Value }));
            _settings.Set(key, nowTs.ToString());
        }
    }

    private IReadOnlyDictionary<ComponentKind, int>? BuildScoreDrops(IReadOnlyDictionary<ComponentKind, ComponentScore> scores, long nowTs)
    {
        if (scores.Count == 0)
            return null;
        var drops = new Dictionary<ComponentKind, int>();
        foreach ((ComponentKind kind, ComponentScore score) in scores)
        {
            if (score.Calibrating)
                continue;
            // Oldest score snapshot from the last 8 days = "a week ago".
            StoredEvent? reference = _repo
                .GetEvents("score", nowTs - 8 * 86400, nowTs, 50)
                .Where(e => e.Kind == kind.ToString())
                .OrderBy(e => e.Ts)
                .FirstOrDefault();
            if (reference?.Data is { } json
                && JsonDocument.Parse(json).RootElement.TryGetProperty("value", out JsonElement v))
            {
                drops[kind] = Math.Max(0, v.GetInt32() - score.Value);
            }
        }
        return drops.Count > 0 ? drops : null;
    }

    /// <summary>Current score vs the daily snapshot nearest ~30 days ago, per component —
    /// feeds the monthly readout. Reuses the same 'score' events SnapshotDailyScores writes.</summary>
    private IReadOnlyDictionary<ComponentKind, (int LastMonth, int Now)>? BuildMonthlyComparison(
        IReadOnlyDictionary<ComponentKind, ComponentScore> scores, long nowTs)
    {
        if (scores.Count == 0)
            return null;
        const long month = 30 * 86400;
        long targetTs = nowTs - month;
        var result = new Dictionary<ComponentKind, (int, int)>();
        foreach ((ComponentKind kind, ComponentScore score) in scores)
        {
            if (score.Calibrating)
                continue;
            // Snapshots from 25–40 days ago; pick the one closest to a month back.
            StoredEvent? reference = _repo
                .GetEvents("score", nowTs - 40 * 86400, nowTs - 25 * 86400, 100)
                .Where(e => e.Kind == kind.ToString())
                .OrderBy(e => Math.Abs(e.Ts - targetTs))
                .FirstOrDefault();
            if (reference?.Data is { } json
                && JsonDocument.Parse(json).RootElement.TryGetProperty("value", out JsonElement v))
            {
                result[kind] = (v.GetInt32(), score.Value);
            }
        }
        return result.Count > 0 ? result : null;
    }

    public void Dispose() => _timer.Stop();
}

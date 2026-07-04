using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

/// <summary>Assembles ScoreInputs from the database and runs the engine for every
/// pasted component (CPU + discrete GPU). Owns the epoch lifecycle: first run
/// starts epoch 0; a repaste starts the next epoch with a fresh learning window.</summary>
public sealed class ScoreCoordinator
{
    private readonly TelemetryRepository _repo;
    private readonly SettingsStore _settings;
    private readonly ThermalProfile _profile;
    private readonly Func<SensorSnapshot?> _latestSnapshot;
    private readonly Func<double, string> _fmtTemp;
    private readonly object _gate = new();

    private Dictionary<ComponentKind, ComponentScore> _latest = new();
    private bool _wasReady;

    public event Action<IReadOnlyDictionary<ComponentKind, ComponentScore>>? ScoresUpdated;

    /// <summary>Set when a Compute() pass sees the baseline turn ready for the first time.</summary>
    public bool BaselineJustBecameReady { get; private set; }

    /// <summary>True once any pasted component has a locked baseline.</summary>
    public bool IsBaselineReady => _wasReady;

    public ScoreCoordinator(
        TelemetryRepository repo,
        SettingsStore settings,
        ThermalProfile profile,
        Func<SensorSnapshot?> latestSnapshot,
        Func<double, string>? fmtTemp = null)
    {
        _repo = repo;
        _settings = settings;
        _profile = profile;
        _latestSnapshot = latestSnapshot;
        _fmtTemp = fmtTemp ?? (t => $"{t:0} °C");

        if (_settings.GetInt(SettingsKeys.BaselineEpoch) is null)
        {
            _settings.SetInt(SettingsKeys.BaselineEpoch, 0);
            _settings.SetTimestamp(SettingsKeys.BaselineEpochStart, DateTimeOffset.UtcNow);
        }
    }

    public int Epoch => _settings.GetInt(SettingsKeys.BaselineEpoch) ?? 0;

    public DateTimeOffset EpochStart => _settings.GetTimestamp(SettingsKeys.BaselineEpochStart) ?? DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<ComponentKind, ComponentScore> Latest
    {
        get { lock (_gate) return _latest; }
    }

    public IReadOnlyDictionary<ComponentKind, ComponentScore> Compute(DateTimeOffset nowUtc)
    {
        var results = new Dictionary<ComponentKind, ComponentScore>();
        SensorSnapshot? snap = _latestSnapshot();
        if (snap is null)
            return results;

        DateTimeOffset epochStart = EpochStart;
        DateTimeOffset learningEnd = epochStart + BaselineBuilder.LearningWindow;
        bool anyReady = false;

        foreach (ComponentReading c in snap.Components.Where(c => c.Kind.HasPaste()))
        {
            ComponentScore score = ScoreComponent(c, epochStart, learningEnd, nowUtc, out bool ready);
            results[c.Kind] = score;
            anyReady |= ready;
        }

        BaselineJustBecameReady = anyReady && !_wasReady;
        _wasReady = anyReady;

        if (BaselineJustBecameReady && Epoch > 0)
            ReportRepasteOutcome(nowUtc);

        lock (_gate) _latest = results;
        ScoresUpdated?.Invoke(results);
        return results;
    }

    /// <summary>The payoff moment: the post-repaste baseline just locked, so we
    /// can tell the user exactly what the fresh paste bought them.</summary>
    private void ReportRepasteOutcome(DateTimeOffset nowUtc)
    {
        IReadOnlyList<BaselineRow> before = _repo.GetBaseline(Epoch - 1);
        IReadOnlyList<BaselineRow> after = _repo.GetBaseline(Epoch);
        var gains = new List<string>();

        foreach (ComponentKind kind in new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete })
        {
            // Compare the heaviest bucket both epochs know, same ambient band.
            var pair = (
                from b in before
                from a in after
                where b.Kind == kind && a.Kind == kind
                      && b.Bucket == a.Bucket && b.Band == a.Band
                      && b.Bucket >= LoadBucket.Medium
                orderby a.Bucket descending, Math.Min(a.Minutes, b.Minutes) descending
                select (Before: b, After: a)).FirstOrDefault();
            if (pair.Before is null || pair.After is null)
                continue;
            double gain = pair.Before.DeltaAvg - pair.After.DeltaAvg;
            gains.Add($"{kind.Label()} {(gain >= 0 ? "−" : "+")}{Math.Abs(gain):0.#}°");
        }

        if (gains.Count == 0)
            return;
        string summary = string.Join(", ", gains);
        bool improved = gains.Any(g => g.Contains('−'));
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "remark", null, null, 1,
            improved
                ? $"Repaste verdict is in: {summary} under load versus the old paste, weather-corrected. Money well spent."
                : $"Repaste verdict: {summary} under load versus the old paste. It barely moved — either the old paste was fine, or the mount deserves a second look.",
            null);
    }

    private ComponentScore ScoreComponent(
        ComponentReading c, DateTimeOffset epochStart, DateTimeOffset learningEnd, DateTimeOffset nowUtc, out bool ready)
    {
        long epochStartTs = epochStart.ToUnixTimeSeconds();
        long nowTs = nowUtc.ToUnixTimeSeconds();
        long learningEndTs = Math.Min(learningEnd.ToUnixTimeSeconds(), nowTs);

        // Baseline pool: learning window, AC power only, known ambient only.
        var baselineStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, epochStartTs, learningEndTs));

        ready = BaselineBuilder.IsReady(epochStart, nowUtc, baselineStats);

        // If the calendar window has passed but the machine never saw real load,
        // keep learning until it has (idle-only data can't define "healthy").
        if (!ready && nowUtc > learningEnd)
        {
            baselineStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, epochStartTs, nowTs));
            learningEndTs = nowTs;
            ready = BaselineBuilder.IsReady(epochStart, nowUtc, baselineStats);
        }

        double progress = BaselineBuilder.Progress(epochStart, nowUtc, baselineStats);
        double? soakBaseline = _repo.GetAverageSoakRate(c.Kind, epochStartTs, learningEndTs);

        // Recent pool: last 7 days that are NOT part of the learning window.
        long recentFromTs = Math.Max(learningEndTs, nowTs - (long)TimeSpan.FromDays(7).TotalSeconds);
        var recentStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, recentFromTs, nowTs));
        double recentHours = Math.Max(1, (nowTs - recentFromTs) / 3600.0);

        var input = new ScoreInput(
            c.Kind, c.Name,
            Recent: recentStats.Select(s => new RecentBucketObs(
                s.Bucket, s.Band, s.Minutes, s.DeltaAvg, s.TempAvg, s.TempMax, s.FanAvg, s.ThrottleCount)).ToList(),
            Baseline: ready ? BuildBaseline(c, epochStartTs, learningEndTs, baselineStats, soakBaseline, nowUtc) : Array.Empty<BaselineBucket>(),
            RecentWindowHours: recentHours,
            ThrottleEvents: _repo.CountEvents("throttle", c.Kind, recentFromTs, nowTs),
            SoakRateRecent: _repo.GetAverageSoakRate(c.Kind, recentFromTs, nowTs),
            SoakRateBaseline: soakBaseline,
            LimitC: c.ThrottleLimitC,
            Profile: c.Kind == ComponentKind.Cpu ? _profile.Cpu : _profile.Gpu,
            BaselineReady: ready,
            CalibrationProgress: progress);

        return ScoringEngine.Score(input, _fmtTemp);
    }

    private List<BaselineBucket> BuildBaseline(
        ComponentReading c, long fromTs, long toTs, IReadOnlyList<BucketStat> stats, double? soakAvg, DateTimeOffset nowUtc)
    {
        List<BaselineRow> rows = BaselineBuilder.Build(
            Epoch, c.Kind, c.Name, stats,
            (bucket, band) => _repo.GetMinuteDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, toTs),
            soakAvg, nowUtc);
        _repo.UpsertBaseline(rows);
        return rows.Select(r => new BaselineBucket(r.Bucket, r.Band, r.DeltaAvg, r.DeltaP95, r.FanAvg, r.Minutes)).ToList();
    }

    /// <summary>Paste scoring only trusts samples on AC power with known ambient —
    /// battery power limits and unknown weather both make deltas incomparable.</summary>
    private static List<BucketStat> FilterForScoring(IReadOnlyList<BucketStat> stats) =>
        stats.Where(s => s.OnAc && s.Band >= 0).ToList();

    /// <summary>User repasted: bump the epoch, restart learning, log the event.
    /// A few days later the before/after comparison lives in the events + baselines.</summary>
    public void RegisterRepaste(DateTimeOffset nowUtc, string? note = null)
    {
        int newEpoch = Epoch + 1;
        _settings.SetInt(SettingsKeys.BaselineEpoch, newEpoch);
        _settings.SetTimestamp(SettingsKeys.BaselineEpochStart, nowUtc);
        _wasReady = false;
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "repaste", null, null, 1,
            string.IsNullOrWhiteSpace(note) ? "Thermal paste replaced. New baseline learning started." : $"Thermal paste replaced: {note}",
            null);
        Compute(nowUtc);
    }
}

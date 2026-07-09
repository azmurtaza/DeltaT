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
    private RepasteReport? _pendingRepasteReport;

    /// <summary>Dormancy beyond which the learned baseline is treated as unverified:
    /// the physical setup (dust, fans, an unlogged repaste) may have drifted while
    /// DeltaT wasn't watching, so the score deserves a "recalibrate me" flag.</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(45);

    public event Action<IReadOnlyDictionary<ComponentKind, ComponentScore>>? ScoresUpdated;

    /// <summary>Set when a Compute() pass sees the baseline turn ready for the first time.</summary>
    public bool BaselineJustBecameReady { get; private set; }

    /// <summary>True once any pasted component has a locked baseline.</summary>
    public bool IsBaselineReady => _wasReady;

    /// <summary>The machine was dormant long enough (see <see cref="StaleThreshold"/>)
    /// that the current baseline can no longer be trusted without recalibration.</summary>
    public bool BaselineStale { get; private set; }

    /// <summary>How many days DeltaT was off before this run, when that gap tripped staleness.</summary>
    public int DormantDays { get; private set; }

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
            _settings.Set(SettingsKeys.BaselineEpochReason, "initial");
        }

        BackfillBaselineLock(DateTimeOffset.UtcNow);
        DetectDormancy(DateTimeOffset.UtcNow);
    }

    /// <summary>How long the old fixed-window code learned before locking a baseline.
    /// Used only to reconstruct a lock point for baselines learned before this build.</summary>
    private static readonly TimeSpan LegacyLearningWindow = TimeSpan.FromDays(7);

    /// <summary>One-shot upgrade path: a baseline learned under the old fixed-window
    /// code carries no lock timestamp. Without one, the confidence-based window would
    /// grow to now and relearn over newer (possibly degraded) data, quietly discarding
    /// the reference the user already earned. Freeze it at where the old 7-day window
    /// would have ended so the existing baseline is preserved untouched.</summary>
    private void BackfillBaselineLock(DateTimeOffset now)
    {
        if (_settings.GetTimestamp(SettingsKeys.BaselineLockedUtc) is not null)
            return;
        if (_repo.GetBaseline(Epoch).Count == 0)
            return; // nothing learned yet — the new confidence model drives from scratch

        DateTimeOffset approxLock = EpochStart + LegacyLearningWindow;
        _settings.SetTimestamp(SettingsKeys.BaselineLockedUtc, approxLock < now ? approxLock : now);
    }

    /// <summary>One-shot at startup: if DeltaT sat idle past the stale threshold and a
    /// baseline already exists, flag it for recalibration. Non-destructive — the
    /// baseline stays put until the user acts.</summary>
    private void DetectDormancy(DateTimeOffset now)
    {
        if (_settings.GetTimestamp(SettingsKeys.LastSeenUtc) is not { } lastSeen)
            return;
        int gapDays = (int)Math.Max(0, (now - lastSeen).TotalDays);
        if (gapDays >= StaleThreshold.TotalDays && _repo.GetBaseline(Epoch).Count > 0)
        {
            BaselineStale = true;
            DormantDays = gapDays;
        }
    }

    public int Epoch => _settings.GetInt(SettingsKeys.BaselineEpoch) ?? 0;

    public DateTimeOffset EpochStart => _settings.GetTimestamp(SettingsKeys.BaselineEpochStart) ?? DateTimeOffset.UtcNow;

    private string EpochReason => _settings.Get(SettingsKeys.BaselineEpochReason) ?? "initial";

    /// <summary>Consumed once by the remarks layer to surface a just-computed repaste
    /// verdict through the normal remark → toast pipe. Returns null after the read.</summary>
    public RepasteReport? ConsumeRepasteReport()
    {
        lock (_gate)
        {
            RepasteReport? r = _pendingRepasteReport;
            _pendingRepasteReport = null;
            return r;
        }
    }

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
        // The learning window grows until confidence locks it, then freezes at the lock
        // time so the reference stays a stable comparison point instead of drifting.
        DateTimeOffset? locked = _settings.GetTimestamp(SettingsKeys.BaselineLockedUtc);
        bool anyReady = false;

        foreach (ComponentReading c in snap.Components.Where(c => c.Kind.HasPaste()))
        {
            ComponentScore score = ScoreComponent(c, epochStart, locked, nowUtc, out bool ready);
            results[c.Kind] = score;
            anyReady |= ready;
        }

        BaselineJustBecameReady = anyReady && !_wasReady;
        _wasReady = anyReady;

        // Freeze the learning window the moment the first component's baseline locks.
        if (BaselineJustBecameReady && locked is null)
            _settings.SetTimestamp(SettingsKeys.BaselineLockedUtc, nowUtc);

        if (BaselineJustBecameReady && Epoch > 0)
        {
            if (EpochReason == "recalibrate")
                ReportRecalibrationComplete(nowUtc);
            else
                ReportRepasteOutcome(nowUtc);
        }

        _settings.SetTimestamp(SettingsKeys.LastSeenUtc, nowUtc); // heartbeat for dormancy detection
        lock (_gate) _latest = results;
        ScoresUpdated?.Invoke(results);
        return results;
    }

    /// <summary>The payoff moment: the post-repaste baseline just locked, so DeltaT
    /// can tell the user — fairly — what the fresh paste actually did. Compares the
    /// two epochs' baselines like-for-like (same load bucket + ambient band,
    /// fan-normalized) via <see cref="BaselineComparer"/>, so a repaste that made
    /// things *worse* is called out just as clearly as one that helped. The verdict
    /// is stashed for the remarks layer to surface as a remark (and a toast when the
    /// news is bad).</summary>
    private void ReportRepasteOutcome(DateTimeOffset nowUtc)
    {
        IReadOnlyList<BaselineRow> before = _repo.GetBaseline(Epoch - 1);
        IReadOnlyList<BaselineRow> after = _repo.GetBaseline(Epoch);

        var perComponent = new List<(ComponentKind Kind, BaselineComparison Cmp)>();
        foreach (ComponentKind kind in new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete })
        {
            BaselineComparison cmp = BaselineComparer.Compare(before, after, kind);
            if (cmp.Verdict != RepasteVerdict.Inconclusive)
                perComponent.Add((kind, cmp));
        }

        RepasteReport report = BuildRepasteReport(perComponent);
        lock (_gate) _pendingRepasteReport = report;
    }

    private static RepasteReport BuildRepasteReport(IReadOnlyList<(ComponentKind Kind, BaselineComparison Cmp)> parts)
    {
        if (parts.Count == 0)
            return new RepasteReport(RepasteVerdict.Inconclusive,
                "Repaste logged, but there hasn't been enough comparable load yet to judge it. Run something demanding and DeltaT will report the before/after.");

        bool fanCorrected = parts.Any(p => p.Cmp.FanCorrected);
        string corrNote = fanCorrected ? "weather- and fan-corrected" : "weather-corrected";

        // Overall verdict: any regression wins (it's the news that matters most),
        // then any improvement, otherwise it barely moved.
        if (parts.Any(p => p.Cmp.Verdict == RepasteVerdict.Worse))
        {
            string worse = Join(parts.Where(p => p.Cmp.Verdict == RepasteVerdict.Worse),
                p => $"{p.Kind.Label()} {p.Cmp.WeightedDeltaChangeC:0.#}° hotter");
            return new RepasteReport(RepasteVerdict.Worse,
                $"Repaste verdict: {worse} under load versus the old paste, {corrNote}. That usually means an air bubble, too little or too much paste, or an uneven mount - worth pulling the cooler and redoing it before the numbers settle in as the new normal.");
        }

        if (parts.Any(p => p.Cmp.Verdict == RepasteVerdict.Improved))
        {
            string gains = Join(parts.Where(p => p.Cmp.Verdict == RepasteVerdict.Improved),
                p => $"{p.Kind.Label()} −{Math.Abs(p.Cmp.WeightedDeltaChangeC):0.#}°");
            return new RepasteReport(RepasteVerdict.Improved,
                $"Repaste verdict is in: {gains} under load versus the old paste, {corrNote}. Money well spent.");
        }

        return new RepasteReport(RepasteVerdict.Unchanged,
            $"Repaste verdict: temperatures barely moved versus the old paste ({corrNote}). Either the old paste was still fine, or the mount deserves a second look.");
    }

    private static string Join<T>(IEnumerable<T> items, Func<T, string> fmt) => string.Join(", ", items.Select(fmt));

    /// <summary>A recalibration (not a repaste) just finished relearning. No before/after
    /// claim — the point was to replace a baseline we no longer trusted.</summary>
    private void ReportRecalibrationComplete(DateTimeOffset nowUtc) =>
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "remark", null, null, 1,
            "Recalibration complete - a fresh baseline is locked in and scoring is back on solid ground.", null);

    private ComponentScore ScoreComponent(
        ComponentReading c, DateTimeOffset epochStart, DateTimeOffset? locked, DateTimeOffset nowUtc, out bool ready)
    {
        long epochStartTs = epochStart.ToUnixTimeSeconds();
        long nowTs = nowUtc.ToUnixTimeSeconds();
        // Grow the window until the baseline locks, then freeze it at the lock time.
        long learningEndTs = Math.Min(locked?.ToUnixTimeSeconds() ?? nowTs, nowTs);

        // Baseline pool: learning window, AC power only, known ambient only.
        var baselineStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, epochStartTs, learningEndTs));

        // Readiness is now a confidence judgement, not a timer: how precisely we know
        // this machine's loaded normal (standard error of session means), gated by the
        // paste still curing. Idle-only data never reaches confidence, so a lightly used
        // machine honestly keeps learning instead of locking a hollow baseline.
        CalibrationState cal = BaselineBuilder.Assess(epochStart, nowUtc, baselineStats,
            (bucket, band) => _repo.GetSessionMeanDeltas(
                c.Kind, c.Name, bucket, band, onAc: true, epochStartTs, learningEndTs, BaselineBuilder.SessionGapSeconds));
        ready = cal.Ready;

        double progress = cal.Confidence;
        double? soakBaseline = _repo.GetAverageSoakRate(c.Kind, epochStartTs, learningEndTs);

        // Recent pool. Once the baseline is locked, "recent" is data AFTER the frozen
        // learning window. Before lock there is no post-window data yet, so a provisional
        // read compares the last 7 days against the provisional baseline: the two windows
        // overlap, which only biases the estimate toward "on baseline" (conservative,
        // never alarmist) and lets DeltaT show a number instead of nothing.
        long weekAgo = nowTs - (long)TimeSpan.FromDays(7).TotalSeconds;
        long recentFromTs = locked is not null ? Math.Max(learningEndTs, weekAgo) : weekAgo;
        var recentStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, recentFromTs, nowTs));
        double recentHours = Math.Max(1, (nowTs - recentFromTs) / 3600.0);

        var input = new ScoreInput(
            c.Kind, c.Name,
            Recent: recentStats.Select(s => new RecentBucketObs(
                s.Bucket, s.Band, s.Minutes, s.DeltaAvg, s.TempAvg, s.TempMax, s.FanAvg, s.ThrottleCount)).ToList(),
            // Build the baseline every pass, not just once locked: a provisional
            // baseline lets the engine put an estimated number on the score before
            // lock. Idle-only history yields no rows, so this stays empty until real
            // load appears — exactly when a provisional score would be meaningless.
            Baseline: BuildBaseline(c, epochStartTs, learningEndTs, nowTs, locked is not null, baselineStats, soakBaseline, nowUtc),
            RecentWindowHours: recentHours,
            ThrottleEvents: _repo.CountEvents("throttle", c.Kind, recentFromTs, nowTs),
            SoakRateRecent: _repo.GetAverageSoakRate(c.Kind, recentFromTs, nowTs),
            SoakRateBaseline: soakBaseline,
            LimitC: c.ThrottleLimitC,
            Profile: c.Kind == ComponentKind.Cpu ? _profile.Cpu : _profile.Gpu,
            BaselineReady: ready,
            CalibrationProgress: progress,
            BaselineStale: BaselineStale,
            DormantDays: DormantDays,
            CalibrationConstraint: cal.Constraint,
            CalibrationDataConfidence: cal.DataConfidence);

        return ScoringEngine.Score(input, _fmtTemp);
    }

    private List<BaselineBucket> BuildBaseline(
        ComponentReading c, long fromTs, long lockedToTs, long nowTs, bool locked,
        IReadOnlyList<BucketStat> frozenStats, double? soakAvg, DateTimeOffset nowUtc)
    {
        // Primary reference: learned up to the lock point and then frozen, so the trusted
        // baseline never drifts over newer (possibly degraded) data.
        List<BaselineRow> rows = BaselineBuilder.Build(
            Epoch, c.Kind, c.Name, frozenStats,
            (bucket, band) => _repo.GetMinuteDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, lockedToTs),
            (bucket, band) => _repo.GetSessionMeanDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, lockedToTs, BaselineBuilder.SessionGapSeconds),
            soakAvg, nowUtc);

        // After lock, keep the baseline honest about weather it has since met: learn a
        // reference for any (bucket, band) the frozen window never saw, drawn from the
        // whole epoch. This is the proper cure for cross-band "Aging" — a winter cold
        // snap after a summer baseline now gets its own like-for-like reference instead
        // of being judged against a warmer band. Crucially it never re-touches (and so
        // never relaxes) a band that was already locked: a new band is a fresh reference,
        // not a re-judgement of the trusted ones.
        if (locked && nowTs > lockedToTs)
        {
            var known = rows.Select(r => (r.Bucket, r.Band)).ToHashSet();
            List<BucketStat> newBandStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, fromTs, nowTs))
                .Where(s => !known.Contains((s.Bucket, s.Band))).ToList();
            if (newBandStats.Count > 0)
                rows.AddRange(BaselineBuilder.Build(
                    Epoch, c.Kind, c.Name, newBandStats,
                    (bucket, band) => _repo.GetMinuteDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, nowTs),
                    (bucket, band) => _repo.GetSessionMeanDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, nowTs, BaselineBuilder.SessionGapSeconds),
                    soakAvg, nowUtc));
        }

        _repo.UpsertBaseline(rows);
        return rows.Select(r => new BaselineBucket(r.Bucket, r.Band, r.DeltaAvg, r.DeltaP95, r.FanAvg, r.Minutes, r.TempAvg)).ToList();
    }

    /// <summary>Paste scoring only trusts samples on AC power with known ambient —
    /// battery power limits and unknown weather both make deltas incomparable.</summary>
    private static List<BucketStat> FilterForScoring(IReadOnlyList<BucketStat> stats) =>
        stats.Where(s => s.OnAc && s.Band >= 0).ToList();

    /// <summary>User repasted: bump the epoch, restart learning, log the event.
    /// A few days later the before/after comparison lives in the events + baselines.</summary>
    public void RegisterRepaste(DateTimeOffset nowUtc, string? note = null)
    {
        StartNewEpoch(nowUtc, "repaste");
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "repaste", null, null, 1,
            string.IsNullOrWhiteSpace(note) ? "Thermal paste replaced. New baseline learning started." : $"Thermal paste replaced: {note}",
            null);
        Compute(nowUtc);
    }

    /// <summary>User accepted DeltaT's "your baseline is stale" prompt: relearn from
    /// scratch without claiming a repaste happened. Unlike <see cref="RegisterRepaste"/>
    /// there's no before/after verdict — the old baseline was the thing we distrusted.</summary>
    public void Recalibrate(DateTimeOffset nowUtc, string? note = null)
    {
        StartNewEpoch(nowUtc, "recalibrate");
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "recalibrate", null, null, 1,
            string.IsNullOrWhiteSpace(note) ? "Baseline recalibration started - scoring pauses for about a week while DeltaT relearns what normal looks like now." : $"Baseline recalibration started: {note}",
            null);
        Compute(nowUtc);
    }

    private void StartNewEpoch(DateTimeOffset nowUtc, string reason)
    {
        _settings.SetInt(SettingsKeys.BaselineEpoch, Epoch + 1);
        _settings.SetTimestamp(SettingsKeys.BaselineEpochStart, nowUtc);
        _settings.Set(SettingsKeys.BaselineEpochReason, reason);
        _settings.Set(SettingsKeys.BaselineLockedUtc, ""); // fresh epoch: window grows again until it re-locks
        _wasReady = false;
        BaselineStale = false; // a fresh learning window supersedes the stale one
        DormantDays = 0;
    }
}

/// <summary>A just-computed repaste verdict, handed to the remarks layer to surface
/// once through the normal remark → toast pipe.</summary>
public sealed record RepasteReport(RepasteVerdict Verdict, string Text);

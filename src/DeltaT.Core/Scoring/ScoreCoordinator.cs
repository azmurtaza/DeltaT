using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

/// <summary>Assembles ScoreInputs from the database and runs the engine for every
/// pasted component (CPU + discrete GPU). Owns the epoch lifecycle: first run
/// starts epoch 0; a repaste starts the next epoch with a fresh learning window.
///
/// Locks are per component: CPU and GPU earn confidence at their own pace, so a
/// machine that games (GPU locks fast) but rarely loads the CPU doesn't freeze the
/// CPU's still-thin learning window the moment the GPU is ready. Each lock carries
/// an "earned" marker so a lock written by anything other than a genuine confidence
/// pass can be recognised and healed later.</summary>
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
    private readonly HashSet<ComponentKind> _unfreezeLogged = new();

    /// <summary>Dormancy beyond which the learned baseline is treated as unverified:
    /// the physical setup (dust, fans, an unlogged repaste) may have drifted while
    /// DeltaT wasn't watching, so the score deserves a "recalibrate me" flag.</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(45);

    /// <summary>How long after a lock its learning window can still be faithfully
    /// re-assessed. Minute aggregates are pruned at 90 days; past ~80 the window's
    /// data is (partially) gone and a re-assessment would be judging thin air.</summary>
    private static readonly TimeSpan LockReassessableFor = TimeSpan.FromDays(80);

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

        // A verdict may only be announced once per epoch. Installs that locked under
        // builds without the marker have already heard theirs (or never will get a
        // faithful one) — grandfather them silently instead of re-toasting on update.
        if (AnyLockExists() && _settings.GetInt(SettingsKeys.BaselineOutcomeReportedEpoch) is null)
            _settings.SetInt(SettingsKeys.BaselineOutcomeReportedEpoch, Epoch);

        DetectDormancy(DateTimeOffset.UtcNow);
    }

    /// <summary>How long the old fixed-window code learned before locking a baseline.
    /// Used only to reconstruct a lock point for baselines learned before this build.</summary>
    private static readonly TimeSpan LegacyLearningWindow = TimeSpan.FromDays(7);

    /// <summary>One-shot upgrade path for baselines learned under the old fixed-window
    /// code, which carried no lock timestamp. Only rows written by those builds count:
    /// they predate the temp_avg column, so a stored baseline where ANY row carries the
    /// absolute-temperature anchor was written by the confidence model and must NOT
    /// freeze the still-growing window. (Treating the confidence model's own provisional
    /// rows as a legacy lock was the "calibration stuck at 0%" bug: any app restart in
    /// the first days froze the learning window at whatever idle data existed, and the
    /// meter could never move again.)</summary>
    private void BackfillBaselineLock(DateTimeOffset now)
    {
        if (_settings.GetTimestamp(SettingsKeys.BaselineLockedUtc) is not null)
            return;
        IReadOnlyList<BaselineRow> rows = _repo.GetBaseline(Epoch);
        if (rows.Count == 0 || rows.Any(r => r.TempAvg is not null))
            return; // nothing learned yet, or provisional rows from the confidence model

        DateTimeOffset approxLock = EpochStart + LegacyLearningWindow;
        _settings.SetTimestamp(SettingsKeys.BaselineLockedUtc, approxLock < now ? approxLock : now);
    }

    /// <summary>One-shot at startup: if DeltaT sat idle past the stale threshold and a
    /// locked baseline exists, flag it for recalibration. Non-destructive — the
    /// baseline stays put until the user acts.</summary>
    private void DetectDormancy(DateTimeOffset now)
    {
        if (_settings.GetTimestamp(SettingsKeys.LastSeenUtc) is not { } lastSeen)
            return;
        int gapDays = (int)Math.Max(0, (now - lastSeen).TotalDays);
        if (gapDays >= StaleThreshold.TotalDays && AnyLockExists())
        {
            BaselineStale = true;
            DormantDays = gapDays;
        }
    }

    public int Epoch => _settings.GetInt(SettingsKeys.BaselineEpoch) ?? 0;

    public DateTimeOffset EpochStart => _settings.GetTimestamp(SettingsKeys.BaselineEpochStart) ?? DateTimeOffset.UtcNow;

    private string EpochReason => _settings.Get(SettingsKeys.BaselineEpochReason) ?? "initial";

    // ------------------------------------------------------------- lock plumbing

    private static ComponentKind[] PastedKinds { get; } = { ComponentKind.Cpu, ComponentKind.GpuDiscrete };

    private static string LockKey(ComponentKind kind) => $"{SettingsKeys.BaselineLockedUtc}.{kind}";

    private static string EarnedKey(ComponentKind kind) => $"{SettingsKeys.BaselineLockEarned}.{kind}";

    /// <summary>This component's lock, falling back to the pre-per-component global key.</summary>
    private DateTimeOffset? LockFor(ComponentKind kind) =>
        _settings.GetTimestamp(LockKey(kind)) ?? _settings.GetTimestamp(SettingsKeys.BaselineLockedUtc);

    private bool LockEarned(ComponentKind kind) => _settings.GetBool(EarnedKey(kind), false);

    private void SetLock(ComponentKind kind, DateTimeOffset ts, bool earned)
    {
        _settings.SetTimestamp(LockKey(kind), ts);
        _settings.SetBool(EarnedKey(kind), earned);
    }

    /// <summary>Clears one component's lock. If the lock came from the shared legacy
    /// key, the other components inherit it as their own per-component lock first, so
    /// healing one component never silently unlocks another.</summary>
    private void ClearLock(ComponentKind kind)
    {
        if (_settings.GetTimestamp(SettingsKeys.BaselineLockedUtc) is { } global)
        {
            foreach (ComponentKind other in PastedKinds)
            {
                if (other != kind && _settings.GetTimestamp(LockKey(other)) is null)
                    _settings.SetTimestamp(LockKey(other), global);
            }
            _settings.Set(SettingsKeys.BaselineLockedUtc, "");
        }
        _settings.Set(LockKey(kind), "");
        _settings.Set(EarnedKey(kind), "");
    }

    private bool AnyLockExists() =>
        _settings.GetTimestamp(SettingsKeys.BaselineLockedUtc) is not null
        || PastedKinds.Any(k => _settings.GetTimestamp(LockKey(k)) is not null);

    // ---------------------------------------------------------------- scoring

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
        bool anyReady = false;

        foreach (ComponentReading c in snap.Components.Where(c => c.Kind.HasPaste()))
        {
            ComponentScore score = ScoreComponent(c, epochStart, nowUtc, out bool ready);
            results[c.Kind] = score;
            anyReady |= ready;
        }

        BaselineJustBecameReady = anyReady && !_wasReady;
        _wasReady = anyReady;

        // Announce the epoch's outcome exactly once, ever — not once per launch.
        // (_wasReady resets on every restart, so without the persisted marker a
        // locked machine re-toasted its repaste verdict at every login.)
        if (BaselineJustBecameReady && Epoch > 0
            && _settings.GetInt(SettingsKeys.BaselineOutcomeReportedEpoch) != Epoch)
        {
            _settings.SetInt(SettingsKeys.BaselineOutcomeReportedEpoch, Epoch);
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

    /// <summary>Long-term drift/step analysis for one component against its frozen baseline.
    /// Only meaningful once locked (there is no stable reference before then) and only over
    /// data AFTER the lock (the learning weeks sit on baseline by construction and would
    /// flatten a real slope). Reads hour rollups, which are never pruned — so this can see
    /// months, even seasons, of history. Returns <see cref="TrendResult.None"/> when there
    /// isn't a locked baseline or enough post-lock weeks yet.</summary>
    public TrendResult ComputeTrend(ComponentKind kind, DateTimeOffset nowUtc)
    {
        if (!kind.HasPaste() || LockFor(kind) is not { } locked)
            return TrendResult.None;
        List<BaselineRow> baseline = _repo.GetBaseline(Epoch).Where(r => r.Kind == kind).ToList();
        if (baseline.Count == 0)
            return TrendResult.None;
        string name = baseline[0].Name;
        IReadOnlyList<WeeklyLoadedCell> cells =
            _repo.GetWeeklyLoadedCells(kind, name, locked.ToUnixTimeSeconds(), nowUtc.ToUnixTimeSeconds());
        return TrendAnalyzer.Analyze(TrendAnalyzer.BuildWeekly(cells, baseline));
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
        foreach (ComponentKind kind in PastedKinds)
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
                $"Repaste verdict: {worse} under load versus the old paste, {corrNote}. That usually means an air bubble, too little or too much paste, or an uneven mount. Worth pulling the cooler and redoing it before the numbers settle in as the new normal.");
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

    /// <summary>A recalibration (not a repaste) finished relearning from scratch — the
    /// new data did NOT match the old baseline (else it would have been adopted), so
    /// tell the user what actually moved when the comparison is conclusive. That
    /// difference is the answer they recalibrated to get.</summary>
    private void ReportRecalibrationComplete(DateTimeOffset nowUtc)
    {
        var changes = new List<string>();
        IReadOnlyList<BaselineRow> before = _repo.GetBaseline(Epoch - 1);
        IReadOnlyList<BaselineRow> after = _repo.GetBaseline(Epoch);
        foreach (ComponentKind kind in PastedKinds)
        {
            BaselineComparison cmp = BaselineComparer.Compare(before, after, kind);
            if (cmp.Verdict == RepasteVerdict.Improved)
                changes.Add($"{kind.Label()} now runs {Math.Abs(cmp.WeightedDeltaChangeC):0.#}° cooler than the retired reference");
            else if (cmp.Verdict == RepasteVerdict.Worse)
                changes.Add($"{kind.Label()} now runs {cmp.WeightedDeltaChangeC:0.#}° hotter than the retired reference");
        }
        string comparison = changes.Count > 0
            ? $" Versus the old baseline (weather- and fan-corrected): {string.Join(", ", changes)}."
            : "";
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "remark", null, null, 1,
            $"Recalibration complete. A fresh baseline is locked in and scoring is back on solid ground.{comparison}", null);
    }

    private ComponentScore ScoreComponent(
        ComponentReading c, DateTimeOffset epochStart, DateTimeOffset nowUtc, out bool ready)
    {
        DateTimeOffset? locked = LockFor(c.Kind);
        CalibrationState cal = AssessWindow(c, epochStart, locked, nowUtc, out var baselineStats, out long learningEndTs);

        // Self-heal: a lock whose own learning window doesn't assess ready was never
        // earned by a confidence pass — it's a freeze left behind by the old backfill
        // bug (or an interrupted upgrade), and it would pin calibration at 0% forever.
        // Only heal what can be judged fairly: an earned lock is trusted outright, and a
        // lock too old to still have its learning minutes (pruned at 90 days) is only
        // healed when its stored baseline never learned a loaded cell — a reference that
        // can't score paste anyway.
        if (locked is not null && !cal.Ready && !LockEarned(c.Kind) && ShouldUnfreeze(c, locked.Value, nowUtc))
        {
            ClearLock(c.Kind);
            _repo.DeleteBaseline(Epoch, c.Kind, c.Name);
            if (_unfreezeLogged.Add(c.Kind))
            {
                _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "system", c.Kind.ToString(), c.Name, 1,
                    $"Calibration self-heal: {c.Kind.Label()} baseline learning had been frozen prematurely by an earlier version. Learning resumed with all of this epoch's data.", null);
            }
            locked = null;
            cal = AssessWindow(c, epochStart, null, nowUtc, out baselineStats, out learningEndTs);
        }

        // A pre-marker legit lock that still assesses ready earns its marker now, so it
        // stays trusted even after its learning minutes age out of retention.
        if (locked is not null && cal.Ready && !LockEarned(c.Kind))
            _settings.SetBool(EarnedKey(c.Kind), true);

        // The moment confidence first crosses the bar, freeze this component's learning
        // window so the trusted reference never drifts over newer (possibly degraded) data.
        bool justLocked = false;
        if (locked is null && cal.Ready)
        {
            SetLock(c.Kind, nowUtc, earned: true);
            locked = nowUtc;
            justLocked = true;
        }
        // Smart recalibration: relearning is verification, not amnesia. As soon as the
        // new epoch has enough comparable loaded data, it is compared like-for-like
        // (same bucket + ambient band, fan-normalized) against the retired baseline —
        // however old that is. If the machine behaves exactly as it used to, the old
        // reference still describes it: adopt it wholesale and lock now, instead of
        // making the user re-earn a baseline nothing invalidated. If behavior moved,
        // learning continues — that difference is what the recalibrate was for.
        else if (locked is null && Epoch > 0 && EpochReason == "recalibrate"
                 && TryAdoptPreviousBaseline(c, cal, nowUtc))
        {
            locked = nowUtc;
        }

        // A surviving lock IS readiness: the lock is the durable record that confidence
        // was earned. Re-deriving readiness from the window every pass would flip a
        // years-old locked machine back to "calibrating" the day its learning minutes
        // aged out of retention.
        ready = locked is not null;
        long epochStartTs = epochStart.ToUnixTimeSeconds();
        long nowTs = nowUtc.ToUnixTimeSeconds();

        // The meter shows the smoothed, evidence-driven progress — not raw Confidence,
        // which is gated and steps (sits at 0, then teleports). Readiness/lock above still
        // keys off cal.Ready, so this only changes what the user watches, never when it locks.
        // Ratcheted per component+epoch: the underlying confidence legitimately dips when a
        // new session reveals variance (that's the statistics working), but a meter falling
        // 76% → 58% reads as the app losing its homework. Display never regresses; the lock
        // still waits for real confidence.
        double progress = RatchetMeter(c.Kind, cal.DisplayProgress);
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

        bool scoreShownBefore = _settings.GetInt(ScoreShownKey(c.Kind)) == Epoch;

        var input = new ScoreInput(
            c.Kind, c.Name,
            Recent: recentStats.Select(s => new RecentBucketObs(
                s.Bucket, s.Band, s.Minutes, s.DeltaAvg, s.TempAvg, s.TempMax, s.FanAvg, s.ThrottleCount, s.GapAvg, s.PowerAvg)).ToList(),
            Baseline: BuildBaseline(c, epochStartTs, learningEndTs, nowTs, locked is not null && !justLocked, baselineStats, soakBaseline, nowUtc),
            RecentWindowHours: recentHours,
            ThrottleEvents: _repo.CountEvents("throttle", c.Kind, recentFromTs, nowTs),
            SoakRateRecent: _repo.GetAverageSoakRate(c.Kind, recentFromTs, nowTs),
            SoakRateBaseline: soakBaseline,
            CooldownRateRecent: _repo.GetAverageCooldownRate(c.Kind, recentFromTs, nowTs),
            CooldownRateBaseline: _repo.GetAverageCooldownRate(c.Kind, epochStartTs, learningEndTs),
            LimitC: c.ThrottleLimitC,
            Profile: c.Kind == ComponentKind.Cpu ? _profile.Cpu : _profile.Gpu,
            BaselineReady: ready,
            CalibrationProgress: progress,
            BaselineStale: BaselineStale,
            DormantDays: DormantDays,
            CalibrationConstraint: cal.Constraint,
            CalibrationDataConfidence: cal.DataConfidence,
            ProvisionalEverShown: scoreShownBefore,
            ConcernOverrideC: ConcernOverride(c.Kind),
            HeadroomWarnings: _settings.GetBool(SettingsKeys.HeadroomWarnings, true));

        ComponentScore score = ScoringEngine.Score(input, _fmtTemp);
        // Once a provisional number has been shown, remember it: the confidence floor
        // is an entry gate, not a hold requirement (see ScoringEngine), so the score
        // keeps updating live instead of vanishing when a noisy session dips confidence.
        if (score.Provisional && !scoreShownBefore)
            _settings.SetInt(ScoreShownKey(c.Kind), Epoch);
        return score;
    }

    /// <summary>User's sustained-temperature concern override for a component, if set.</summary>
    private double? ConcernOverride(ComponentKind kind) => kind == ComponentKind.Cpu
        ? _settings.GetDouble(SettingsKeys.ConcernOverrideCpuC)
        : _settings.GetDouble(SettingsKeys.ConcernOverrideGpuC);

    private static string ScoreShownKey(ComponentKind kind) => $"{SettingsKeys.BaselineScoreShown}.{kind}";

    /// <summary>Displayed calibration progress never regresses within an epoch: returns
    /// the high-water mark of what this component's meter has already shown, persisting
    /// new peaks. A new epoch (repaste/recalibrate) starts the ratchet fresh.</summary>
    private double RatchetMeter(ComponentKind kind, double current)
    {
        string key = $"{SettingsKeys.BaselineMeterPeak}.{kind}";
        double peak = 0;
        if (_settings.Get(key) is { } stored)
        {
            int sep = stored.IndexOf(':');
            if (sep > 0
                && int.TryParse(stored[..sep], out int epoch) && epoch == Epoch
                && double.TryParse(stored[(sep + 1)..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double p))
                peak = p;
        }
        if (current > peak + 0.0005)
        {
            _settings.Set(key, $"{Epoch}:{current.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            return current;
        }
        return Math.Max(current, peak);
    }

    /// <summary>Readiness is a confidence judgement, not a timer: how precisely we know
    /// this machine's loaded normal (standard error of independent session means), gated
    /// by paste cure only when the epoch began with an actual repaste. Idle-only data
    /// never reaches confidence, so a lightly used machine honestly keeps learning
    /// instead of locking a hollow baseline.</summary>
    private CalibrationState AssessWindow(
        ComponentReading c, DateTimeOffset epochStart, DateTimeOffset? locked, DateTimeOffset nowUtc,
        out List<BucketStat> baselineStats, out long learningEndTs)
    {
        long epochStartTs = epochStart.ToUnixTimeSeconds();
        long nowTs = nowUtc.ToUnixTimeSeconds();
        // Grow the window until the baseline locks, then freeze it at the lock time.
        learningEndTs = Math.Min(locked?.ToUnixTimeSeconds() ?? nowTs, nowTs);
        long end = learningEndTs;

        // Baseline pool: learning window, AC power only, known ambient only.
        baselineStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, epochStartTs, end));

        int loadedSessions = _repo.CountLoadedSessions(
            c.Kind, c.Name, onAc: true, epochStartTs, end, BaselineBuilder.SessionGapSeconds);

        return BaselineBuilder.Assess(epochStart, nowUtc, baselineStats,
            (bucket, band) => _repo.GetSessionMeanDeltas(
                c.Kind, c.Name, bucket, band, onAc: true, epochStartTs, end, BaselineBuilder.SessionGapSeconds),
            loadedSessions,
            pasteIsFresh: EpochReason == "repaste");
    }

    /// <summary>Compare the recalibrating epoch's provisional cells against the previous
    /// epoch's stored baseline and, when they are statistically the same machine, carry
    /// the old reference over (all its buckets and bands) and lock. Guarded three ways:
    /// the comparer needs ≥30 matched loaded minutes, the weighted change must sit
    /// within the practical noise floor (not merely within a thin sample's wide error
    /// bars), and at least two independent loaded bouts must back the new data.</summary>
    private bool TryAdoptPreviousBaseline(ComponentReading c, CalibrationState cal, DateTimeOffset nowUtc)
    {
        if (cal.LoadedSessions < 2)
            return false;
        IReadOnlyList<BaselineRow> previous = _repo.GetBaseline(Epoch - 1);
        if (previous.Count == 0)
            return false;
        List<BaselineRow> current = _repo.GetBaseline(Epoch)
            .Where(r => r.Kind == c.Kind && r.Name == c.Name).ToList(); // provisional rows from earlier passes
        if (current.Count == 0)
            return false;

        BaselineComparison cmp = BaselineComparer.Compare(previous, current, c.Kind);
        if (cmp.Verdict != RepasteVerdict.Unchanged
            || Math.Abs(cmp.WeightedDeltaChangeC) > BaselineComparer.SignificantChangeC)
            return false;

        long nowTs = nowUtc.ToUnixTimeSeconds();
        _repo.UpsertBaseline(previous
            .Where(r => r.Kind == c.Kind)
            .Select(r => r with { Epoch = Epoch, Name = c.Name, Updated = nowTs })
            .ToList());
        SetLock(c.Kind, nowUtc, earned: true);
        // The adoption IS this epoch's outcome — don't also announce "recalibration
        // complete" as if a from-scratch relearn had finished.
        _settings.SetInt(SettingsKeys.BaselineOutcomeReportedEpoch, Epoch);
        _repo.InsertEvent(nowTs, "remark", c.Kind.ToString(), c.Name, 1,
            $"Recalibration check: {c.Kind.Label()} behaves exactly like its old baseline at matching load and weather (fan-corrected, within {cmp.WeightedDeltaChangeC:+0.#;-0.#}°). The old reference carries over, so there's no point relearning what hasn't changed.", null);
        return true;
    }

    private bool ShouldUnfreeze(ComponentReading c, DateTimeOffset locked, DateTimeOffset nowUtc)
    {
        if (nowUtc - locked < LockReassessableFor)
            return true; // the window's minutes still exist, and they say "not ready"
        // Too old to re-judge fairly: heal only a reference that never learned any
        // loaded cell — it can't score paste, so there is nothing to lose.
        return !_repo.GetBaseline(Epoch).Any(r =>
            r.Kind == c.Kind && r.Name == c.Name && r.Bucket is LoadBucket.Heavy or LoadBucket.Medium or LoadBucket.Max);
    }

    private List<BaselineBucket> BuildBaseline(
        ComponentReading c, long fromTs, long lockedToTs, long nowTs, bool locked,
        IReadOnlyList<BucketStat> windowStats, double? soakAvg, DateTimeOffset nowUtc)
    {
        List<BaselineRow> rows;
        if (!locked)
        {
            // Still learning (or locking on this very pass): rebuild the reference from
            // the learning window and persist it — at the lock transition this is the
            // write that makes the stored rows the durable reference from here on.
            rows = BaselineBuilder.Build(
                Epoch, c.Kind, c.Name, windowStats,
                (bucket, band) => _repo.GetMinuteDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, lockedToTs),
                (bucket, band) => _repo.GetSessionMeanDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, lockedToTs, BaselineBuilder.SessionGapSeconds),
                soakAvg, nowUtc);
            _repo.UpsertBaseline(rows);
        }
        else
        {
            // Locked: the STORED rows are the reference. Rebuilding from raw aggregates
            // every pass silently evaporated the baseline once the learning window's
            // minutes aged past the 90-day retention — the score kept its verdict but
            // lost every cell it was judging against.
            rows = _repo.GetBaseline(Epoch).Where(r => r.Kind == c.Kind && r.Name == c.Name).ToList();

            // Stored rows missing under a live lock (seeded store, lost table): repair
            // them from the frozen learning window while its minutes still exist. The
            // frozen window — not the whole epoch — so a degraded recent stretch can't
            // seep into the healthy reference.
            if (rows.Count == 0 && windowStats.Count > 0)
            {
                rows = BaselineBuilder.Build(
                    Epoch, c.Kind, c.Name, windowStats,
                    (bucket, band) => _repo.GetMinuteDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, lockedToTs),
                    (bucket, band) => _repo.GetSessionMeanDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, lockedToTs, BaselineBuilder.SessionGapSeconds),
                    soakAvg, nowUtc);
                _repo.UpsertBaseline(rows);
            }

            // Keep the baseline honest about weather it meets after the lock: learn a
            // reference for any (bucket, band) the frozen window never saw, drawn from
            // the whole epoch. This is the proper cure for cross-band "Aging" — a winter
            // cold snap after a summer baseline gets its own like-for-like reference
            // instead of being judged against a warmer band. It never re-touches (and so
            // never relaxes) a cell that is already locked.
            if (nowTs > lockedToTs)
            {
                var known = rows.Select(r => (r.Bucket, r.Band)).ToHashSet();
                List<BucketStat> newBandStats = FilterForScoring(_repo.GetBucketStats(c.Kind, c.Name, fromTs, nowTs))
                    .Where(s => !known.Contains((s.Bucket, s.Band))).ToList();
                if (newBandStats.Count > 0)
                {
                    List<BaselineRow> newRows = BaselineBuilder.Build(
                        Epoch, c.Kind, c.Name, newBandStats,
                        (bucket, band) => _repo.GetMinuteDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, nowTs),
                        (bucket, band) => _repo.GetSessionMeanDeltas(c.Kind, c.Name, bucket, band, onAc: true, fromTs, nowTs, BaselineBuilder.SessionGapSeconds),
                        soakAvg, nowUtc);
                    _repo.UpsertBaseline(newRows);
                    rows.AddRange(newRows);
                }
            }
        }
        return rows.Select(r => new BaselineBucket(r.Bucket, r.Band, r.DeltaAvg, r.DeltaP95, r.FanAvg, r.Minutes, r.TempAvg, r.GapAvg, r.PowerAvg)).ToList();
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

    /// <summary>User accepted DeltaT's "your baseline is stale" prompt (or hit the
    /// button out of doubt): start a verification epoch. History and trends are never
    /// touched. The new data is checked against the old baseline as it arrives — if
    /// the machine still behaves identically, the old reference is adopted back
    /// (see <see cref="TryAdoptPreviousBaseline"/>); only genuine change relearns.</summary>
    public void Recalibrate(DateTimeOffset nowUtc, string? note = null)
    {
        StartNewEpoch(nowUtc, "recalibrate");
        _repo.InsertEvent(nowUtc.ToUnixTimeSeconds(), "recalibrate", null, null, 1,
            string.IsNullOrWhiteSpace(note)
                ? "Baseline recalibration started. DeltaT will verify the machine against its old baseline under real load, keep it if nothing changed, and relearn only what did. History and trends stay put."
                : $"Baseline recalibration started: {note}",
            null);
        Compute(nowUtc);
    }

    private void StartNewEpoch(DateTimeOffset nowUtc, string reason)
    {
        _settings.SetInt(SettingsKeys.BaselineEpoch, Epoch + 1);
        _settings.SetTimestamp(SettingsKeys.BaselineEpochStart, nowUtc);
        _settings.Set(SettingsKeys.BaselineEpochReason, reason);
        _settings.Set(SettingsKeys.BaselineLockedUtc, ""); // fresh epoch: windows grow again until they re-lock
        foreach (ComponentKind kind in PastedKinds)
        {
            _settings.Set(LockKey(kind), "");
            _settings.Set(EarnedKey(kind), "");
        }
        _wasReady = false;
        BaselineStale = false; // a fresh learning window supersedes the stale one
        DormantDays = 0;
    }
}

/// <summary>A just-computed repaste verdict, handed to the remarks layer to surface
/// once through the normal remark → toast pipe.</summary>
public sealed record RepasteReport(RepasteVerdict Verdict, string Text);

using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>End-to-end coordinator tests against a real SQLite store — the layer where
/// the "calibration stuck at 0%" bug lived. The scenarios mirror what real installs hit:
/// provisional rows + an app restart, a frozen learning window, repeated launches after
/// a repaste, and components that earn confidence at different speeds.</summary>
public class ScoreCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string CpuName = "CPU";
    private const string GpuName = "GPU";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"deltat-coord-{Guid.NewGuid():N}.db");
    private readonly DeltaTDb _db;
    private readonly TelemetryRepository _repo;
    private readonly SettingsStore _settings;
    private SensorSnapshot? _snapshot;

    private static readonly ThermalProfile Profile = new("test", "Test", "laptop", 0, null, null, null, null);

    public ScoreCoordinatorTests()
    {
        _db = new DeltaTDb(_dbPath);
        _repo = new TelemetryRepository(_db);
        _settings = new SettingsStore(_db);
    }

    private ScoreCoordinator NewCoordinator() =>
        new(_repo, _settings, Profile, () => _snapshot);

    private void UseSnapshot(DateTimeOffset now, bool withGpu = false)
    {
        var components = new List<ComponentReading>
        {
            new(ComponentKind.Cpu, CpuName, 80, null, 50, null, null, null, false, 100),
        };
        if (withGpu)
            components.Add(new ComponentReading(ComponentKind.GpuDiscrete, GpuName, 70, null, 40, 3000, null, null, false, 87));
        _snapshot = new SensorSnapshot(now, true, components);
    }

    private void StartEpoch(int epoch, DateTimeOffset start, string reason = "initial")
    {
        _settings.SetInt(SettingsKeys.BaselineEpoch, epoch);
        _settings.SetTimestamp(SettingsKeys.BaselineEpochStart, start);
        _settings.Set(SettingsKeys.BaselineEpochReason, reason);
    }

    /// <summary>One contiguous loaded (or idle) bout of telemetry minutes.</summary>
    private void WriteSession(ComponentKind kind, string name, DateTimeOffset start, int minutes, double delta,
        LoadBucket bucket = LoadBucket.Heavy, int band = 2, int mode = 0)
    {
        var accs = new List<MinuteAccum>();
        for (int i = 0; i < minutes; i++)
        {
            accs.Add(new MinuteAccum
            {
                Minute = start.AddMinutes(i).ToUnixTimeSeconds() / 60 * 60,
                Kind = kind, Name = name, Bucket = bucket, Band = band, OnAc = true, Mode = mode,
                N = 30,
                TempSum = (delta + 25) * 30, TempMin = delta + 24, TempMax = delta + 26,
                LoadSum = (bucket == LoadBucket.Heavy ? 85 : 5) * 30,
                DeltaSum = delta * 30, DeltaN = 30,
            });
        }
        _repo.UpsertMinutes(accs);
    }

    /// <summary>Four tight, well-separated loaded sessions — enough to earn a lock.</summary>
    private void WriteLockworthyLoad(ComponentKind kind, string name, DateTimeOffset firstSession, double baseDelta = 60.0, int mode = 0)
    {
        double[] deltas = { baseDelta, baseDelta + 0.1, baseDelta - 0.1, baseDelta + 0.05 };
        for (int s = 0; s < deltas.Length; s++)
            WriteSession(kind, name, firstSession.AddHours(s * 5), minutes: 15, delta: deltas[s], mode: mode);
    }

    // ------------------------------------------------------------ the 0% bug

    [Fact]
    public void ProvisionalRows_DoNotBackfillALock_OnRestart()
    {
        // A fresh install runs a few hours (provisional rows get persisted, with the
        // temp_avg anchor), then the app restarts. The old code mistook those rows for
        // a legacy locked baseline and froze the learning window at "now" — with zero
        // loaded sessions inside it, calibration read 0% forever.
        StartEpoch(0, T0);
        _repo.UpsertBaseline(new[]
        {
            new BaselineRow(0, ComponentKind.Cpu, CpuName, 2, LoadBucket.Idle, 19, 21, null, null, 120,
                T0.AddHours(5).ToUnixTimeSeconds(), null, TempAvg: 45),
        });

        _ = NewCoordinator(); // the "restart"

        Assert.Null(_settings.GetTimestamp(SettingsKeys.BaselineLockedUtc));
        Assert.Null(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));
    }

    [Fact]
    public void LegacyRows_WithoutTempAnchor_StillBackfillALock()
    {
        // Rows written by pre-confidence builds carry no temp_avg; those genuinely were
        // locked references and keep their frozen window at epochStart + 7 days.
        StartEpoch(0, T0.AddDays(-20));
        _repo.UpsertBaseline(new[]
        {
            new BaselineRow(0, ComponentKind.Cpu, CpuName, 2, LoadBucket.Heavy, 60, 63, null, null, 300,
                T0.AddDays(-13).ToUnixTimeSeconds(), null, TempAvg: null),
        });

        _ = NewCoordinator();

        DateTimeOffset? locked = _settings.GetTimestamp(SettingsKeys.BaselineLockedUtc);
        Assert.NotNull(locked);
        Assert.Equal(T0.AddDays(-20) + TimeSpan.FromDays(7), locked!.Value);
    }

    [Fact]
    public void FrozenIdleLock_SelfHeals_AndRelocksOnRealData()
    {
        // The exact stuck state seen in the wild: a lock stamped by the old backfill
        // bug, whose frozen window holds only idle minutes — 0% confident, forever.
        DateTimeOffset now = T0.AddDays(6);
        StartEpoch(0, T0);
        WriteSession(ComponentKind.Cpu, CpuName, T0.AddHours(1), 120, 20, LoadBucket.Idle);
        _settings.SetTimestamp(SettingsKeys.BaselineLockedUtc, T0.AddHours(4)); // bogus freeze
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddDays(1)); // real usage, after the freeze

        UseSnapshot(now);
        var coordinator = NewCoordinator();
        var scores = coordinator.Compute(now);

        // The bogus lock is healed in the same pass, the grown window sees the real
        // load, and the baseline locks properly — earned this time.
        Assert.False(scores[ComponentKind.Cpu].Calibrating);
        Assert.True(_settings.GetBool($"{SettingsKeys.BaselineLockEarned}.{ComponentKind.Cpu}", false));
        Assert.Contains(_repo.GetBaseline(0), r => r.Bucket == LoadBucket.Heavy);
    }

    [Fact]
    public void EarnedLock_IsNeverHealedAway()
    {
        DateTimeOffset now = T0.AddDays(6);
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2));
        UseSnapshot(now);

        var coordinator = NewCoordinator();
        coordinator.Compute(now);
        DateTimeOffset? locked = _settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}");
        Assert.NotNull(locked);

        // Later passes (and restarts) leave an earned lock exactly where it was.
        var restarted = NewCoordinator();
        restarted.Compute(now.AddDays(3));
        Assert.Equal(locked, _settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));
    }

    [Fact]
    public void FreshLock_KeepsScoring_InsteadOfCollapsingToAwaitingData()
    {
        // The field bug: a CPU showing a provisional score with a real "peaks within 2 °C
        // of the silicon limit" finding (headroom penalty) dropped to WAITING — with a
        // falsely-reassuring 100 headroom — the instant its baseline locked, because the
        // post-lock recent window was floored at the lock instant and so started empty.
        DateTimeOffset now = T0.AddDays(2);
        StartEpoch(0, T0);
        // Four lockworthy loaded bouts whose peaks (TempMax = delta + 26) land at the
        // 100 °C limit set in UseSnapshot, so the near-limit headroom evidence is present.
        double[] deltas = { 74.0, 74.1, 73.9, 74.05 };
        for (int s = 0; s < deltas.Length; s++)
            WriteSession(ComponentKind.Cpu, CpuName, T0.AddHours(2 + s * 5), minutes: 15, delta: deltas[s]);

        UseSnapshot(now);
        var coordinator = NewCoordinator();
        var scores = coordinator.Compute(now);

        // The baseline locked on this very pass...
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));
        ComponentScore cpu = scores[ComponentKind.Cpu];
        // ...and the score is a real reading, not the WAITING placeholder that hid the fault.
        Assert.False(cpu.AwaitingData);
        Assert.True(cpu.Scored);
        // The near-limit evidence that drove the low headroom survives the lock transition.
        Assert.Contains(cpu.Reasons, r => r.Code == "headroom" && r.PointsLost > 0);
    }

    // ------------------------------------------------------- verdict lifecycle

    [Fact]
    public void RepasteVerdict_FiresOnce_NotOnEveryLaunch()
    {
        // Epoch 0: the pre-repaste reference this epoch is judged against.
        _repo.UpsertBaseline(new[]
        {
            new BaselineRow(0, ComponentKind.Cpu, CpuName, 2, LoadBucket.Heavy, 60, 63, null, null, 60,
                T0.AddDays(-10).ToUnixTimeSeconds(), 0.2, 85),
        });
        StartEpoch(1, T0, "repaste");
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(6));

        DateTimeOffset now = T0.AddDays(6); // past the cure ramp for a repaste epoch
        UseSnapshot(now);
        var coordinator = NewCoordinator();
        coordinator.Compute(now);

        RepasteReport? report = coordinator.ConsumeRepasteReport();
        Assert.NotNull(report);
        Assert.Equal(RepasteVerdict.Unchanged, report!.Verdict); // same deltas as before

        // "Next login": a fresh coordinator over the same store must stay quiet.
        var relaunched = NewCoordinator();
        relaunched.Compute(now.AddHours(1));
        Assert.Null(relaunched.ConsumeRepasteReport());
    }

    // ------------------------------------------------------- smart recalibrate

    [Fact]
    public void Recalibrate_AdoptsOldBaseline_WhenMachineBehavesTheSame()
    {
        // Epoch 0 locks a real reference, then the user hits Recalibrate. The new
        // epoch's data matches the old baseline like-for-like — so the old reference
        // must carry over (with all its cells), not be relearned from scratch.
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2));
        UseSnapshot(T0.AddDays(2));
        var coordinator = NewCoordinator();
        coordinator.Compute(T0.AddDays(2));
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));

        DateTimeOffset recal = T0.AddDays(3);
        coordinator.Recalibrate(recal);
        Assert.Null(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));

        // Same deltas as the old baseline, two separated bouts (35 loaded minutes).
        WriteSession(ComponentKind.Cpu, CpuName, recal.AddHours(1), 20, 60.0);
        WriteSession(ComponentKind.Cpu, CpuName, recal.AddHours(4), 15, 60.1);
        coordinator.Compute(recal.AddHours(5)); // builds the provisional rows
        var scores = coordinator.Compute(recal.AddHours(5).AddMinutes(5)); // compares + adopts

        Assert.False(scores[ComponentKind.Cpu].Calibrating); // adopted, locked — days early
        Assert.True(_settings.GetBool($"{SettingsKeys.BaselineLockEarned}.{ComponentKind.Cpu}", false));
        // The full old reference (all cells) lives in the new epoch now.
        Assert.Contains(_repo.GetBaseline(1), r => r.Bucket == LoadBucket.Heavy && r.Minutes >= 60);
        // And the adoption was announced instead of a bogus "recalibration complete".
        Assert.Contains(_repo.GetEvents("remark", 0, recal.AddDays(1).ToUnixTimeSeconds(), 50),
            e => e.Message.Contains("carries over"));
    }

    [Fact]
    public void Recalibrate_Relearns_WhenMachineRunsHotter()
    {
        // The recalibrated machine genuinely runs ~8° hotter (dust, cooler change) —
        // adopting the old baseline would hide exactly what the user asked to re-check.
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2));
        UseSnapshot(T0.AddDays(2));
        var coordinator = NewCoordinator();
        coordinator.Compute(T0.AddDays(2));

        DateTimeOffset recal = T0.AddDays(3);
        coordinator.Recalibrate(recal);
        WriteSession(ComponentKind.Cpu, CpuName, recal.AddHours(1), 20, 68.0);
        WriteSession(ComponentKind.Cpu, CpuName, recal.AddHours(4), 15, 68.2);
        coordinator.Compute(recal.AddHours(5));
        var scores = coordinator.Compute(recal.AddHours(5).AddMinutes(5));

        Assert.True(scores[ComponentKind.Cpu].Calibrating); // no adoption — keeps learning
        Assert.Null(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));
    }

    // ------------------------------------------------------- per-component locks

    [Fact]
    public void CpuAndGpu_LockIndependently()
    {
        // CPU earns its lock early; the GPU has seen nothing but idle. The GPU's
        // learning window must keep growing (the old shared freeze pinned it forever),
        // so when real GPU load finally arrives, it locks too.
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2));
        WriteSession(ComponentKind.GpuDiscrete, GpuName, T0.AddHours(2), 120, 12, LoadBucket.Idle);

        DateTimeOffset day2 = T0.AddDays(2);
        UseSnapshot(day2, withGpu: true);
        var coordinator = NewCoordinator();
        var scores = coordinator.Compute(day2);
        Assert.False(scores[ComponentKind.Cpu].Calibrating);
        Assert.True(scores[ComponentKind.GpuDiscrete].Calibrating);

        // A week later the GPU finally gets exercised — its window grew to include it.
        WriteLockworthyLoad(ComponentKind.GpuDiscrete, GpuName, T0.AddDays(5));
        DateTimeOffset day7 = T0.AddDays(7);
        UseSnapshot(day7, withGpu: true);
        scores = coordinator.Compute(day7);

        Assert.False(scores[ComponentKind.GpuDiscrete].Calibrating);
        DateTimeOffset cpuLock = _settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}")!.Value;
        DateTimeOffset gpuLock = _settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.GpuDiscrete}")!.Value;
        Assert.True(gpuLock > cpuLock);
    }

    [Fact]
    public void LockedBaseline_ServesStoredRows_EvenAfterMinutesArePruned()
    {
        // Once locked, the reference must come from the baseline table, not be
        // re-derived from minute aggregates — those are pruned at 90 days, and the old
        // rebuild-every-pass path silently evaporated the baseline with them.
        DateTimeOffset now = T0.AddDays(6);
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2));
        UseSnapshot(now);
        var coordinator = NewCoordinator();
        coordinator.Compute(now);
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));

        // Simulate retention: the learning window's minutes vanish.
        _repo.Prune(now.AddDays(91), TimeSpan.FromHours(1), TimeSpan.FromDays(90));

        var later = NewCoordinator();
        var scores = later.Compute(now.AddDays(92));
        // Still locked (earned locks are trusted), still judging against real cells.
        Assert.False(scores[ComponentKind.Cpu].Calibrating);
        Assert.Contains(_repo.GetBaseline(0), r => r.Bucket == LoadBucket.Heavy && r.Kind == ComponentKind.Cpu);
    }

    // --------------------------------------------------- fixed indoor temperature mode

    private void EnableFixedMode() => _settings.SetBool(SettingsKeys.IndoorFixedMode, true);
    private void DisableFixedMode() => _settings.SetBool(SettingsKeys.IndoorFixedMode, false);

    [Fact]
    public void WeatherAndFixed_LearnSeparateBaselines_ThatNeverMix()
    {
        // Weather mode first: learn a baseline at rise 60.
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2), baseDelta: 60, mode: 0);
        DateTimeOffset weatherNow = T0.AddDays(1);
        UseSnapshot(weatherNow);
        var coordinator = NewCoordinator();
        coordinator.Compute(weatherNow);
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));

        // Switch to fixed indoor mode. The fixed reference sits higher than the outside was, so
        // the die's rise over it is smaller (45). Both regimes' data land in the SAME time window
        // and the SAME ambient band from here on, so only the mode tag can keep them apart.
        EnableFixedMode();
        coordinator.EnsureFixedModeStarted(weatherNow);

        // With no fixed baseline yet, fixed mode must NOT borrow the weather lock/score.
        UseSnapshot(weatherNow.AddMinutes(1));
        var early = coordinator.Compute(weatherNow.AddMinutes(1));
        Assert.False(early[ComponentKind.Cpu].Scored); // fixed baseline is still calibrating
        Assert.Null(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}.fixed"));

        // Interleave both regimes' data in the same window: fixed at 45, weather at 60.
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, weatherNow.AddHours(1), baseDelta: 45, mode: 1);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, weatherNow.AddHours(2), baseDelta: 60, mode: 0);

        DateTimeOffset fixedNow = weatherNow.AddDays(1);
        UseSnapshot(fixedNow);
        coordinator.Compute(fixedNow);

        // Fixed baseline locked independently, and each baseline learned ITS OWN rise, uncontaminated.
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}.fixed"));
        double fixedDelta = _repo.GetBaseline(0, mode: 1).Single(r => r.Bucket == LoadBucket.Heavy).DeltaAvg;
        double weatherDelta = _repo.GetBaseline(0, mode: 0).Single(r => r.Bucket == LoadBucket.Heavy).DeltaAvg;
        Assert.InRange(fixedDelta, 44, 46);    // learned the fixed regime, not pulled toward 60
        Assert.InRange(weatherDelta, 59, 61);  // weather baseline untouched by the fixed data
    }

    [Fact]
    public void TogglingModes_SwitchesBaselines_WithoutWiping()
    {
        // Learn both baselines.
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2), baseDelta: 60, mode: 0);
        DateTimeOffset t1 = T0.AddDays(1);
        UseSnapshot(t1);
        var coordinator = NewCoordinator();
        coordinator.Compute(t1);

        EnableFixedMode();
        coordinator.EnsureFixedModeStarted(t1);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, t1.AddHours(1), baseDelta: 45, mode: 1);
        DateTimeOffset t2 = t1.AddDays(1);
        UseSnapshot(t2);
        coordinator.Compute(t2);

        string weatherLock = $"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}";
        string fixedLock = $"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}.fixed";
        DateTimeOffset? weatherAt = _settings.GetTimestamp(weatherLock);
        DateTimeOffset? fixedAt = _settings.GetTimestamp(fixedLock);
        Assert.NotNull(weatherAt);
        Assert.NotNull(fixedAt);

        // Flip weather -> fixed -> weather several times and compute each time. Neither lock may
        // be cleared or moved: toggling switches which baseline is active, it never relearns.
        for (int i = 0; i < 3; i++)
        {
            DisableFixedMode();
            UseSnapshot(t2.AddHours(i * 2 + 1));
            var w = coordinator.Compute(t2.AddHours(i * 2 + 1));
            Assert.True(w[ComponentKind.Cpu].Scored); // weather baseline serves immediately

            EnableFixedMode();
            UseSnapshot(t2.AddHours(i * 2 + 2));
            var f = coordinator.Compute(t2.AddHours(i * 2 + 2));
            Assert.True(f[ComponentKind.Cpu].Scored); // fixed baseline serves immediately
        }

        Assert.Equal(weatherAt, _settings.GetTimestamp(weatherLock));
        Assert.Equal(fixedAt, _settings.GetTimestamp(fixedLock));
    }

    [Fact]
    public void ChangingFixedTemperature_RelearnsFixedBaseline_LeavesWeatherUntouched()
    {
        StartEpoch(0, T0);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, T0.AddHours(2), baseDelta: 60, mode: 0);
        DateTimeOffset t1 = T0.AddDays(1);
        UseSnapshot(t1);
        var coordinator = NewCoordinator();
        coordinator.Compute(t1);

        EnableFixedMode();
        coordinator.EnsureFixedModeStarted(t1);
        WriteLockworthyLoad(ComponentKind.Cpu, CpuName, t1.AddHours(1), baseDelta: 45, mode: 1);
        DateTimeOffset t2 = t1.AddDays(1);
        UseSnapshot(t2);
        coordinator.Compute(t2);
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}.fixed"));

        // User changes the fixed temperature: the fixed baseline is invalid (learned at the old
        // reference) and must be dropped and relearned; weather is never touched.
        coordinator.ResetFixedBaseline(t2.AddMinutes(1));
        Assert.Null(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}.fixed"));
        Assert.Empty(_repo.GetBaseline(0, mode: 1));
        Assert.NotEmpty(_repo.GetBaseline(0, mode: 0)); // weather baseline survives
        Assert.NotNull(_settings.GetTimestamp($"{SettingsKeys.BaselineLockedUtc}.{ComponentKind.Cpu}"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}

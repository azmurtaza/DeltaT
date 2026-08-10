using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeltaT.Core.Storage;

/// <summary>Key-value settings backed by the settings table, cached in memory.
/// Thread-safe. Typed helpers keep parsing in one place.</summary>
public sealed class SettingsStore
{
    private readonly DeltaTDb _db;
    private readonly Dictionary<string, string> _cache = new();
    private readonly object _gate = new();
    private Task _deferredWrites = Task.CompletedTask;

    public SettingsStore(DeltaTDb db)
    {
        _db = db;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            _cache[reader.GetString(0)] = reader.GetString(1);
    }

    public string? Get(string key)
    {
        lock (_gate) return _cache.TryGetValue(key, out string? v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate) _cache[key] = value;
        Persist(key, value);
    }

    /// <summary>Set a value without waiting for the database. The in-memory cache (which every
    /// read goes through) is updated synchronously, so the new value is live the instant this
    /// returns; only the row write is handed to a background writer.
    ///
    /// <para>For settings a user flips from the UI thread. A settings write is a SQLite write,
    /// and SQLite serializes writers: if the telemetry batch or a scoring pass happens to hold
    /// the write lock, the call blocks on <c>busy_timeout</c> (5 s) and the click visibly hangs.
    /// Nothing about that wait makes the setting more correct, since the cache already answers
    /// every read. Writes are chained, so two sets of the same key still land in order, and a
    /// crash in the millisecond before the row lands simply leaves the previous value.</para></summary>
    public void SetDeferred(string key, string value)
    {
        lock (_gate)
        {
            _cache[key] = value;
            _deferredWrites = _deferredWrites.ContinueWith(
                _ => Persist(key, value), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public void SetIntDeferred(string key, int value) => SetDeferred(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Waits for every <see cref="SetDeferred"/> write to reach the database. For
    /// shutdown (and tests that tear the file down), so a deferred row is never in flight when
    /// the connection goes away. Reads never need it: the cache already answers them.</summary>
    public void Flush()
    {
        Task pending;
        lock (_gate) pending = _deferredWrites;
        pending.Wait();
    }

    private void Persist(string key, string value)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public double? GetDouble(string key) =>
        Get(key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    public void SetDouble(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    public int? GetInt(string key) =>
        Get(key) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;

    public void SetInt(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public bool GetBool(string key, bool fallback) => Get(key) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => fallback,
    };

    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");

    public DateTimeOffset? GetTimestamp(string key) =>
        Get(key) is { } s && long.TryParse(s, out long unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;

    public void SetTimestamp(string key, DateTimeOffset ts) => Set(key, ts.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
}

/// <summary>Well-known settings keys, so they aren't scattered as strings.</summary>
public static class SettingsKeys
{
    public const string LocationJson = "ambient.location";        // GeoLocation as JSON
    public const string LastAmbientC = "ambient.lastTemp";
    public const string LastAmbientFetched = "ambient.lastFetched";
    public const string IndoorOffsetC = "ambient.indoorOffset";   // room − outside, user tunable (weather mode, display only)
    public const string WeatherRefreshHours = "ambient.refreshHours"; // outside-temp re-fetch cadence (1..6, default 3)
    public const string IndoorFixedMode = "ambient.fixedMode";    // 1 = score against a fixed indoor temp, not outside weather
    public const string IndoorFixedTempC = "ambient.fixedTempC";  // the user-set fixed indoor temperature (°C)
    public const string IndoorFixedSince = "ambient.fixedSince";  // when the current fixed-temp reference began (learning floor for fixed mode)

    // Cooling setups: named airflow arrangements (a cooler pad's fan up or down, a side panel
    // on or off) that each learn their own baseline. See Scoring/CoolingProfiles.cs for why
    // this is a reference regime and not a correction. An install that never touches the
    // feature stores none of these and behaves exactly as before.
    public const string CoolingProfiles = "cooling.profiles";          // "id:name" records, newline separated; the default setup is implicit
    public const string ActiveCoolingProfile = "cooling.active";       // id of the setup currently in use
    public const string CoolingProfileNextId = "cooling.nextId";       // monotonic id allocator; ids are never reused
    public const string CoolingProfileDefaultName = "cooling.defaultName"; // user's name for the implicit default setup
    public const string CoolingProfileSince = "cooling.since";         // "…{id}" = when a setup was declared (its learning floor)

    public const string BaselineEpoch = "baseline.epoch";          // increments on repaste
    public const string BaselineEpochStart = "baseline.epochStart";
    public const string BaselineEpochReason = "baseline.epochReason"; // initial | repaste | recalibrate
    public const string BaselineEpochKinds = "baseline.epochKinds";   // which components this epoch actually reset (comma-joined); empty = all of them
    public const string BaselineLockedUtc = "baseline.lockedUtc";   // legacy shared lock; per-component locks live at "baseline.lockedUtc.{Kind}"
    public const string BaselineLockEarned = "baseline.lockEarned"; // "…{Kind}" = the lock came from a real confidence pass (never self-healed away)
    public const string BaselineOutcomeReportedEpoch = "baseline.outcomeReported"; // epoch whose repaste/recalibrate verdict was already announced
    public const string BaselineMeterPeak = "baseline.meterPeak";   // "…{Kind}" = "epoch:value" ratchet: the calibration meter never shows less than it already showed this epoch
    public const string BaselineScoreShown = "baseline.scoreShown"; // "…{Kind}" = epoch in which a provisional score was first shown (once shown, it stays shown)

    public const string LastSeenUtc = "app.lastSeenUtc";           // heartbeat: last scoring pass, for dormancy detection

    public const string UnitsFahrenheit = "ui.fahrenheit";
    public const string Clock12Hour = "ui.clock12h";               // show times as 12-hour (AM/PM) instead of 24-hour
    public const string CloseToTray = "ui.closeToTray";
    public const string NotificationsEnabled = "ui.notifications";
    public const string SampleIntervalSeconds = "monitor.intervalSeconds";
    public const string CaptureEnabled = "monitor.captureEnabled";  // background sensor sampling on/off (off = pause polling)
    public const string AutostartEnabled = "app.autostart";
    public const string AutoUpdate = "app.autoUpdate";             // check GitHub for newer releases and self-update
    public const string FirstRunDone = "app.firstRunDone";

    // Self-update bookkeeping. The updater shuts the app down to hand off to the setup and
    // the helper brings it back either way, so without a record of what was already tried a
    // failing install repeats on every launch forever (see UpdateAttemptGate). These are
    // cleared the moment the running version catches up with what was attempted.
    public const string UpdateAttemptVersion = "app.updateAttemptVersion"; // version last handed to the installer
    public const string UpdateAttemptCount = "app.updateAttemptCount";     // how many times, for that version
    public const string UpdateAttemptUtc = "app.updateAttemptUtc";         // when the last attempt started
    public const string UpdateFailureNoticed = "app.updateFailNoticed";    // version whose failure was already reported to the user

    public const string WhatsNewShownVersion = "app.whatsNewShown"; // "M.m.b" of the last release whose what's-new popup was shown
    public const string FeedbackContact = "feedback.lastContact";   // last contact info typed into a feedback report, remembered for next time

    // Overclocker-friendly warning limits. Concern override is the sustained-average
    // temperature past which DeltaT treats the machine as too hot; null = use the
    // chassis profile's number. Headroom warnings flag peaks that kiss the silicon
    // limit; a rig pinned near TjMax by design can turn them off (real throttle
    // EVENTS are always still counted).
    public const string ConcernOverrideCpuC = "scoring.concernCpuC";
    public const string ConcernOverrideGpuC = "scoring.concernGpuC";
    public const string HeadroomWarnings = "scoring.headroomWarnings";
}

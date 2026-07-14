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
    public const string IndoorOffsetC = "ambient.indoorOffset";   // room − outside, user tunable

    public const string BaselineEpoch = "baseline.epoch";          // increments on repaste
    public const string BaselineEpochStart = "baseline.epochStart";
    public const string BaselineEpochReason = "baseline.epochReason"; // initial | repaste | recalibrate
    public const string BaselineLockedUtc = "baseline.lockedUtc";   // legacy shared lock; per-component locks live at "baseline.lockedUtc.{Kind}"
    public const string BaselineLockEarned = "baseline.lockEarned"; // "…{Kind}" = the lock came from a real confidence pass (never self-healed away)
    public const string BaselineOutcomeReportedEpoch = "baseline.outcomeReported"; // epoch whose repaste/recalibrate verdict was already announced
    public const string BaselineMeterPeak = "baseline.meterPeak";   // "…{Kind}" = "epoch:value" ratchet: the calibration meter never shows less than it already showed this epoch
    public const string BaselineScoreShown = "baseline.scoreShown"; // "…{Kind}" = epoch in which a provisional score was first shown (once shown, it stays shown)

    public const string LastSeenUtc = "app.lastSeenUtc";           // heartbeat: last scoring pass, for dormancy detection

    public const string UnitsFahrenheit = "ui.fahrenheit";
    public const string CloseToTray = "ui.closeToTray";
    public const string NotificationsEnabled = "ui.notifications";
    public const string SampleIntervalSeconds = "monitor.intervalSeconds";
    public const string CaptureEnabled = "monitor.captureEnabled";  // background sensor sampling on/off (off = pause polling)
    public const string AutostartEnabled = "app.autostart";
    public const string AutoUpdate = "app.autoUpdate";             // check GitHub for newer releases and self-update
    public const string FirstRunDone = "app.firstRunDone";

    // Overclocker-friendly warning limits. Concern override is the sustained-average
    // temperature past which DeltaT treats the machine as too hot; null = use the
    // chassis profile's number. Headroom warnings flag peaks that kiss the silicon
    // limit; a rig pinned near TjMax by design can turn them off (real throttle
    // EVENTS are always still counted).
    public const string ConcernOverrideCpuC = "scoring.concernCpuC";
    public const string ConcernOverrideGpuC = "scoring.concernGpuC";
    public const string HeadroomWarnings = "scoring.headroomWarnings";
}

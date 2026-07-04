using Microsoft.Data.Sqlite;

namespace DeltaT.Core.Storage;

/// <summary>Owns the SQLite file (default: %LOCALAPPDATA%\DeltaT\deltat.db),
/// schema creation and migrations. Connections are cheap (pooled) — open one
/// per operation and dispose it.</summary>
public sealed class DeltaTDb
{
    public string DbPath { get; }
    private readonly string _connectionString;

    public DeltaTDb(string? dbPath = null)
    {
        DbPath = dbPath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Migrate();
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaT", "deltat.db");

    /// <summary>One-time carry-over of the store from the app's old name (Kelvin),
    /// so learned baselines and history survive the rename. No-op after the move.</summary>
    public static void MigrateLegacyStore()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string oldDir = Path.Combine(root, "Kelvin");
            string newDir = Path.Combine(root, "DeltaT");
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
                return;

            Directory.Move(oldDir, newDir);
            foreach ((string from, string to) in new[]
            {
                ("kelvin.db", "deltat.db"),
                ("kelvin.db-wal", "deltat.db-wal"),
                ("kelvin.db-shm", "deltat.db-shm"),
                ("kelvin-sim.db", "deltat-sim.db"),
                ("kelvin-sim.db-wal", "deltat-sim.db-wal"),
                ("kelvin-sim.db-shm", "deltat-sim.db-shm"),
                ("kelvin.log", "deltat.log"),
            })
            {
                string src = Path.Combine(newDir, from);
                if (File.Exists(src))
                    File.Move(src, Path.Combine(newDir, to), overwrite: false);
            }
        }
        catch
        {
            // A fresh store is the fallback; never block startup on the rename.
        }
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void Migrate()
    {
        using var conn = Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        long version;
        using (var get = conn.CreateCommand())
        {
            get.CommandText = "PRAGMA user_version;";
            version = (long)get.ExecuteScalar()!;
        }

        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS samples(
                    ts        INTEGER NOT NULL,
                    kind      TEXT    NOT NULL,
                    name      TEXT    NOT NULL,
                    temp      REAL,
                    hotspot   REAL,
                    load      REAL,
                    fan       REAL,
                    power     REAL,
                    throttling INTEGER NOT NULL DEFAULT 0,
                    ambient   REAL,
                    on_ac     INTEGER NOT NULL DEFAULT 1
                );
                CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples(ts);
                CREATE INDEX IF NOT EXISTS ix_samples_comp ON samples(kind, name, ts);

                -- One row per (minute, component, load bucket, ambient band, power source).
                -- band = -1 when ambient was unknown at the time.
                CREATE TABLE IF NOT EXISTS agg_minute(
                    minute    INTEGER NOT NULL,
                    kind      TEXT    NOT NULL,
                    name      TEXT    NOT NULL,
                    bucket    INTEGER NOT NULL,
                    band      INTEGER NOT NULL,
                    on_ac     INTEGER NOT NULL,
                    n         INTEGER NOT NULL,
                    temp_sum  REAL    NOT NULL,
                    temp_min  REAL    NOT NULL,
                    temp_max  REAL    NOT NULL,
                    load_sum  REAL    NOT NULL,
                    delta_sum REAL    NOT NULL DEFAULT 0,
                    delta_n   INTEGER NOT NULL DEFAULT 0,
                    fan_sum   REAL    NOT NULL DEFAULT 0,
                    fan_n     INTEGER NOT NULL DEFAULT 0,
                    throttle_n INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(minute, kind, name, bucket, band, on_ac)
                );

                CREATE TABLE IF NOT EXISTS agg_hour(
                    hour      INTEGER NOT NULL,
                    kind      TEXT    NOT NULL,
                    name      TEXT    NOT NULL,
                    bucket    INTEGER NOT NULL,
                    band      INTEGER NOT NULL,
                    on_ac     INTEGER NOT NULL,
                    n         INTEGER NOT NULL,
                    temp_sum  REAL    NOT NULL,
                    temp_min  REAL    NOT NULL,
                    temp_max  REAL    NOT NULL,
                    load_sum  REAL    NOT NULL,
                    delta_sum REAL    NOT NULL DEFAULT 0,
                    delta_n   INTEGER NOT NULL DEFAULT 0,
                    fan_sum   REAL    NOT NULL DEFAULT 0,
                    fan_n     INTEGER NOT NULL DEFAULT 0,
                    throttle_n INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(hour, kind, name, bucket, band, on_ac)
                );

                -- throttle | soak | remark | repaste | fingerprint | system
                CREATE TABLE IF NOT EXISTS events(
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts       INTEGER NOT NULL,
                    type     TEXT    NOT NULL,
                    kind     TEXT,
                    name     TEXT,
                    severity INTEGER NOT NULL DEFAULT 0,
                    message  TEXT    NOT NULL,
                    data     TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);
                CREATE INDEX IF NOT EXISTS ix_events_type ON events(type, ts);

                -- Learned healthy behaviour. epoch increments on every repaste,
                -- so history from before a repaste never pollutes the new baseline.
                CREATE TABLE IF NOT EXISTS baseline(
                    epoch     INTEGER NOT NULL,
                    kind      TEXT    NOT NULL,
                    name      TEXT    NOT NULL,
                    band      INTEGER NOT NULL,
                    bucket    INTEGER NOT NULL,
                    delta_avg REAL    NOT NULL,
                    delta_p95 REAL,
                    soak_rate REAL,
                    fan_avg   REAL,
                    minutes   INTEGER NOT NULL,
                    updated   INTEGER NOT NULL,
                    PRIMARY KEY(epoch, kind, name, band, bucket)
                );

                CREATE TABLE IF NOT EXISTS settings(
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        if (version < 2)
        {
            // The app used to be called Kelvin; stored remarks/events keep their
            // historical text, so rewrite the name once. Cosmetic but user-facing.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE events SET message = replace(replace(message, 'Kelvin', 'DeltaT'), 'kelvin', 'deltat');
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }
    }
}

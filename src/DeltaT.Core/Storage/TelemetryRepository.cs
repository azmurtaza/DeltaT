using DeltaT.Core.Monitoring;
using Microsoft.Data.Sqlite;

namespace DeltaT.Core.Storage;

public sealed record RawSampleRow(
    long Ts, ComponentKind Kind, string Name,
    double? Temp, double? Hotspot, double? Load, double? Fan, double? Power,
    bool Throttling, double? Ambient, bool OnAc);

/// <summary>Accumulated statistics for one (minute, component, bucket, band, power) cell.</summary>
public sealed class MinuteAccum
{
    public required long Minute;
    public required ComponentKind Kind;
    public required string Name;
    public required LoadBucket Bucket;
    public required int Band; // AmbientBand as int, -1 = ambient unknown
    public required bool OnAc;
    public int N;
    public double TempSum, TempMin = double.MaxValue, TempMax = double.MinValue;
    public double LoadSum;
    public double DeltaSum;
    public int DeltaN;
    public double FanSum;
    public int FanN;
    public int ThrottleN;
    // Hotspot-to-edge gap (hotspot − temp), when the sensor exposes a hotspot.
    public double GapSum;
    public int GapN;
}

public sealed record BucketStat(
    LoadBucket Bucket, int Band, bool OnAc,
    int Minutes, long SampleCount,
    double TempAvg, double TempMin, double TempMax,
    double LoadAvg, double? DeltaAvg, double? FanAvg, int ThrottleCount,
    double? GapAvg = null);

public sealed record SeriesPoint(long Ts, double? TempAvg, double? TempMin, double? TempMax, double? LoadAvg, double? Ambient);

public sealed record StoredEvent(long Id, long Ts, string Type, string? Kind, string? Name, int Severity, string Message, string? Data);

public sealed record BaselineRow(
    int Epoch, ComponentKind Kind, string Name, int Band, LoadBucket Bucket,
    double DeltaAvg, double? DeltaP95, double? SoakRate, double? FanAvg, int Minutes, long Updated,
    // Standard error of the cell's mean delta, from independent session means.
    // Null for cells (or legacy rows) without enough sessions to estimate it.
    double? DeltaSe = null,
    // Mean absolute die temperature (°C) for this cell — the physical anchor behind
    // cross-band scoring. Null for legacy rows until the next baseline rebuild refills it.
    double? TempAvg = null,
    // This machine's own healthy hotspot-to-edge gap (°C) for the cell. Null when the
    // sensor exposes no hotspot (CPUs, older GPUs) or on legacy rows.
    double? GapAvg = null);

/// <summary>All reads/writes of telemetry. SQL lives here and nowhere else.</summary>
public sealed class TelemetryRepository
{
    private readonly DeltaTDb _db;

    public TelemetryRepository(DeltaTDb db) => _db = db;

    // ---------------------------------------------------------------- writes

    public void InsertSamples(IReadOnlyList<RawSampleRow> rows)
    {
        if (rows.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO samples(ts,kind,name,temp,hotspot,load,fan,power,throttling,ambient,on_ac)
            VALUES($ts,$kind,$name,$temp,$hotspot,$load,$fan,$power,$throttling,$ambient,$onac);
            """;
        var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pTemp = cmd.Parameters.Add("$temp", SqliteType.Real);
        var pHot = cmd.Parameters.Add("$hotspot", SqliteType.Real);
        var pLoad = cmd.Parameters.Add("$load", SqliteType.Real);
        var pFan = cmd.Parameters.Add("$fan", SqliteType.Real);
        var pPower = cmd.Parameters.Add("$power", SqliteType.Real);
        var pThr = cmd.Parameters.Add("$throttling", SqliteType.Integer);
        var pAmb = cmd.Parameters.Add("$ambient", SqliteType.Real);
        var pAc = cmd.Parameters.Add("$onac", SqliteType.Integer);

        foreach (RawSampleRow r in rows)
        {
            pTs.Value = r.Ts;
            pKind.Value = r.Kind.ToString();
            pName.Value = r.Name;
            pTemp.Value = (object?)r.Temp ?? DBNull.Value;
            pHot.Value = (object?)r.Hotspot ?? DBNull.Value;
            pLoad.Value = (object?)r.Load ?? DBNull.Value;
            pFan.Value = (object?)r.Fan ?? DBNull.Value;
            pPower.Value = (object?)r.Power ?? DBNull.Value;
            pThr.Value = r.Throttling ? 1 : 0;
            pAmb.Value = (object?)r.Ambient ?? DBNull.Value;
            pAc.Value = r.OnAc ? 1 : 0;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Additive upsert so a partially-written minute (app restart) merges
    /// instead of being lost or duplicated.</summary>
    public void UpsertMinutes(IReadOnlyList<MinuteAccum> minutes)
    {
        if (minutes.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO agg_minute(minute,kind,name,bucket,band,on_ac,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n)
            VALUES($m,$kind,$name,$bucket,$band,$onac,$n,$tsum,$tmin,$tmax,$lsum,$dsum,$dn,$fsum,$fn,$thn,$gsum,$gn)
            ON CONFLICT(minute,kind,name,bucket,band,on_ac) DO UPDATE SET
                n = n + excluded.n,
                temp_sum = temp_sum + excluded.temp_sum,
                temp_min = MIN(temp_min, excluded.temp_min),
                temp_max = MAX(temp_max, excluded.temp_max),
                load_sum = load_sum + excluded.load_sum,
                delta_sum = delta_sum + excluded.delta_sum,
                delta_n = delta_n + excluded.delta_n,
                fan_sum = fan_sum + excluded.fan_sum,
                fan_n = fan_n + excluded.fan_n,
                throttle_n = throttle_n + excluded.throttle_n,
                gap_sum = gap_sum + excluded.gap_sum,
                gap_n = gap_n + excluded.gap_n;
            """;
        AddAggParams(cmd);
        foreach (MinuteAccum a in minutes)
        {
            cmd.Parameters["$m"].Value = a.Minute;
            FillAggParams(cmd, a);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void AddAggParams(SqliteCommand cmd)
    {
        cmd.Parameters.Add("$m", SqliteType.Integer);
        cmd.Parameters.Add("$kind", SqliteType.Text);
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$bucket", SqliteType.Integer);
        cmd.Parameters.Add("$band", SqliteType.Integer);
        cmd.Parameters.Add("$onac", SqliteType.Integer);
        cmd.Parameters.Add("$n", SqliteType.Integer);
        cmd.Parameters.Add("$tsum", SqliteType.Real);
        cmd.Parameters.Add("$tmin", SqliteType.Real);
        cmd.Parameters.Add("$tmax", SqliteType.Real);
        cmd.Parameters.Add("$lsum", SqliteType.Real);
        cmd.Parameters.Add("$dsum", SqliteType.Real);
        cmd.Parameters.Add("$dn", SqliteType.Integer);
        cmd.Parameters.Add("$fsum", SqliteType.Real);
        cmd.Parameters.Add("$fn", SqliteType.Integer);
        cmd.Parameters.Add("$thn", SqliteType.Integer);
        cmd.Parameters.Add("$gsum", SqliteType.Real);
        cmd.Parameters.Add("$gn", SqliteType.Integer);
    }

    private static void FillAggParams(SqliteCommand cmd, MinuteAccum a)
    {
        cmd.Parameters["$kind"].Value = a.Kind.ToString();
        cmd.Parameters["$name"].Value = a.Name;
        cmd.Parameters["$bucket"].Value = (int)a.Bucket;
        cmd.Parameters["$band"].Value = a.Band;
        cmd.Parameters["$onac"].Value = a.OnAc ? 1 : 0;
        cmd.Parameters["$n"].Value = a.N;
        cmd.Parameters["$tsum"].Value = a.TempSum;
        cmd.Parameters["$tmin"].Value = a.TempMin;
        cmd.Parameters["$tmax"].Value = a.TempMax;
        cmd.Parameters["$lsum"].Value = a.LoadSum;
        cmd.Parameters["$dsum"].Value = a.DeltaSum;
        cmd.Parameters["$dn"].Value = a.DeltaN;
        cmd.Parameters["$fsum"].Value = a.FanSum;
        cmd.Parameters["$fn"].Value = a.FanN;
        cmd.Parameters["$thn"].Value = a.ThrottleN;
        cmd.Parameters["$gsum"].Value = a.GapSum;
        cmd.Parameters["$gn"].Value = a.GapN;
    }

    /// <summary>Rebuilds every hour row in a range from its minutes, in one statement.
    /// Called at pipeline start: an app shutdown (or crash) mid-hour leaves that hour
    /// unrolled forever — no later snapshot in it ever triggers the rollover — which
    /// punched a permanent hole in the 7d/30d charts at every quit. Idempotent.</summary>
    public void RollupHours(long fromHourUnix, long toHourUnixExclusive)
    {
        long from = fromHourUnix / 3600 * 3600;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agg_hour WHERE hour >= $from AND hour < $to;";
            del.Parameters.AddWithValue("$from", from);
            del.Parameters.AddWithValue("$to", toHourUnixExclusive);
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO agg_hour(hour,kind,name,bucket,band,on_ac,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n)
                SELECT minute / 3600 * 3600, kind, name, bucket, band, on_ac,
                       SUM(n), SUM(temp_sum), MIN(temp_min), MAX(temp_max), SUM(load_sum),
                       SUM(delta_sum), SUM(delta_n), SUM(fan_sum), SUM(fan_n), SUM(throttle_n),
                       SUM(gap_sum), SUM(gap_n)
                FROM agg_minute
                WHERE minute >= $from AND minute < $to
                GROUP BY minute / 3600 * 3600, kind, name, bucket, band, on_ac;
                """;
            ins.Parameters.AddWithValue("$from", from);
            ins.Parameters.AddWithValue("$to", toHourUnixExclusive);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Rebuilds one hour row from its minutes. Delete-then-insert keeps it idempotent.</summary>
    public void RollupHour(long hourStartUnix)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agg_hour WHERE hour=$h;";
            del.Parameters.AddWithValue("$h", hourStartUnix);
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO agg_hour(hour,kind,name,bucket,band,on_ac,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n)
                SELECT $h, kind, name, bucket, band, on_ac,
                       SUM(n), SUM(temp_sum), MIN(temp_min), MAX(temp_max), SUM(load_sum),
                       SUM(delta_sum), SUM(delta_n), SUM(fan_sum), SUM(fan_n), SUM(throttle_n),
                       SUM(gap_sum), SUM(gap_n)
                FROM agg_minute
                WHERE minute >= $h AND minute < $h + 3600
                GROUP BY kind, name, bucket, band, on_ac;
                """;
            ins.Parameters.AddWithValue("$h", hourStartUnix);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Prune(DateTimeOffset nowUtc, TimeSpan rawRetention, TimeSpan minuteRetention)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts < $rawCut; DELETE FROM agg_minute WHERE minute < $minCut;";
        cmd.Parameters.AddWithValue("$rawCut", (nowUtc - rawRetention).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$minCut", (nowUtc - minuteRetention).ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- events

    public long InsertEvent(long ts, string type, string? kind, string? name, int severity, string message, string? dataJson = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(ts,type,kind,name,severity,message,data)
            VALUES($ts,$type,$kind,$name,$sev,$msg,$data);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sev", severity);
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$data", (object?)dataJson ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<StoredEvent> GetEvents(string? type, long fromTs, long toTs, int limit = 200)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id,ts,type,kind,name,severity,message,data FROM events
            WHERE ts BETWEEN $from AND $to {(type is null ? "" : "AND type=$type")}
            ORDER BY ts DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        cmd.Parameters.AddWithValue("$limit", limit);
        if (type is not null) cmd.Parameters.AddWithValue("$type", type);
        var list = new List<StoredEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new StoredEvent(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return list;
    }

    public int CountEvents(string type, ComponentKind? kind, long fromTs, long toTs)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM events
            WHERE type=$type AND ts BETWEEN $from AND $to {(kind is null ? "" : "AND kind=$kind")};
            """;
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Average heat-soak rate (°C/min) from stored soak events in a window.</summary>
    public double? GetAverageSoakRate(ComponentKind kind, long fromTs, long toTs)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AVG(CAST(json_extract(data,'$.rate') AS REAL))
            FROM events WHERE type='soak' AND kind=$kind AND ts BETWEEN $from AND $to;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        object? result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToDouble(result);
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Per-bucket/band statistics over minute aggregates in a window —
    /// the shape both the scoring engine and the baseline builder consume.</summary>
    public IReadOnlyList<BucketStat> GetBucketStats(ComponentKind kind, string? name, long fromTs, long toTs)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT bucket, band, on_ac,
                   COUNT(DISTINCT minute), SUM(n),
                   SUM(temp_sum)/SUM(n), MIN(temp_min), MAX(temp_max),
                   SUM(load_sum)/SUM(n),
                   CASE WHEN SUM(delta_n) > 0 THEN SUM(delta_sum)/SUM(delta_n) END,
                   CASE WHEN SUM(fan_n) > 0 THEN SUM(fan_sum)/SUM(fan_n) END,
                   SUM(throttle_n),
                   CASE WHEN SUM(gap_n) > 0 THEN SUM(gap_sum)/SUM(gap_n) END
            FROM agg_minute
            WHERE kind=$kind {(name is null ? "" : "AND name=$name")} AND minute BETWEEN $from AND $to
            GROUP BY bucket, band, on_ac;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        if (name is not null) cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);

        var list = new List<BucketStat>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BucketStat(
                (LoadBucket)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0,
                reader.GetInt32(3), reader.GetInt64(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7),
                reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9),
                reader.IsDBNull(10) ? null : reader.GetDouble(10),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetDouble(12)));
        }
        return list;
    }

    /// <summary>Per-minute delta averages for one bucket — used to compute p95 for baselines.</summary>
    public IReadOnlyList<double> GetMinuteDeltas(ComponentKind kind, string name, LoadBucket bucket, int band, bool onAc, long fromTs, long toTs)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT delta_sum/delta_n FROM agg_minute
            WHERE kind=$kind AND name=$name AND bucket=$bucket AND band=$band AND on_ac=$onac
              AND delta_n > 0 AND minute BETWEEN $from AND $to;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$bucket", (int)bucket);
        cmd.Parameters.AddWithValue("$band", band);
        cmd.Parameters.AddWithValue("$onac", onAc ? 1 : 0);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        var list = new List<double>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetDouble(0));
        return list;
    }

    /// <summary>Per-session mean deltas for one cell. A "session" is a contiguous run
    /// of loaded minutes; a gap larger than <paramref name="gapSeconds"/> starts a new
    /// one. Collapsing each session to a single mean strips the heavy minute-to-minute
    /// autocorrelation, so the calibration model can treat them as independent samples
    /// and compute an honest standard error of the baseline mean.</summary>
    public IReadOnlyList<double> GetSessionMeanDeltas(
        ComponentKind kind, string name, LoadBucket bucket, int band, bool onAc, long fromTs, long toTs, int gapSeconds)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT minute, delta_sum/delta_n FROM agg_minute
            WHERE kind=$kind AND name=$name AND bucket=$bucket AND band=$band AND on_ac=$onac
              AND delta_n > 0 AND minute BETWEEN $from AND $to
            ORDER BY minute;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$bucket", (int)bucket);
        cmd.Parameters.AddWithValue("$band", band);
        cmd.Parameters.AddWithValue("$onac", onAc ? 1 : 0);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);

        var sessionMeans = new List<double>();
        long prevMinute = long.MinValue;
        double runSum = 0;
        int runN = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long minute = reader.GetInt64(0);
            double delta = reader.GetDouble(1);
            if (runN > 0 && minute - prevMinute > gapSeconds)
            {
                sessionMeans.Add(runSum / runN);
                runSum = 0;
                runN = 0;
            }
            runSum += delta;
            runN++;
            prevMinute = minute;
        }
        if (runN > 0)
            sessionMeans.Add(runSum / runN);
        return sessionMeans;
    }

    /// <summary>Distinct loaded (medium/heavy/full) usage bouts in a window, deduplicated
    /// across load buckets and ambient bands. A single gaming session oscillates between
    /// Medium, Heavy and Max (and can straddle an ambient-band boundary), so counting each
    /// cell's sessions separately would overstate how many independent observations the
    /// calibration model really has — one evening of play must count as one bout.</summary>
    public int CountLoadedSessions(ComponentKind kind, string name, bool onAc, long fromTs, long toTs, int gapSeconds)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT minute FROM agg_minute
            WHERE kind=$kind AND name=$name AND bucket IN ($med,$heavy,$max) AND band >= 0 AND on_ac=$onac
              AND delta_n > 0 AND minute BETWEEN $from AND $to
            ORDER BY minute;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$med", (int)LoadBucket.Medium);
        cmd.Parameters.AddWithValue("$heavy", (int)LoadBucket.Heavy);
        cmd.Parameters.AddWithValue("$max", (int)LoadBucket.Max);
        cmd.Parameters.AddWithValue("$onac", onAc ? 1 : 0);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);

        int sessions = 0;
        long prevMinute = long.MinValue;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long minute = reader.GetInt64(0);
            if (prevMinute == long.MinValue || minute - prevMinute > gapSeconds)
                sessions++;
            prevMinute = minute;
        }
        return sessions;
    }

    /// <summary>Chart series. Resolution: "raw" (samples), "minute" or "hour" (aggregates).</summary>
    public IReadOnlyList<SeriesPoint> GetSeries(ComponentKind kind, string? name, long fromTs, long toTs, string resolution)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        string nameFilter = name is null ? "" : "AND name=$name";
        cmd.CommandText = resolution switch
        {
            "raw" => $"""
                SELECT ts, temp, temp, temp, load, ambient FROM samples
                WHERE kind=$kind {nameFilter} AND ts BETWEEN $from AND $to AND temp IS NOT NULL
                ORDER BY ts;
                """,
            "minute" => $"""
                SELECT minute, SUM(temp_sum)/SUM(n), MIN(temp_min), MAX(temp_max), SUM(load_sum)/SUM(n),
                       CASE WHEN SUM(delta_n) > 0 THEN SUM(temp_sum)/SUM(n) - SUM(delta_sum)/SUM(delta_n) END
                FROM agg_minute
                WHERE kind=$kind {nameFilter} AND minute BETWEEN $from AND $to
                GROUP BY minute ORDER BY minute;
                """,
            _ => $"""
                SELECT hour, SUM(temp_sum)/SUM(n), MIN(temp_min), MAX(temp_max), SUM(load_sum)/SUM(n),
                       CASE WHEN SUM(delta_n) > 0 THEN SUM(temp_sum)/SUM(n) - SUM(delta_sum)/SUM(delta_n) END
                FROM agg_hour
                WHERE kind=$kind {nameFilter} AND hour BETWEEN $from AND $to
                GROUP BY hour ORDER BY hour;
                """,
        };
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        if (name is not null) cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);

        var list = new List<SeriesPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SeriesPoint(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }
        return list;
    }

    /// <summary>Hottest this component has ever been (minute aggregates cover
    /// everything except the last, still-open minute).</summary>
    public double? GetAllTimeMax(ComponentKind kind, string? name = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT MAX(m) FROM (
                SELECT MAX(temp_max) AS m FROM agg_minute WHERE kind=$kind {(name is null ? "" : "AND name=$name")}
                UNION ALL
                SELECT MAX(temp_max) AS m FROM agg_hour WHERE kind=$kind {(name is null ? "" : "AND name=$name")}
            );
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        if (name is not null) cmd.Parameters.AddWithValue("$name", name);
        object? result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToDouble(result);
    }

    // ---------------------------------------------------------------- baseline

    public void UpsertBaseline(IReadOnlyList<BaselineRow> rows)
    {
        if (rows.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO baseline(epoch,kind,name,band,bucket,delta_avg,delta_p95,soak_rate,fan_avg,minutes,updated,delta_se,temp_avg,gap_avg)
            VALUES($e,$kind,$name,$band,$bucket,$davg,$dp95,$soak,$fan,$min,$upd,$dse,$tavg,$gavg)
            ON CONFLICT(epoch,kind,name,band,bucket) DO UPDATE SET
                delta_avg=excluded.delta_avg, delta_p95=excluded.delta_p95, soak_rate=excluded.soak_rate,
                fan_avg=excluded.fan_avg, minutes=excluded.minutes, updated=excluded.updated,
                delta_se=excluded.delta_se, temp_avg=excluded.temp_avg, gap_avg=excluded.gap_avg;
            """;
        foreach (BaselineRow r in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$e", r.Epoch);
            cmd.Parameters.AddWithValue("$kind", r.Kind.ToString());
            cmd.Parameters.AddWithValue("$name", r.Name);
            cmd.Parameters.AddWithValue("$band", r.Band);
            cmd.Parameters.AddWithValue("$bucket", (int)r.Bucket);
            cmd.Parameters.AddWithValue("$davg", r.DeltaAvg);
            cmd.Parameters.AddWithValue("$dp95", (object?)r.DeltaP95 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$soak", (object?)r.SoakRate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fan", (object?)r.FanAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$min", r.Minutes);
            cmd.Parameters.AddWithValue("$upd", r.Updated);
            cmd.Parameters.AddWithValue("$dse", (object?)r.DeltaSe ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tavg", (object?)r.TempAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gavg", (object?)r.GapAvg ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Drops one component's learned rows for an epoch. Used when a baseline
    /// lock turns out to have been bogus (frozen by the old backfill bug): the rows it
    /// wrote were never confidence-earned, so the relearn starts from a clean slate.</summary>
    public void DeleteBaseline(int epoch, ComponentKind kind, string name)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM baseline WHERE epoch=$e AND kind=$kind AND name=$name;";
        cmd.Parameters.AddWithValue("$e", epoch);
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BaselineRow> GetBaseline(int epoch)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT epoch,kind,name,band,bucket,delta_avg,delta_p95,soak_rate,fan_avg,minutes,updated,delta_se,temp_avg,gap_avg FROM baseline WHERE epoch=$e;";
        cmd.Parameters.AddWithValue("$e", epoch);
        var list = new List<BaselineRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BaselineRow(
                reader.GetInt32(0),
                Enum.Parse<ComponentKind>(reader.GetString(1)),
                reader.GetString(2), reader.GetInt32(3), (LoadBucket)reader.GetInt32(4),
                reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.GetInt32(9), reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetDouble(12),
                reader.IsDBNull(13) ? null : reader.GetDouble(13)));
        }
        return list;
    }
}

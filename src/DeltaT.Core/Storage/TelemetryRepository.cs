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
    public int Mode; // ambient source: 0 = outside weather, 1 = fixed indoor temperature
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
    // Package power (watts), when the sensor exposes it. Powers thermal-resistance scoring.
    public double PowerSum;
    public int PowerN;
}

public sealed record BucketStat(
    LoadBucket Bucket, int Band, bool OnAc,
    int Minutes, long SampleCount,
    double TempAvg, double TempMin, double TempMax,
    double LoadAvg, double? DeltaAvg, double? FanAvg, int ThrottleCount,
    double? GapAvg = null, double? PowerAvg = null);

public sealed record SeriesPoint(long Ts, double? TempAvg, double? TempMin, double? TempMax, double? LoadAvg, double? Ambient);

/// <summary>One (week, load bucket, ambient band) loaded-cell aggregate — the raw
/// material for long-term drift/step detection. DeltaAvg is the mean rise-over-ambient.</summary>
public sealed record WeeklyLoadedCell(long WeekStartTs, LoadBucket Bucket, int Band, int Minutes, double DeltaAvg);

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
    double? GapAvg = null,
    // Mean package power (watts) the cell was learned at — the divisor behind
    // thermal-resistance scoring (ΔT ∝ P). Null when the sensor exposes no power.
    double? PowerAvg = null,
    // Ambient-source mode this baseline was learned under: 0 = outside weather,
    // 1 = fixed indoor temperature. Both coexist; the active toggle picks which.
    int Mode = 0);

/// <summary>One (load bucket, ambient band, power band) aggregate — a bucket/band split further
/// by power band, so the two regimes a bucket is learned across (CPU boost on/off, two power
/// limits) can be told apart. Pband is the power-band index (watts quantized). The raw material
/// for the power-tagged baseline sub-cells.</summary>
public sealed record PowerBandStat(
    LoadBucket Bucket, int Band, int Pband, int Minutes,
    double? DeltaAvg, double? FanAvg, double TempAvg, double? GapAvg, double? PowerAvg);

/// <summary>A power-tagged baseline sub-cell: the same shape as a baseline cell for one
/// (bucket, band) but learned at a single power regime (pband), stored beside the blended
/// cell so scoring can compare a reading to its own regime. Only the scoring rise/power match
/// reads these; every other baseline consumer keeps using the blended <see cref="BaselineRow"/>.</summary>
public sealed record BaselinePowerRow(
    int Epoch, ComponentKind Kind, string Name, int Band, LoadBucket Bucket, int Pband,
    double DeltaAvg, double? FanAvg, double? TempAvg, double? GapAvg, double? PowerAvg,
    int Minutes, long Updated, int Mode = 0);

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
            INSERT INTO agg_minute(minute,kind,name,bucket,band,on_ac,mode,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n,power_sum,power_n)
            VALUES($m,$kind,$name,$bucket,$band,$onac,$mode,$n,$tsum,$tmin,$tmax,$lsum,$dsum,$dn,$fsum,$fn,$thn,$gsum,$gn,$psum,$pn)
            ON CONFLICT(minute,kind,name,bucket,band,on_ac,mode) DO UPDATE SET
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
                gap_n = gap_n + excluded.gap_n,
                power_sum = power_sum + excluded.power_sum,
                power_n = power_n + excluded.power_n;
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
        cmd.Parameters.Add("$mode", SqliteType.Integer);
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
        cmd.Parameters.Add("$psum", SqliteType.Real);
        cmd.Parameters.Add("$pn", SqliteType.Integer);
    }

    private static void FillAggParams(SqliteCommand cmd, MinuteAccum a)
    {
        cmd.Parameters["$kind"].Value = a.Kind.ToString();
        cmd.Parameters["$name"].Value = a.Name;
        cmd.Parameters["$bucket"].Value = (int)a.Bucket;
        cmd.Parameters["$band"].Value = a.Band;
        cmd.Parameters["$onac"].Value = a.OnAc ? 1 : 0;
        cmd.Parameters["$mode"].Value = a.Mode;
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
        cmd.Parameters["$psum"].Value = a.PowerSum;
        cmd.Parameters["$pn"].Value = a.PowerN;
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
                INSERT INTO agg_hour(hour,kind,name,bucket,band,on_ac,mode,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n,power_sum,power_n)
                SELECT minute / 3600 * 3600, kind, name, bucket, band, on_ac, mode,
                       SUM(n), SUM(temp_sum), MIN(temp_min), MAX(temp_max), SUM(load_sum),
                       SUM(delta_sum), SUM(delta_n), SUM(fan_sum), SUM(fan_n), SUM(throttle_n),
                       SUM(gap_sum), SUM(gap_n), SUM(power_sum), SUM(power_n)
                FROM agg_minute
                WHERE minute >= $from AND minute < $to
                GROUP BY minute / 3600 * 3600, kind, name, bucket, band, on_ac, mode;
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
                INSERT INTO agg_hour(hour,kind,name,bucket,band,on_ac,mode,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n,power_sum,power_n)
                SELECT $h, kind, name, bucket, band, on_ac, mode,
                       SUM(n), SUM(temp_sum), MIN(temp_min), MAX(temp_max), SUM(load_sum),
                       SUM(delta_sum), SUM(delta_n), SUM(fan_sum), SUM(fan_n), SUM(throttle_n),
                       SUM(gap_sum), SUM(gap_n), SUM(power_sum), SUM(power_n)
                FROM agg_minute
                WHERE minute >= $h AND minute < $h + 3600
                GROUP BY kind, name, bucket, band, on_ac, mode;
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

    public long InsertEvent(long ts, string type, string? kind, string? name, int severity, string message, string? dataJson = null, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events(ts,type,kind,name,severity,message,data,mode)
            VALUES($ts,$type,$kind,$name,$sev,$msg,$data,$mode);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sev", severity);
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$data", (object?)dataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", mode);
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

    /// <summary>Count events of a type in a window. <paramref name="mode"/> null counts every
    /// ambient-source mode (the cross-mode view remarks want for "throttled in the last hour");
    /// a specific mode restricts to that regime, as scoring's recent window does.</summary>
    public int CountEvents(string type, ComponentKind? kind, long fromTs, long toTs, int? mode = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM events
            WHERE type=$type AND ts BETWEEN $from AND $to
              {(kind is null ? "" : "AND kind=$kind")}
              {(mode is null ? "" : "AND mode=$mode")};
            """;
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind.ToString());
        if (mode is not null) cmd.Parameters.AddWithValue("$mode", mode.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Average heat-soak rate (°C/min) from stored soak events in a window, for the
    /// given ambient-source mode.</summary>
    public double? GetAverageSoakRate(ComponentKind kind, long fromTs, long toTs, int mode = 0)
        => AverageEventRate("soak", kind, fromTs, toTs, mode);

    /// <summary>Average cooldown rate (°C/min, positive) from stored cooldown events —
    /// the falling-edge counterpart to the soak rate.</summary>
    public double? GetAverageCooldownRate(ComponentKind kind, long fromTs, long toTs, int mode = 0)
        => AverageEventRate("cooldown", kind, fromTs, toTs, mode);

    private double? AverageEventRate(string type, ComponentKind kind, long fromTs, long toTs, int mode)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // AC-only, like every other baseline/recent comparison: battery power limits
        // throttle the watts, and both rates ride the watts, so a battery-limited soak
        // or cooldown would read as a paste change the power normalizer can't see (its
        // power means come from the AC-filtered cells, so the battery watts are
        // invisible to it). Events written before the tag existed count as AC — the
        // same default the samples table has always used. Mode-scoped, so a fixed-indoor
        // rate is never averaged against a weather-mode one (they measure against
        // different ambient references and must never mix).
        cmd.CommandText = """
            SELECT AVG(CAST(json_extract(data,'$.rate') AS REAL))
            FROM events WHERE type=$type AND kind=$kind AND ts BETWEEN $from AND $to
              AND mode=$mode
              AND COALESCE(json_extract(data,'$.on_ac'), 1) = 1;
            """;
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        cmd.Parameters.AddWithValue("$mode", mode);
        object? result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToDouble(result);
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Per-bucket/band statistics over aggregates in a window — the shape both
    /// the scoring engine and the baseline builder consume. Resolution "minute" (default)
    /// reads agg_minute (kept 90 days); "hour" reads agg_hour (kept forever), so a
    /// year-ago comparison window can still be summarised after the minutes are pruned.
    /// The Minutes field is real minutes either way — hour rows count 60 per hour.</summary>
    public IReadOnlyList<BucketStat> GetBucketStats(ComponentKind kind, string? name, long fromTs, long toTs, string resolution = "minute", int mode = 0)
    {
        bool hour = resolution == "hour";
        string table = hour ? "agg_hour" : "agg_minute";
        string timeCol = hour ? "hour" : "minute";
        string minutesExpr = hour ? "COUNT(DISTINCT hour) * 60" : "COUNT(DISTINCT minute)";
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT bucket, band, on_ac,
                   {minutesExpr}, SUM(n),
                   SUM(temp_sum)/SUM(n), MIN(temp_min), MAX(temp_max),
                   SUM(load_sum)/SUM(n),
                   CASE WHEN SUM(delta_n) > 0 THEN SUM(delta_sum)/SUM(delta_n) END,
                   CASE WHEN SUM(fan_n) > 0 THEN SUM(fan_sum)/SUM(fan_n) END,
                   SUM(throttle_n),
                   CASE WHEN SUM(gap_n) > 0 THEN SUM(gap_sum)/SUM(gap_n) END,
                   CASE WHEN SUM(power_n) > 0 THEN SUM(power_sum)/SUM(power_n) END
            FROM {table}
            WHERE kind=$kind {(name is null ? "" : "AND name=$name")} AND {timeCol} BETWEEN $from AND $to
              AND mode=$mode
            GROUP BY bucket, band, on_ac;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        if (name is not null) cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        cmd.Parameters.AddWithValue("$mode", mode);

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
                reader.IsDBNull(12) ? null : reader.GetDouble(12),
                reader.IsDBNull(13) ? null : reader.GetDouble(13)));
        }
        return list;
    }

    /// <summary>Per (bucket, band, power band) aggregates over agg_minute — the raw material for
    /// power-tagged baseline sub-cells. Each minute row already carries that minute's mean watts
    /// (power_sum/power_n); grouping by that quantized into <paramref name="bandWidthW"/>-wide bands
    /// clusters a bucket's minutes into its power regimes with no schema change to the aggregates.
    /// AC power and known ambient only, matching the baseline pool.</summary>
    public IReadOnlyList<PowerBandStat> GetPowerBandStats(ComponentKind kind, string name, long fromTs, long toTs, double bandWidthW, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // pband = floor(minute-mean-watts / width). Only minutes that carry power feed this.
        cmd.CommandText = """
            SELECT bucket, band,
                   CAST((power_sum/power_n)/$w AS INTEGER) AS pband,
                   COUNT(DISTINCT minute),
                   CASE WHEN SUM(delta_n) > 0 THEN SUM(delta_sum)/SUM(delta_n) END,
                   CASE WHEN SUM(fan_n) > 0 THEN SUM(fan_sum)/SUM(fan_n) END,
                   SUM(temp_sum)/SUM(n),
                   CASE WHEN SUM(gap_n) > 0 THEN SUM(gap_sum)/SUM(gap_n) END,
                   SUM(power_sum)/SUM(power_n)
            FROM agg_minute
            WHERE kind=$kind AND name=$name AND on_ac=1 AND band>=0 AND power_n>0
              AND mode=$mode
              AND minute BETWEEN $from AND $to
            GROUP BY bucket, band, pband;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        cmd.Parameters.AddWithValue("$w", bandWidthW);
        cmd.Parameters.AddWithValue("$mode", mode);

        var list = new List<PowerBandStat>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new PowerBandStat(
                (LoadBucket)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8)));
        return list;
    }

    /// <summary>Per-minute delta averages for one bucket — used to compute p95 for baselines.</summary>
    public IReadOnlyList<double> GetMinuteDeltas(ComponentKind kind, string name, LoadBucket bucket, int band, bool onAc, long fromTs, long toTs, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT delta_sum/delta_n FROM agg_minute
            WHERE kind=$kind AND name=$name AND bucket=$bucket AND band=$band AND on_ac=$onac
              AND mode=$mode
              AND delta_n > 0 AND minute BETWEEN $from AND $to;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$bucket", (int)bucket);
        cmd.Parameters.AddWithValue("$band", band);
        cmd.Parameters.AddWithValue("$onac", onAc ? 1 : 0);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        cmd.Parameters.AddWithValue("$mode", mode);
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
        ComponentKind kind, string name, LoadBucket bucket, int band, bool onAc, long fromTs, long toTs, int gapSeconds, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT minute, delta_sum/delta_n FROM agg_minute
            WHERE kind=$kind AND name=$name AND bucket=$bucket AND band=$band AND on_ac=$onac
              AND mode=$mode
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
        cmd.Parameters.AddWithValue("$mode", mode);

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
    public int CountLoadedSessions(ComponentKind kind, string name, bool onAc, long fromTs, long toTs, int gapSeconds, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT minute FROM agg_minute
            WHERE kind=$kind AND name=$name AND bucket IN ($med,$heavy,$max) AND band >= 0 AND on_ac=$onac
              AND mode=$mode
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
        cmd.Parameters.AddWithValue("$mode", mode);

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

    /// <summary>Per-week loaded (medium/heavy/full) cell aggregates over hour rollups, on
    /// AC power with a known ambient band. Feeds long-term drift/step detection: each row
    /// keeps its own (bucket, band) so the trend can compare like-for-like against the
    /// baseline and stay honest across seasons instead of blaming summer on the paste.
    /// Weeks are anchored to the Unix epoch (Thursday), which is fine — only the spacing
    /// matters to a regression, not the calendar weekday.</summary>
    public IReadOnlyList<WeeklyLoadedCell> GetWeeklyLoadedCells(ComponentKind kind, string? name, long fromTs, long toTs, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT (hour / 604800) * 604800 AS week, bucket, band,
                   COUNT(DISTINCT hour) * 60, SUM(delta_sum)/SUM(delta_n)
            FROM agg_hour
            WHERE kind=$kind {(name is null ? "" : "AND name=$name")}
              AND on_ac=1 AND band >= 0 AND delta_n > 0 AND mode=$mode
              AND bucket IN ($med,$heavy,$max)
              AND hour BETWEEN $from AND $to
            GROUP BY week, bucket, band
            ORDER BY week;
            """;
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        if (name is not null) cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$med", (int)LoadBucket.Medium);
        cmd.Parameters.AddWithValue("$heavy", (int)LoadBucket.Heavy);
        cmd.Parameters.AddWithValue("$max", (int)LoadBucket.Max);
        cmd.Parameters.AddWithValue("$from", fromTs);
        cmd.Parameters.AddWithValue("$to", toTs);
        cmd.Parameters.AddWithValue("$mode", mode);

        var list = new List<WeeklyLoadedCell>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new WeeklyLoadedCell(
                reader.GetInt64(0), (LoadBucket)reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetDouble(4)));
        }
        return list;
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
            INSERT INTO baseline(epoch,kind,name,band,bucket,delta_avg,delta_p95,soak_rate,fan_avg,minutes,updated,delta_se,temp_avg,gap_avg,power_avg,mode)
            VALUES($e,$kind,$name,$band,$bucket,$davg,$dp95,$soak,$fan,$min,$upd,$dse,$tavg,$gavg,$pavg,$mode)
            ON CONFLICT(epoch,kind,name,band,bucket,mode) DO UPDATE SET
                delta_avg=excluded.delta_avg, delta_p95=excluded.delta_p95, soak_rate=excluded.soak_rate,
                fan_avg=excluded.fan_avg, minutes=excluded.minutes, updated=excluded.updated,
                delta_se=excluded.delta_se, temp_avg=excluded.temp_avg, gap_avg=excluded.gap_avg,
                power_avg=excluded.power_avg;
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
            cmd.Parameters.AddWithValue("$pavg", (object?)r.PowerAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mode", r.Mode);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Drops one component's learned rows for an epoch. Used when a baseline
    /// lock turns out to have been bogus (frozen by the old backfill bug): the rows it
    /// wrote were never confidence-earned, so the relearn starts from a clean slate.</summary>
    public void DeleteBaseline(int epoch, ComponentKind kind, string name, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM baseline WHERE epoch=$e AND kind=$kind AND name=$name AND mode=$mode;
            DELETE FROM baseline_power WHERE epoch=$e AND kind=$kind AND name=$name AND mode=$mode;
            """;
        cmd.Parameters.AddWithValue("$e", epoch);
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$mode", mode);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops every learned row for an epoch in one ambient-source mode, across all
    /// components. Used when a fixed-indoor reference changes: the old fixed baseline was learned
    /// against a different indoor temperature and must not linger.</summary>
    public void DeleteBaselineForMode(int epoch, int mode)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM baseline WHERE epoch=$e AND mode=$mode;
            DELETE FROM baseline_power WHERE epoch=$e AND mode=$mode;
            """;
        cmd.Parameters.AddWithValue("$e", epoch);
        cmd.Parameters.AddWithValue("$mode", mode);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BaselineRow> GetBaseline(int epoch, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT epoch,kind,name,band,bucket,delta_avg,delta_p95,soak_rate,fan_avg,minutes,updated,delta_se,temp_avg,gap_avg,power_avg,mode FROM baseline WHERE epoch=$e AND mode=$mode;";
        cmd.Parameters.AddWithValue("$e", epoch);
        cmd.Parameters.AddWithValue("$mode", mode);
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
                reader.IsDBNull(13) ? null : reader.GetDouble(13),
                reader.IsDBNull(14) ? null : reader.GetDouble(14),
                reader.GetInt32(15)));
        }
        return list;
    }

    /// <summary>Replace an epoch's power-tagged sub-cells for the buckets/bands in
    /// <paramref name="rows"/>. A bucket that is no longer multi-modal (all its rows gone from
    /// this set) has its stale sub-cells cleared first, so scoring falls back to the blended
    /// cell rather than an outdated regime split.</summary>
    public void UpsertBaselinePower(int epoch, ComponentKind kind, string name, IReadOnlyList<BaselinePowerRow> rows, int mode = 0)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        // Clear this component's sub-cells for the epoch AND mode, then write the current set. The
        // set is small (only multi-modal loaded buckets), so a full replace is simplest and keeps
        // the table from accumulating regimes the machine has stopped using. Scoped by mode so a
        // fixed-mode rebuild never wipes the weather-mode sub-cells (or vice versa).
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM baseline_power WHERE epoch=$e AND kind=$kind AND name=$name AND mode=$mode;";
            del.Parameters.AddWithValue("$e", epoch);
            del.Parameters.AddWithValue("$kind", kind.ToString());
            del.Parameters.AddWithValue("$name", name);
            del.Parameters.AddWithValue("$mode", mode);
            del.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO baseline_power(epoch,kind,name,band,bucket,pband,delta_avg,fan_avg,temp_avg,gap_avg,power_avg,minutes,updated,mode)
            VALUES($e,$kind,$name,$band,$bucket,$pband,$davg,$fan,$tavg,$gavg,$pavg,$min,$upd,$mode);
            """;
        foreach (BaselinePowerRow r in rows)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$e", r.Epoch);
            cmd.Parameters.AddWithValue("$kind", r.Kind.ToString());
            cmd.Parameters.AddWithValue("$name", r.Name);
            cmd.Parameters.AddWithValue("$band", r.Band);
            cmd.Parameters.AddWithValue("$bucket", (int)r.Bucket);
            cmd.Parameters.AddWithValue("$pband", r.Pband);
            cmd.Parameters.AddWithValue("$davg", r.DeltaAvg);
            cmd.Parameters.AddWithValue("$fan", (object?)r.FanAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tavg", (object?)r.TempAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gavg", (object?)r.GapAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pavg", (object?)r.PowerAvg ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$min", r.Minutes);
            cmd.Parameters.AddWithValue("$upd", r.Updated);
            cmd.Parameters.AddWithValue("$mode", r.Mode);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<BaselinePowerRow> GetBaselinePower(int epoch, ComponentKind kind, string name, int mode = 0)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT band,bucket,pband,delta_avg,fan_avg,temp_avg,gap_avg,power_avg,minutes,updated FROM baseline_power WHERE epoch=$e AND kind=$kind AND name=$name AND mode=$mode;";
        cmd.Parameters.AddWithValue("$e", epoch);
        cmd.Parameters.AddWithValue("$kind", kind.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$mode", mode);
        var list = new List<BaselinePowerRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new BaselinePowerRow(
                epoch, kind, name, reader.GetInt32(0), (LoadBucket)reader.GetInt32(1), reader.GetInt32(2),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.GetInt32(8), reader.GetInt64(9), mode));
        return list;
    }
}

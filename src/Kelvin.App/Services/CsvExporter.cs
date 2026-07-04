using System.Globalization;
using System.IO;
using System.Text;
using Kelvin.Core.Storage;

namespace Kelvin.App.Services;

public static class CsvExporter
{
    /// <summary>Exports minute-level aggregates — detailed enough for any
    /// spreadsheet analysis, small enough to open anywhere.</summary>
    public static int Export(KelvinDb db, string path)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT minute, kind, name, bucket, band, on_ac, n,
                   temp_sum/n, temp_min, temp_max, load_sum/n,
                   CASE WHEN delta_n > 0 THEN delta_sum/delta_n END,
                   throttle_n
            FROM agg_minute ORDER BY minute, kind;
            """;

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("minute_utc,component,name,load_bucket,ambient_band,on_ac,samples,temp_avg,temp_min,temp_max,load_avg,delta_vs_outside,throttle_samples");

        int rows = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string minute = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string name = reader.GetString(2).Replace('"', '\'');
            writer.WriteLine(string.Join(',',
                minute,
                reader.GetString(1),
                $"\"{name}\"",
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetDouble(7).ToString("0.##", CultureInfo.InvariantCulture),
                reader.GetDouble(8).ToString("0.##", CultureInfo.InvariantCulture),
                reader.GetDouble(9).ToString("0.##", CultureInfo.InvariantCulture),
                reader.GetDouble(10).ToString("0.##", CultureInfo.InvariantCulture),
                reader.IsDBNull(11) ? "" : reader.GetDouble(11).ToString("0.##", CultureInfo.InvariantCulture),
                reader.GetInt32(12)));
            rows++;
        }
        return rows;
    }
}

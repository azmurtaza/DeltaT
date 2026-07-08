using System.Globalization;
using System.IO;
using System.Text;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.App.Services;

public static class CsvExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exports minute-level aggregates as a spreadsheet-friendly CSV:
    /// separate local date and time columns, human-readable load levels, weather
    /// bands and power source, and units spelled out in every header.</summary>
    public static int Export(DeltaTDb db, string path)
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

        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("date,time,component,sensor,load_level,outside_band,power,samples,"
            + "temp_avg_c,temp_min_c,temp_max_c,load_avg_pct,rise_over_outside_c,throttle_samples");

        int rows = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // Stored UTC; the CSV is a display edge, so show the user's local time.
            DateTime local = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).LocalDateTime;

            writer.WriteLine(string.Join(',',
                local.ToString("yyyy-MM-dd", Inv),
                local.ToString("HH:mm", Inv),
                ComponentLabel(reader.GetString(1)),
                Quote(reader.GetString(2)),
                LoadLevel(reader.GetInt32(3)),
                OutsideBand(reader.GetInt32(4)),
                reader.GetInt32(5) != 0 ? "AC" : "Battery",
                reader.GetInt64(6),
                Num(reader.GetDouble(7)),
                Num(reader.GetDouble(8)),
                Num(reader.GetDouble(9)),
                Num(reader.GetDouble(10)),
                reader.IsDBNull(11) ? "" : Num(reader.GetDouble(11)),
                reader.GetInt32(12)));
            rows++;
        }
        return rows;
    }

    private static string Num(double v) => v.ToString("0.##", Inv);

    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"")}\"";

    private static string ComponentLabel(string kind) => kind switch
    {
        "Cpu" => "CPU",
        "GpuDiscrete" => "GPU",
        "GpuIntegrated" => "iGPU",
        "Storage" => "SSD",
        "Battery" => "Battery",
        "Board" => "Motherboard",
        _ => kind,
    };

    private static string LoadLevel(int bucket) =>
        Enum.IsDefined((LoadBucket)bucket) ? ((LoadBucket)bucket).ToString() : "Unknown";

    private static string OutsideBand(int band) => band switch
    {
        (int)AmbientBand.Cold => "Cold (<15C)",
        (int)AmbientBand.Mild => "Mild (15-25C)",
        (int)AmbientBand.Warm => "Warm (25-35C)",
        (int)AmbientBand.Hot => "Hot (>35C)",
        _ => "Unknown",
    };
}

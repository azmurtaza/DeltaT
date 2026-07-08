using System.Text.Json;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Weather;
using Microsoft.Data.Sqlite;

namespace DeltaT.App.Services;

/// <summary>Dev/demo-only (`--seed=healthy|repaste`, combine with --simulate + --uishot):
/// fills the simulation database with a realistic multi-week thermal history so the
/// dashboard shows a genuine verdict, the Trends graphs show rich non-linear curves,
/// and the Remarks feed reads as a lived-in machine. Nothing here runs on real
/// hardware — it only ever writes to the separate <c>deltat-sim.db</c>.
///
/// The two scenarios share one healthy learning window (epoch 0, 30 days back) so the
/// baseline is identical; they differ only in the recent window:
/// <list type="bullet">
/// <item><b>healthy</b> — recent deltas sit on baseline → "Fresh" (mid-90s score).</item>
/// <item><b>repaste</b> — deltas ramp up over the last three weeks, throttling and
/// heat-soaking faster → a real "Repaste now" on the CPU, "Degraded" on the GPU.</item>
/// </list>
/// Both truths fall out of the ordinary scoring engine — this only supplies the data.</summary>
public static class DemoSeeder
{
    // Must match SimulatedSensorSource exactly: scoring joins recent stats to the
    // live snapshot by component name.
    public const string CpuName = "Simulated Core i5-13420H";
    public const string GpuName = "Simulated RTX 3050 Laptop";
    public const string SsdName = "Simulated NVMe SSD";

    private const int HistoryDays = 30;
    private const int Band = (int)AmbientBand.Mild; // seeded weather stays in one band so comparisons are like-for-like

    public static void Seed(DeltaTDb db, TelemetryRepository repo, SettingsStore settings, bool degraded, DateTimeOffset nowUtc)
    {
        ClearAll(db);

        settings.SetBool(SettingsKeys.FirstRunDone, true);
        settings.SetInt(SettingsKeys.BaselineEpoch, 0);
        settings.SetTimestamp(SettingsKeys.BaselineEpochStart, nowUtc.AddDays(-HistoryDays));
        SeedWeather(settings, nowUtc);

        List<MinuteAccum> minutes = GenerateTimeline(degraded, nowUtc);
        repo.UpsertMinutes(minutes);
        RollupHours(db);

        SeedEvents(repo, nowUtc, degraded);
    }

    // ---------------------------------------------------------------- timeline

    /// <summary>One agg_minute row per (minute, component) across the whole history.
    /// Load follows a daily rhythm (idle nights, working days, gaming evenings, the
    /// odd render/compile burst); temperature is ambient + a load-driven rise + a
    /// scenario-dependent excess that grows toward the present in the repaste case.</summary>
    private static List<MinuteAccum> GenerateTimeline(bool degraded, DateTimeOffset nowUtc)
    {
        var rng = new Random(degraded ? 4242 : 1717);
        DateTimeOffset start = FloorMinute(nowUtc.AddDays(-HistoryDays));
        int totalMinutes = (int)(nowUtc - start).TotalMinutes;

        var rows = new List<MinuteAccum>(totalMinutes * 3);

        // Session state so load is coherent minute-to-minute rather than white noise.
        int gamingLeft = 0, renderLeft = 0, workLeft = 0;
        double cpuLoad = 4, gpuLoad = 1;

        for (int i = 0; i < totalMinutes; i++)
        {
            DateTimeOffset dt = start.AddMinutes(i);
            double daysAgo = (nowUtc - dt).TotalDays;
            double hour = dt.ToLocalTime().TimeOfDay.TotalHours;
            bool newDay = i % 1440 == 0;

            // Decide the day's sessions at local midnight.
            if (newDay)
            {
                gamingLeft = rng.NextDouble() < 0.72 ? rng.Next(75, 185) : 0;   // most evenings
                renderLeft = rng.NextDouble() < 0.55 ? rng.Next(25, 70) : 0;    // a CPU-heavy burst
                workLeft = rng.Next(180, 360);
            }

            // Target loads from the current activity for this hour.
            (double cpuTarget, double gpuTarget) = ActivityTarget(hour, ref gamingLeft, ref renderLeft, ref workLeft, rng);

            // Ease toward the target so ramps read like a real machine, not a step.
            cpuLoad += (cpuTarget - cpuLoad) * 0.35 + (rng.NextDouble() - 0.5) * 2.5;
            gpuLoad += (gpuTarget - gpuLoad) * 0.40 + (rng.NextDouble() - 0.5) * 2.0;
            cpuLoad = Math.Clamp(cpuLoad, 1, 100);
            gpuLoad = Math.Clamp(gpuLoad, 0, 100);

            double ambient = 21.0 + 3.4 * Math.Sin((hour - 15.0) / 24.0 * 2 * Math.PI);
            double ssdLoad = Math.Min(100, cpuLoad * 0.3 + 1);

            double excessCpu = Excess(daysAgo, degraded, cpuPeak: true);
            double excessGpu = Excess(daysAgo, degraded, cpuPeak: false);

            double cpuDelta = 20 + cpuLoad / 100.0 * 42 + excessCpu + Noise(rng, 1.2);
            double gpuDelta = 11 + gpuLoad / 100.0 * 35 + excessGpu + Noise(rng, 1.0);
            double ssdDelta = 18 + cpuLoad / 100.0 * 9 + Noise(rng, 0.5);

            double cpuTemp = Math.Min(100, ambient + cpuDelta);
            double gpuTemp = Math.Min(87, ambient + gpuDelta);
            double ssdTemp = ambient + ssdDelta;

            double hottest = Math.Max(cpuTemp, gpuTemp);
            double fan = hottest < ambient + 14 ? 0 : Math.Min(6000, 1500 + (hottest - (ambient + 14)) * 95);

            bool cpuThrottle = cpuTemp >= 99.5;

            long minute = FloorMinute(dt).ToUnixTimeSeconds();
            rows.Add(MinuteRow(minute, ComponentKind.Cpu, CpuName, cpuLoad, cpuTemp, cpuDelta, fan, cpuThrottle));
            rows.Add(MinuteRow(minute, ComponentKind.GpuDiscrete, GpuName, gpuLoad, gpuTemp, gpuDelta, fan, gpuTemp >= 86.5));
            rows.Add(MinuteRow(minute, ComponentKind.Storage, SsdName, ssdLoad, ssdTemp, ssdDelta, 0, false));
        }

        return rows;
    }

    private static (double Cpu, double Gpu) ActivityTarget(
        double hour, ref int gamingLeft, ref int renderLeft, ref int workLeft, Random rng)
    {
        bool night = hour < 8 || hour >= 24;
        bool evening = hour >= 19 && hour < 24;
        bool daytime = hour >= 9 && hour < 19;

        // Gaming: GPU pinned, CPU moderate. Evenings only.
        if (evening && gamingLeft > 0)
        {
            gamingLeft--;
            return (55 + rng.NextDouble() * 22, 88 + rng.NextDouble() * 10);
        }
        // Render/compile burst: CPU pinned, GPU low. Daytime.
        if (daytime && renderLeft > 0)
        {
            renderLeft--;
            return (90 + rng.NextDouble() * 9, 8 + rng.NextDouble() * 10);
        }
        // Ordinary working load: light-to-medium, bursty.
        if (daytime && workLeft > 0)
        {
            workLeft--;
            double burst = rng.NextDouble() < 0.15 ? 35 : 0;
            return (18 + burst + rng.NextDouble() * 22, 6 + rng.NextDouble() * 12);
        }
        // Idle / away.
        _ = night;
        return (3 + rng.NextDouble() * 5, 1 + rng.NextDouble() * 3);
    }

    /// <summary>Degradation curve: flat through the learning window, then a curved
    /// climb toward the present (steeper near the end) — the visible "paste drying
    /// out" trend. Healthy machines drift only a degree or two over a month.</summary>
    private static double Excess(double daysAgo, bool degraded, bool cpuPeak)
    {
        if (daysAgo >= 22)
            return 0;
        double x = (22 - daysAgo) / 22.0; // 0 at 22 days ago → 1 now
        double peak = degraded ? (cpuPeak ? 14.0 : 9.0) : (cpuPeak ? 1.6 : 1.0);
        return peak * (degraded ? Math.Pow(x, 1.6) : x);
    }

    private static MinuteAccum MinuteRow(
        long minute, ComponentKind kind, string name, double load, double temp, double delta, double fan, bool throttle)
    {
        const int n = 30; // ~one 2-second sample every 2s for a minute
        double spread = 1.1 + load / 100.0 * 1.4;
        bool hasFan = fan >= 300;
        return new MinuteAccum
        {
            Minute = minute,
            Kind = kind,
            Name = name,
            Bucket = LoadBuckets.FromPercent(load),
            Band = Band,
            OnAc = true,
            N = n,
            TempSum = temp * n,
            TempMin = temp - spread,
            TempMax = temp + spread,
            LoadSum = load * n,
            DeltaSum = delta * n,
            DeltaN = n,
            FanSum = hasFan ? fan * n : 0,
            FanN = hasFan ? n : 0,
            ThrottleN = throttle ? n : 0,
        };
    }

    /// <summary>Rebuild every hour bucket from the minutes in one pass.</summary>
    private static void RollupHours(DeltaTDb db)
    {
        using SqliteConnection conn = db.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agg_hour(hour,kind,name,bucket,band,on_ac,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n)
            SELECT (minute/3600)*3600 AS hour, kind, name, bucket, band, on_ac,
                   SUM(n), SUM(temp_sum), MIN(temp_min), MAX(temp_max), SUM(load_sum),
                   SUM(delta_sum), SUM(delta_n), SUM(fan_sum), SUM(fan_n), SUM(throttle_n)
            FROM agg_minute
            GROUP BY hour, kind, name, bucket, band, on_ac;
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ events

    private static void SeedEvents(TelemetryRepository repo, DateTimeOffset now, bool degraded)
    {
        long At(double daysAgo) => now.AddDays(-daysAgo).ToUnixTimeSeconds();
        void Ev(double daysAgo, string type, ComponentKind? kind, int sev, string msg, string? data = null) =>
            repo.InsertEvent(At(daysAgo), type, kind?.ToString(), null, sev, msg, data);

        // Shared story: repasted a month ago, baseline learned, fingerprint captured.
        Ev(30, "repaste", null, 1, "Thermal paste replaced. New baseline learning started.");
        Ev(29.6, "fingerprint", null, 1, "Fingerprint test complete - CPU and GPU load-response curves captured.");
        Ev(25, "remark", null, 1, "Baseline locked in. From now on, DeltaT compares this machine against itself - the only comparison that means anything.");

        // Baseline-window heat-soak references (the "healthy" soak rate).
        foreach (double d in new[] { 28.0, 26.5, 24.0 })
        {
            Ev(d, "soak", ComponentKind.Cpu, 0, "Heat-soak measured on load onset.", "{\"rate\":2.0}");
            Ev(d, "soak", ComponentKind.GpuDiscrete, 0, "Heat-soak measured on load onset.", "{\"rate\":1.8}");
        }

        if (!degraded)
        {
            Ev(19, "remark", null, 0, "Weekly check-in: deltas on baseline, no throttling, nothing drifting. The paste is earning its keep.");
            Ev(12, "fingerprint", null, 1, "Fingerprint re-run - response curves unchanged since the last check.");
            Ev(6, "remark", ComponentKind.GpuDiscrete, 1, "New record: GPU touched 71° under load - the hottest DeltaT has seen it, still well inside spec.");
            Ev(1.5, "remark", null, 0, "Weekly check-in: deltas on baseline, no throttling, nothing drifting. The paste is earning its keep.");
            foreach (double d in new[] { 5.0, 3.0, 1.0 })
            {
                Ev(d, "soak", ComponentKind.Cpu, 0, "Heat-soak measured on load onset.", "{\"rate\":2.0}");
                Ev(d, "soak", ComponentKind.GpuDiscrete, 0, "Heat-soak measured on load onset.", "{\"rate\":1.8}");
            }
            return;
        }

        // Repaste story: the slide, week by week.
        Ev(13, "remark", ComponentKind.Cpu, 1, "CPU is running 5.4° hotter than a week ago at similar load and weather. Watching.");
        Ev(8, "remark", ComponentKind.Cpu, 2, "CPU paste score slid 11 points this week. One bad week isn't a verdict, but the trend has DeltaT's attention.");

        // Recent heat-soaks are markedly faster — a paste signature.
        foreach ((double d, double cpu, double gpu) in new[] { (5.5, 2.8, 2.4), (3.0, 3.2, 2.5), (1.2, 3.4, 2.7), (0.3, 3.5, 2.6) })
        {
            Ev(d, "soak", ComponentKind.Cpu, 0, "Heat-soak measured on load onset.", $"{{\"rate\":{cpu.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
            Ev(d, "soak", ComponentKind.GpuDiscrete, 0, "Heat-soak measured on load onset.", $"{{\"rate\":{gpu.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        }

        // Throttle kisses, concentrated in the last few days (some in the last 24 h
        // so they land as markers on the 24 h chart).
        double[] cpuThrottles = { 5.4, 4.6, 3.7, 3.1, 2.4, 1.9, 1.3, 0.9, 0.6, 0.4, 0.15 };
        foreach (double d in cpuThrottles)
            Ev(d, "throttle", ComponentKind.Cpu, 2, "CPU hit its thermal limit and pulled clocks back to stay under TjMax.");
        foreach (double d in new[] { 3.4, 1.1, 0.5 })
            Ev(d, "throttle", ComponentKind.GpuDiscrete, 2, "GPU reached its thermal cap and trimmed the boost clock.");

        Ev(2.2, "remark", ComponentKind.GpuDiscrete, 2, "GPU paste is degrading - the delta over baseline keeps widening. Start planning a repaste; no emergency, but the direction is clear.");
        Ev(0.35, "remark", ComponentKind.Cpu, 3, "CPU paste has crossed into repaste territory. DeltaT's honest read: it's time - fresh paste should claw back several degrees.");
    }

    // ------------------------------------------------------------------ helpers

    private static void SeedWeather(SettingsStore settings, DateTimeOffset now)
    {
        var loc = new GeoLocation(33.6844, 73.0479, "Islamabad", "PK", "manual");
        settings.Set(SettingsKeys.LocationJson, JsonSerializer.Serialize(loc));
        settings.SetDouble(SettingsKeys.LastAmbientC, 21.4);
        settings.SetTimestamp(SettingsKeys.LastAmbientFetched, now.AddMinutes(-20));
    }

    private static void ClearAll(DeltaTDb db)
    {
        using SqliteConnection conn = db.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM samples; DELETE FROM agg_minute; DELETE FROM agg_hour; DELETE FROM events; DELETE FROM baseline;";
        cmd.ExecuteNonQuery();
    }

    private static double Noise(Random rng, double scale) => (rng.NextDouble() - 0.5) * scale;

    private static DateTimeOffset FloorMinute(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
}

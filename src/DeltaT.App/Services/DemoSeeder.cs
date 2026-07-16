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
/// The scenarios share one healthy learning window (epoch 0, 30 days back) so the
/// baseline is identical; they differ only in the recent window:
/// <list type="bullet">
/// <item><b>healthy</b> — recent deltas sit on baseline → "Fresh" (mid-90s score).</item>
/// <item><b>repaste</b> — deltas ramp up over the last three weeks, throttling and
/// heat-soaking faster → a real "Critical" on the CPU, "Degraded" on the GPU.</item>
/// <item><b>overclock</b> — cooling never changes, but the machine's power does: the
/// baseline is learned with CPU boost OFF, then boost switches on 10 days ago (before
/// the 7-day recent window, so every recent reading is boosted). Rise, soak/cooldown
/// rates and the GPU hotspot gap all ride the watts (ΔT ≈ P × R_thermal), so the die
/// runs visibly hotter while the thermal resistance is unchanged. The engine should
/// normalize it away: healthy score, POWER reading +NN%, PowerConfig reassurance.</item>
/// </list>
/// Every truth falls out of the ordinary scoring engine — this only supplies the data.</summary>
public static class DemoSeeder
{
    // Must match SimulatedSensorSource exactly: scoring joins recent stats to the
    // live snapshot by component name.
    public const string CpuName = "Simulated Core i5-13420H";
    public const string GpuName = "Simulated RTX 3050 Laptop";
    public const string SsdName = "Simulated NVMe SSD";

    private const int HistoryDays = 30;
    private const int Band = (int)AmbientBand.Mild; // seeded weather stays in one band so comparisons are like-for-like

    /// <summary>Boost-off epochs draw (and dissipate) this fraction of stock power. The
    /// recent window runs at 1.0, so the learned baseline sits ~39% below it.</summary>
    private const double BoostOffScale = 0.72;

    /// <summary>Days ago the boost toggle flips in the overclock scenario. Must sit
    /// outside the scoring engine's 7-day recent window so every recent reading is
    /// boosted (a mixed window would measure the blend, not the regime).</summary>
    private const double BoostOnDaysAgo = 10.0;

    /// <param name="provisional">When true, start the epoch just two days ago and leave
    /// the baseline unlocked, so the score is still calibrating but far enough along to
    /// show a provisional estimate — for screenshotting the pre-lock UI.</param>
    public static void Seed(DeltaTDb db, TelemetryRepository repo, SettingsStore settings, bool degraded, DateTimeOffset nowUtc, bool provisional = false, bool overclock = false)
    {
        ClearAll(db);

        settings.SetBool(SettingsKeys.FirstRunDone, true);
        settings.SetInt(SettingsKeys.BaselineEpoch, 0);
        if (provisional)
        {
            // Young epoch, not yet cured: still calibrating, but with real load to estimate from.
            settings.SetTimestamp(SettingsKeys.BaselineEpochStart, nowUtc.AddDays(-2.2));
            settings.Set(SettingsKeys.BaselineLockedUtc, "");
        }
        else
        {
            settings.SetTimestamp(SettingsKeys.BaselineEpochStart, nowUtc.AddDays(-HistoryDays));
            // Freeze the learning window in the clean early stretch (degradation only starts
            // ~22 days ago), so the baseline is the healthy reference the recent weeks are
            // judged against — matching how a continuously-running install would have locked.
            settings.SetTimestamp(SettingsKeys.BaselineLockedUtc, nowUtc.AddDays(-HistoryDays + 7));
        }
        SeedWeather(settings, nowUtc);

        List<MinuteAccum> minutes = GenerateTimeline(degraded, overclock, nowUtc);
        repo.UpsertMinutes(minutes);
        RollupHours(db);

        SeedEvents(repo, nowUtc, degraded, overclock);
    }

    // ---------------------------------------------------------------- timeline

    /// <summary>One agg_minute row per (minute, component) across the whole history.
    /// Load follows a daily rhythm (idle nights, working days, gaming evenings, the
    /// odd render/compile burst); temperature is ambient + a load-driven rise + a
    /// scenario-dependent excess that grows toward the present in the repaste case.</summary>
    private static List<MinuteAccum> GenerateTimeline(bool degraded, bool overclock, DateTimeOffset nowUtc)
    {
        var rng = new Random(degraded ? 4242 : overclock ? 9091 : 1717);
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

            // ΔT ≈ P × R_thermal: with the cooler unchanged, the whole rise scales with
            // the watts. The excess (a thermal-resistance fault) is added after, since a
            // degraded joint is a worse R, not a power change.
            double pf = PowerFactor(daysAgo, overclock);

            double cpuNoise = Noise(rng, 1.2), gpuNoise = Noise(rng, 1.0);
            double cpuStock = 20 + cpuLoad / 100.0 * 42 + excessCpu + cpuNoise;
            double gpuStock = 11 + gpuLoad / 100.0 * 35 + excessGpu + gpuNoise;

            double cpuDelta = (20 + cpuLoad / 100.0 * 42) * pf + excessCpu + cpuNoise;
            double gpuDelta = (11 + gpuLoad / 100.0 * 35) * pf + excessGpu + gpuNoise;
            double ssdDelta = 18 + cpuLoad / 100.0 * 9 + Noise(rng, 0.5);

            double cpuTemp = Math.Min(100, ambient + cpuDelta);
            double gpuTemp = Math.Min(87, ambient + gpuDelta);
            double ssdTemp = ambient + ssdDelta;

            // The fan answers temperature, but its response to a POWER change has to be
            // the plausible one: DetectionBenchmark (the accuracy authority) models a
            // temperature-targeting curve as rpm × (1 + 0.4 × (P/P_baseline − 1)), and the
            // engine allows up to a 0.6 coupling before it calls a fan strained. Driving
            // this curve straight off the boosted temperature instead over-answers the
            // watts and reads as "fans working harder", which is the dust corroborator, so
            // a pure power change would invent an airflow fault. Curve off the stock-power
            // temperature, then apply the coupling. At pf = 1 this is identical to before,
            // and a dust/paste excess still drives the fan up (that tell is real).
            double hottestStock = Math.Max(Math.Min(100, ambient + cpuStock), Math.Min(87, ambient + gpuStock));
            double fanStock = hottestStock < ambient + 14 ? 0 : 1500 + (hottestStock - (ambient + 14)) * 95;
            double fan = Math.Min(6000, fanStock * (1 + 0.4 * (pf - 1)));

            bool cpuThrottle = cpuTemp >= 99.5;

            long minute = FloorMinute(dt).ToUnixTimeSeconds();
            rows.Add(MinuteRow(minute, ComponentKind.Cpu, CpuName, cpuLoad, cpuTemp, cpuDelta, fan, cpuThrottle, pf));
            rows.Add(MinuteRow(minute, ComponentKind.GpuDiscrete, GpuName, gpuLoad, gpuTemp, gpuDelta, fan, gpuTemp >= 86.5, pf));
            rows.Add(MinuteRow(minute, ComponentKind.Storage, SsdName, ssdLoad, ssdTemp, ssdDelta, 0, false, pf));
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
    /// <summary>Power draw relative to stock. Only the overclock scenario moves it: the
    /// learning window runs boost-off, then boost flips on before the recent window opens.
    /// A step, not a ramp, because that is what flipping a boost toggle looks like.</summary>
    private static double PowerFactor(double daysAgo, bool overclock) =>
        overclock && daysAgo >= BoostOnDaysAgo ? BoostOffScale : 1.0;

    private static double Excess(double daysAgo, bool degraded, bool cpuPeak)
    {
        if (daysAgo >= 22)
            return 0;
        double x = (22 - daysAgo) / 22.0; // 0 at 22 days ago → 1 now
        double peak = degraded ? (cpuPeak ? 14.0 : 9.0) : (cpuPeak ? 1.6 : 1.0);
        return peak * (degraded ? Math.Pow(x, 1.6) : x);
    }

    private static MinuteAccum MinuteRow(
        long minute, ComponentKind kind, string name, double load, double temp, double delta, double fan, bool throttle,
        double powerFactor = 1.0)
    {
        const int n = 30; // ~one 2-second sample every 2s for a minute
        double spread = 1.1 + load / 100.0 * 1.4;
        bool hasFan = fan >= 300;
        // Package power tracks load (matching SimulatedSensorSource), and the GPU
        // carries a steady healthy hotspot gap, so the demo's health matrix shows a
        // real POWER state and MOUNT reading instead of dashes.
        double power = kind switch
        {
            ComponentKind.Cpu => (8 + load / 100.0 * 38) * powerFactor,
            ComponentKind.GpuDiscrete => (6 + load / 100.0 * 64) * powerFactor,
            _ => 0,
        };
        // The hotspot-to-edge gap is heat flux × internal resistance, so it rides the
        // watts exactly like the rise does. A gap that ignored power would let the engine
        // read a boost-off baseline as a widening mount.
        double gap = kind == ComponentKind.GpuDiscrete ? (7.5 + load / 100.0 * 2.5) * powerFactor : 0;
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
            GapSum = gap > 0 ? gap * n : 0,
            GapN = gap > 0 ? n : 0,
            PowerSum = power > 0 ? power * n : 0,
            PowerN = power > 0 ? n : 0,
        };
    }

    /// <summary>Rebuild every hour bucket from the minutes in one pass.</summary>
    private static void RollupHours(DeltaTDb db)
    {
        using SqliteConnection conn = db.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agg_hour(hour,kind,name,bucket,band,on_ac,n,temp_sum,temp_min,temp_max,load_sum,delta_sum,delta_n,fan_sum,fan_n,throttle_n,gap_sum,gap_n,power_sum,power_n)
            SELECT (minute/3600)*3600 AS hour, kind, name, bucket, band, on_ac,
                   SUM(n), SUM(temp_sum), MIN(temp_min), MAX(temp_max), SUM(load_sum),
                   SUM(delta_sum), SUM(delta_n), SUM(fan_sum), SUM(fan_n), SUM(throttle_n),
                   SUM(gap_sum), SUM(gap_n), SUM(power_sum), SUM(power_n)
            FROM agg_minute
            GROUP BY hour, kind, name, bucket, band, on_ac;
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ events

    private static void SeedEvents(TelemetryRepository repo, DateTimeOffset now, bool degraded, bool overclock = false)
    {
        long At(double daysAgo) => now.AddDays(-daysAgo).ToUnixTimeSeconds();
        void Ev(double daysAgo, string type, ComponentKind? kind, int sev, string msg, string? data = null) =>
            repo.InsertEvent(At(daysAgo), type, kind?.ToString(), null, sev, msg, data);
        // dT/dt ≈ P/C, so a soak rate measured in a boost-off epoch is slower in exact
        // proportion to the missing watts. Tag each rate with the factor in force that day.
        void Soak(double daysAgo, double cpuRate, double gpuRate)
        {
            double pf = PowerFactor(daysAgo, overclock);
            string R(double v) => (v * pf).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            Ev(daysAgo, "soak", ComponentKind.Cpu, 0, "Heat-soak measured on load onset.", $"{{\"rate\":{R(cpuRate)}}}");
            Ev(daysAgo, "soak", ComponentKind.GpuDiscrete, 0, "Heat-soak measured on load onset.", $"{{\"rate\":{R(gpuRate)}}}");
        }

        // Shared story: repasted a month ago, baseline learned, fingerprint captured.
        Ev(30, "repaste", null, 1, "Thermal paste replaced. New baseline learning started.");
        Ev(29.6, "fingerprint", null, 1, "Fingerprint test complete. CPU and GPU load-response curves captured.");
        Ev(25, "remark", null, 1, "Baseline locked in. From now on, DeltaT compares this machine against itself, the only comparison that means anything.");

        // Baseline-window heat-soak references (the "healthy" soak rate).
        foreach (double d in new[] { 28.0, 26.5, 24.0 })
            Soak(d, 2.0, 1.8);

        if (overclock)
        {
            Ev(BoostOnDaysAgo, "remark", null, 1, "CPU package power jumped about 39% at the same load. Nothing about the cooling changed, so DeltaT is judging every comparison at equal wattage from here.");
            Ev(4, "remark", ComponentKind.Cpu, 0, "Weekly check-in: the machine runs hotter than the baseline, but only because it draws more power. Thermal resistance is unchanged.");
            foreach (double d in new[] { 6.0, 3.0, 1.0 })
                Soak(d, 2.0, 1.8);
            return;
        }

        if (!degraded)
        {
            Ev(19, "remark", null, 0, "Weekly check-in: deltas on baseline, no throttling, nothing drifting. Cooling is earning its keep.");
            Ev(12, "fingerprint", null, 1, "Fingerprint re-run. Response curves unchanged since the last check.");
            Ev(6, "remark", ComponentKind.GpuDiscrete, 1, "New record: GPU touched 71° under load, the hottest DeltaT has seen it, still well inside spec.");
            Ev(1.5, "remark", null, 0, "Weekly check-in: deltas on baseline, no throttling, nothing drifting. Cooling is earning its keep.");
            foreach (double d in new[] { 5.0, 3.0, 1.0 })
                Soak(d, 2.0, 1.8);
            return;
        }

        // Repaste story: the slide, week by week.
        Ev(13, "remark", ComponentKind.Cpu, 1, "CPU is running 5.4° hotter than a week ago at similar load and weather. Watching.");
        Ev(8, "remark", ComponentKind.Cpu, 2, "CPU thermal score slid 11 points this week. One bad week isn't a verdict, but the trend has DeltaT's attention.");

        // Recent heat-soaks are markedly faster — a paste signature.
        foreach ((double d, double cpu, double gpu) in new[] { (5.5, 2.8, 2.4), (3.0, 3.2, 2.5), (1.2, 3.4, 2.7), (0.3, 3.5, 2.6) })
            Soak(d, cpu, gpu);

        // Throttle kisses, concentrated in the last few days (some in the last 24 h
        // so they land as markers on the 24 h chart).
        double[] cpuThrottles = { 5.4, 4.6, 3.7, 3.1, 2.4, 1.9, 1.3, 0.9, 0.6, 0.4, 0.15 };
        foreach (double d in cpuThrottles)
            Ev(d, "throttle", ComponentKind.Cpu, 2, "CPU hit its thermal limit and pulled clocks back to stay under TjMax.");
        foreach (double d in new[] { 3.4, 1.1, 0.5 })
            Ev(d, "throttle", ComponentKind.GpuDiscrete, 2, "GPU reached its thermal cap and trimmed the boost clock.");

        Ev(2.2, "remark", ComponentKind.GpuDiscrete, 2, "GPU cooling is degrading. The delta over baseline keeps widening. Start planning a repaste; no emergency, but the direction is clear.");
        Ev(0.35, "remark", ComponentKind.Cpu, 3, "CPU thermal score has crossed into act-now territory. The evidence points at dust or blocked airflow, so start with a clean-out; that should claw back several degrees.");
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

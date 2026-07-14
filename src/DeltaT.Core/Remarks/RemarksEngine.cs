using DeltaT.Core.Diagnostics;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;

namespace DeltaT.Core.Remarks;

/// <summary>A fingerprint that completed since the last evaluation, plus its
/// weather-corrected sustained delta vs the previous same-target run (null = first).</summary>
public sealed record FingerprintEcho(ComponentKind Kind, FingerprintResult Result, double? DeltaVsPrevious);

public enum RemarkSeverity
{
    Info = 0,     // ticker only
    Notice = 1,   // ticker, highlighted
    Warning = 2,  // ticker + toast (if enabled)
    Alert = 3,    // ticker + toast, insistent
}

public sealed record Remark(string RuleId, DateTimeOffset TimestampUtc, RemarkSeverity Severity, string Text, ComponentKind? Kind);

/// <summary>Everything a rule may look at. Assembled by the app once a minute;
/// nullable fields mean "unknown right now" and rules must cope.</summary>
public sealed record RemarkContext(
    DateTimeOffset NowUtc,
    bool FirstEver,
    SensorSnapshot? Latest,
    // Per paste-component 10-minute vs previous-hour comparison at similar load.
    IReadOnlyDictionary<ComponentKind, (double Recent10Avg, double PrevHourAvg, bool SimilarLoad)>? ShortTrend,
    double? AmbientC,
    bool AmbientStale,
    string? City,
    IReadOnlyDictionary<ComponentKind, ComponentScore>? Scores,
    IReadOnlyDictionary<ComponentKind, int>? ScoreDropThisWeek,
    int ThrottleEventsLastHour,
    IReadOnlyDictionary<ComponentKind, double>? AllTimeMax,
    bool OnAcPower,
    bool BaselineReady,
    bool BaselineJustBecameReady,
    double CalibrationProgress,
    int LearningDay,
    // Baseline gone stale after a long dormancy — prompt a recalibration.
    bool BaselineStale = false,
    int DormantDays = 0,
    // Per paste-component score now vs ~30 days ago, for the monthly readout.
    IReadOnlyDictionary<ComponentKind, (int LastMonth, int Now)>? ScoreVsLastMonth = null,
    // A just-locked repaste verdict to announce once (better/worse/unchanged).
    RepasteReport? RepasteOutcome = null,
    // While calibrating: the binding constraint on baseline confidence.
    string CalibrationConstraint = "",
    // Local wall-clock hour (0-23), supplied by the host (timezone is a display
    // concern, so Core never asks the clock itself); -1 = unknown.
    int LocalHour = -1,
    // Throttle events over the last 30 days; null = host didn't ask the repo.
    int? ThrottleEventsLast30Days = null,
    // Days since DeltaT first ever ran on this machine. Unlike LearningDay this
    // survives recalibration - it measures the relationship, not the epoch.
    int DaysTogether = 0,
    // Weather city changed since the last evaluation (a real move or a manual pick).
    bool CityChanged = false,
    // A fingerprint completed since the last evaluation - announced once.
    FingerprintEcho? Fingerprint = null,
    // Per paste-component long-term drift/step verdict against the frozen baseline.
    IReadOnlyDictionary<ComponentKind, TrendResult>? Trends = null,
    // Days since the last fingerprint of ANY kind completed; null = never run one.
    // Feeds the periodic "time for a controlled check-up" nudge.
    int? DaysSinceLastFingerprint = null);

/// <summary>DeltaT's voice: dry, precise, occasionally warm. Rules fire against a
/// context snapshot and are rate-limited per rule (and per component where it
/// matters) so the feed stays observant, never spammy.</summary>
public sealed class RemarksEngine
{
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new();
    private readonly List<Rule> _rules;

    private sealed record Rule(string Id, TimeSpan Cooldown, Func<RemarkContext, IEnumerable<Remark>> Evaluate);

    public RemarksEngine()
    {
        _rules = BuildRules();
    }

    /// <summary>Cooldowns must survive restarts, or every launch re-fires the
    /// daily remarks. The host persists this blob and hands it back on startup.</summary>
    public string ExportCooldownState()
    {
        lock (_lastFired)
            return System.Text.Json.JsonSerializer.Serialize(
                _lastFired.ToDictionary(kv => kv.Key, kv => kv.Value.ToUnixTimeSeconds()));
    }

    public void ImportCooldownState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (state is null) return;
            lock (_lastFired)
                foreach ((string key, long ts) in state)
                    _lastFired[key] = DateTimeOffset.FromUnixTimeSeconds(ts);
        }
        catch { /* corrupted state — start fresh */ }
    }

    public IReadOnlyList<Remark> Evaluate(RemarkContext ctx)
    {
        var fired = new List<Remark>();
        foreach (Rule rule in _rules)
        {
            IEnumerable<Remark> produced;
            try { produced = rule.Evaluate(ctx); }
            catch { continue; } // a broken rule must never take the engine down

            foreach (Remark remark in produced)
            {
                string key = remark.Kind is { } k ? $"{rule.Id}:{k}" : rule.Id;
                lock (_lastFired)
                {
                    if (_lastFired.TryGetValue(key, out DateTimeOffset last) && ctx.NowUtc - last < rule.Cooldown)
                        continue;
                    _lastFired[key] = ctx.NowUtc;
                }
                fired.Add(remark);
            }
        }
        return fired;
    }

    private static List<Rule> BuildRules() => new()
    {
        new Rule("hello", TimeSpan.MaxValue, ctx =>
        {
            if (!ctx.FirstEver || ctx.Latest is not { } snap)
                return Enumerable.Empty<Remark>();
            var cpu = snap.Find(ComponentKind.Cpu)?.TemperatureC;
            var gpu = snap.Find(ComponentKind.GpuDiscrete)?.TemperatureC;
            string temps = string.Join(", ", new[]
            {
                cpu is { } c ? $"CPU {c:0}°" : null,
                gpu is { } g ? $"GPU {g:0}°" : null,
            }.Where(s => s is not null));
            string outside = ctx.AmbientC is { } a ? $" It's {a:0}° outside{(ctx.City is { } city ? $" in {city}" : "")}." : "";
            return One("hello", ctx, RemarkSeverity.Notice,
                $"First readings are in: {temps}.{outside} DeltaT is watching now; give it about a week to learn what normal looks like here.");
        }),

        new Rule("temp-climbing", TimeSpan.FromHours(2), ctx =>
        {
            if (ctx.ShortTrend is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, (double recent, double prev, bool similar)) in ctx.ShortTrend)
            {
                double diff = recent - prev;
                if (similar && diff >= 5)
                    list.Add(new Remark("temp-climbing", ctx.NowUtc, RemarkSeverity.Notice,
                        $"{kind.Label()} is running {diff:0.#}° hotter than it was an hour ago at similar load. Watching.", kind));
            }
            return list;
        }),

        new Rule("throttle-hour", TimeSpan.FromHours(1), ctx =>
            ctx.ThrottleEventsLastHour >= 1
                ? One("throttle-hour", ctx, ctx.ThrottleEventsLastHour >= 3 ? RemarkSeverity.Alert : RemarkSeverity.Warning,
                    ctx.ThrottleEventsLastHour == 1
                        ? "Thermal throttle: the silicon touched its limit and pulled clocks back. Once is a data point."
                        : $"Thermal throttling {ctx.ThrottleEventsLastHour}× in the last hour. The cooling isn't keeping up with the load.")
                : Enumerable.Empty<Remark>()),

        new Rule("record-high", TimeSpan.FromHours(6), ctx =>
        {
            // "Hottest ever seen" is meaningless on day one — everything is a record.
            if (ctx.LearningDay < 2 && !ctx.BaselineReady) return Enumerable.Empty<Remark>();
            if (ctx.Latest is not { } snap || ctx.AllTimeMax is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach (ComponentKind kind in new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete })
            {
                if (snap.Find(kind) is { TemperatureC: { } t }
                    && ctx.AllTimeMax.TryGetValue(kind, out double max) && max > 40 && t > max)
                    list.Add(new Remark("record-high", ctx.NowUtc, RemarkSeverity.Notice,
                        $"New record: {kind.Label()} just hit {t:0}°, the hottest DeltaT has ever seen it.", kind));
            }
            return list;
        }),

        new Rule("weather-hot", TimeSpan.FromHours(8), ctx =>
            ctx.AmbientC is { } a && a >= 38
                ? One("weather-hot", ctx, RemarkSeverity.Info,
                    $"It's {a:0}° outside. If today's numbers look scary, blame the sun before the paste. DeltaT corrects for it.")
                : Enumerable.Empty<Remark>()),

        new Rule("weather-cold", TimeSpan.FromHours(12), ctx =>
            ctx.AmbientC is { } a && a <= 8
                ? One("weather-cold", ctx, RemarkSeverity.Info,
                    $"{a:0}° outside, a free cooling day. Don't let today's pretty temps fool the long-term picture; DeltaT won't.")
                : Enumerable.Empty<Remark>()),

        new Rule("weather-stale", TimeSpan.FromHours(12), ctx =>
            ctx.AmbientStale
                ? One("weather-stale", ctx, RemarkSeverity.Info,
                    "Can't reach the weather service, so DeltaT is using the last known outside temperature until it's back.")
                : Enumerable.Empty<Remark>()),

        new Rule("on-battery", TimeSpan.FromHours(4), ctx =>
            !ctx.OnAcPower && ctx.Latest?.Find(ComponentKind.Cpu)?.Bucket is LoadBucket.Heavy or LoadBucket.Max
                ? One("on-battery", ctx, RemarkSeverity.Info,
                    "Heavy load on battery, so these readings don't count toward paste scoring (battery power limits change the physics).")
                : Enumerable.Empty<Remark>()),

        new Rule("learning-daily", TimeSpan.FromHours(20), ctx =>
            !ctx.BaselineReady && ctx.LearningDay >= 1
                ? One("learning-daily", ctx, RemarkSeverity.Info,
                    $"Learning day {ctx.LearningDay}: baseline is {ctx.CalibrationProgress * 100:0}% calibrated"
                    + (string.IsNullOrWhiteSpace(ctx.CalibrationConstraint)
                        ? ". Use the machine normally. Games and heavy work teach DeltaT the most."
                        : $": {ctx.CalibrationConstraint}."))
                : Enumerable.Empty<Remark>()),

        new Rule("baseline-ready", TimeSpan.MaxValue, ctx =>
            ctx.BaselineJustBecameReady
                ? One("baseline-ready", ctx, RemarkSeverity.Notice,
                    "Baseline locked in. From now on, DeltaT compares this machine against itself, the only comparison that means anything.")
                : Enumerable.Empty<Remark>()),

        // The repaste verdict is one-shot (host consumes it once), so no real cooldown needed.
        new Rule("repaste-outcome", TimeSpan.Zero, ctx =>
        {
            if (ctx.RepasteOutcome is not { } r) return Enumerable.Empty<Remark>();
            RemarkSeverity sev = r.Verdict switch
            {
                RepasteVerdict.Worse => RemarkSeverity.Warning,   // bad application → toast
                RepasteVerdict.Improved => RemarkSeverity.Notice,
                RepasteVerdict.Unchanged => RemarkSeverity.Notice,
                _ => RemarkSeverity.Info,
            };
            return One("repaste-outcome", ctx, sev, r.Text);
        }),

        // Long dormancy: the baseline may no longer describe the machine. Warn (toast)
        // and point at recalibration. Fires at most every few days while stale.
        new Rule("baseline-stale", TimeSpan.FromDays(3), ctx =>
            ctx.BaselineStale
                ? One("baseline-stale", ctx, RemarkSeverity.Warning,
                    $"DeltaT hadn't run in about {StaleGap(ctx.DormantDays)}. A lot can change while it's off (dust, a moved fan, a cleaning, even a repaste), so the current score is judging against an old, unverified baseline. Recalibrate (Settings) to trust it again.")
                : Enumerable.Empty<Remark>()),

        new Rule("monthly-report", TimeSpan.FromDays(28), ctx =>
        {
            if (ctx.ScoreVsLastMonth is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, (int lastMonth, int now)) in ctx.ScoreVsLastMonth)
            {
                int drop = lastMonth - now;
                string line = drop >= 6
                    ? $"Monthly check: {kind.Label()} thermal score is {now}, down {drop} from last month ({lastMonth}). The drift is real, not weather. DeltaT already corrected for that."
                    : drop <= -4
                        ? $"Monthly check: {kind.Label()} thermal score is {now}, up {-drop} from last month ({lastMonth}). Whatever changed, the cooling is happier."
                        : $"Monthly check: {kind.Label()} thermal score is {now}, about the same as last month ({lastMonth}). Holding steady.";
                list.Add(new Remark("monthly-report", ctx.NowUtc, RemarkSeverity.Info, line, kind));
            }
            return list;
        }),

        new Rule("score-drop", TimeSpan.FromHours(24), ctx =>
        {
            if (ctx.ScoreDropThisWeek is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, int drop) in ctx.ScoreDropThisWeek)
            {
                if (drop >= 8)
                    list.Add(new Remark("score-drop", ctx.NowUtc, RemarkSeverity.Warning,
                        $"{kind.Label()} thermal score slid {drop} points this week. One bad week isn't a verdict, but the trend has DeltaT's attention.", kind));
            }
            return list;
        }),

        new Rule("verdict-repaste", TimeSpan.FromHours(24), ctx =>
        {
            if (ctx.Scores is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, ComponentScore score) in ctx.Scores)
            {
                // The action names the DIAGNOSED cause, not reflexively the paste: a score
                // of 25 driven by dust wants a clean-out, not a repaste.
                if (score.Verdict == Verdict.RepasteNow)
                    list.Add(new Remark("verdict-repaste", ctx.NowUtc, RemarkSeverity.Alert,
                        $"{kind.Label()} thermal score is {score.Value}. DeltaT's honest read: it's time. {VerdictAction(score, urgent: true)}", kind));
                else if (score.Verdict == Verdict.Degraded)
                    list.Add(new Remark("verdict-repaste", ctx.NowUtc, RemarkSeverity.Warning,
                        $"{kind.Label()} cooling is degrading (score {score.Value}). {VerdictAction(score, urgent: false)} No emergency, but the direction is clear.", kind));
            }
            return list;
        }),

        // Long-term drift: the per-bucket score sees "hotter than baseline now"; this sees
        // the machine trending hotter week over week and projects roughly how long until it
        // matters. Weather-corrected upstream, so it's paste/dust drift, not a season.
        new Rule("paste-drift", TimeSpan.FromDays(7), ctx =>
        {
            if (ctx.Trends is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, TrendResult tr) in ctx.Trends)
            {
                if (!tr.HasTrend || tr.SlopePerMonthC <= 0 || tr.CurrentExcessC < 1.5)
                    continue;
                string eta = tr.MonthsToConcern is { } m && m > 0
                    ? m < 1.5 ? " At this rate it reaches a repaste-worthy level within a month."
                              : $" At this rate it's about {m:0} months from a repaste-worthy level."
                    : "";
                list.Add(new Remark("paste-drift", ctx.NowUtc, RemarkSeverity.Warning,
                    $"{kind.Label()} has been creeping up about {tr.SlopePerMonthC:0.#}° a month over the last {tr.Weeks} weeks at matched load and weather, a slow drift the weekly numbers hide.{eta} Worth keeping an eye on.", kind));
            }
            return list;
        }),

        // Step change: a discrete jump in the weather-corrected level, not a slow ramp -
        // the signature of an event (a knocked cooler, a fan that started failing, a moved
        // case) rather than paste aging. DeltaT can't name the cause, only the date to look at.
        new Rule("thermal-step", TimeSpan.FromDays(7), ctx =>
        {
            if (ctx.Trends is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, TrendResult tr) in ctx.Trends)
            {
                if (tr.Step is not { } step || step.JumpC < TrendAnalyzer.MinStepC)
                    continue;
                int weeksAgo = Math.Max(1, (int)Math.Round((ctx.NowUtc.ToUnixTimeSeconds() - step.AtWeekStartTs) / 604800.0));
                list.Add(new Remark("thermal-step", ctx.NowUtc, RemarkSeverity.Warning,
                    $"{kind.Label()} stepped up about {step.JumpC:0.#}° at matched load and weather around {weeksAgo} week{(weeksAgo == 1 ? "" : "s")} ago and stayed there. That's a sudden change, not gradual wear. Think back to anything around then: a bump to the cooler, a fan, a case move, new paste. DeltaT flags the timing; the cause is yours to spot.", kind));
            }
            return list;
        }),

        // Surface the score's fan-undershoot cause-hint in the ticker: a fan that can no
        // longer reach its old speed at the same load points at a failing fan or clogged
        // intake. Read off the computed score so the two never disagree.
        new Rule("fan-slowing", TimeSpan.FromDays(3), ctx =>
        {
            if (ctx.Scores is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, ComponentScore score) in ctx.Scores)
            {
                if (score.Reasons.FirstOrDefault(r => r.Code == "fan-undershoot") is { } reason)
                    list.Add(new Remark("fan-slowing", ctx.NowUtc, RemarkSeverity.Notice, reason.Text, kind));
            }
            return list;
        }),

        // Controlled check-up nudge: the fingerprint test runs an identical, repeatable load,
        // so month-over-month fingerprints are the most scientifically comparable reading
        // DeltaT has. Prompt one periodically once there's a baseline to compare against.
        new Rule("checkup-due", TimeSpan.FromDays(14), ctx =>
            ctx is { BaselineReady: true, DaysSinceLastFingerprint: >= 30 }
                ? One("checkup-due", ctx, RemarkSeverity.Info,
                    ctx.DaysSinceLastFingerprint is { } d && d >= 60
                        ? $"It's been about {d / 30} months since the last fingerprint test. A fresh run is the cleanest way to compare thermals like-for-like: same load, weather-corrected. A good habit every month or two."
                        : "It's been about a month since the last fingerprint test. Running one now gives you a controlled, weather-corrected data point to compare against, the most rigorous check of thermal health DeltaT offers.")
                : Enumerable.Empty<Remark>()),

        new Rule("ssd-hot", TimeSpan.FromHours(2), ctx =>
            ctx.Latest?.Components.FirstOrDefault(c => c.Kind == ComponentKind.Storage && c.TemperatureC >= 70) is { TemperatureC: { } t }
                ? One("ssd-hot", ctx, RemarkSeverity.Warning,
                    $"SSD at {t:0}°, beyond comfortable. It has no paste to blame; check airflow around the drive bay.")
                : Enumerable.Empty<Remark>()),

        new Rule("all-quiet", TimeSpan.FromDays(7), ctx =>
        {
            if (!ctx.BaselineReady || ctx.Scores is null || ctx.Scores.Count == 0) return Enumerable.Empty<Remark>();
            bool allHealthy = ctx.Scores.Values.All(s => s.Verdict is Verdict.Fresh or Verdict.Good);
            return allHealthy && ctx.ThrottleEventsLastHour == 0
                ? One("all-quiet", ctx, RemarkSeverity.Info,
                    "Weekly check-in: deltas on baseline, no throttling, nothing drifting. The paste is earning its keep.")
                : Enumerable.Empty<Remark>();
        }),

        // Counterpart to temp-climbing: credit where due when things improve.
        new Rule("temp-falling", TimeSpan.FromHours(6), ctx =>
        {
            if (ctx.ShortTrend is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, (double recent, double prev, bool similar)) in ctx.ShortTrend)
            {
                double diff = prev - recent;
                if (similar && diff >= 5)
                    list.Add(new Remark("temp-falling", ctx.NowUtc, RemarkSeverity.Info,
                        $"{kind.Label()} is running {diff:0.#}° cooler than an hour ago at similar load. Whatever changed, the silicon approves.", kind));
            }
            return list;
        }),

        // A wide hotspot-to-edge gap under load is the classic signature of paste
        // that stopped spreading heat evenly (pump-out, drying, a void).
        new Rule("gpu-hotspot-gap", TimeSpan.FromHours(24), ctx =>
        {
            if (ctx.Latest?.Find(ComponentKind.GpuDiscrete) is not
                { TemperatureC: { } t, HotspotC: { } hot, LoadPercent: >= 50 })
                return Enumerable.Empty<Remark>();
            double gap = hot - t;
            return gap switch
            {
                >= 28 => One("gpu-hotspot-gap", ctx, RemarkSeverity.Warning,
                    $"GPU hotspot is running {gap:0}° above the edge sensor under load. Heat is trapped in one spot. That pattern is classic dried or pumped-out paste.", ComponentKind.GpuDiscrete),
                >= 20 => One("gpu-hotspot-gap", ctx, RemarkSeverity.Notice,
                    $"GPU hotspot is {gap:0}° above its edge sensor under load. A wide gap like that is how paste looks when it stops spreading heat evenly. Worth watching.", ComponentKind.GpuDiscrete),
                _ => Enumerable.Empty<Remark>(),
            };
        }),

        // Hot silicon under heavy load with the fan barely turning: almost always a
        // silent fan profile. Fan-normalized scoring already compensates, but the
        // user should know the trade they're making.
        new Rule("fan-silent-load", TimeSpan.FromHours(6), ctx =>
        {
            if (ctx.Latest is not { } snap) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach (ComponentKind kind in new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete })
            {
                if (snap.Find(kind) is { TemperatureC: >= 82 and { } t, FanRpm: < 1200 and { } rpm,
                        Bucket: LoadBucket.Heavy or LoadBucket.Max })
                    list.Add(new Remark("fan-silent-load", ctx.NowUtc, RemarkSeverity.Notice,
                        $"{kind.Label()} at {t:0}° under heavy load with the fan at {rpm:0} RPM. If that's a silent fan profile, it's buying quiet with degrees.", kind));
            }
            return list;
        }),

        new Rule("battery-wear", TimeSpan.FromDays(90), ctx =>
        {
            if (ctx.Latest?.Find(ComponentKind.Battery) is not { WearPercent: >= 15 and { } wear } bat)
                return Enumerable.Empty<Remark>();
            string cycles = bat.BatteryCycles is { } c ? $" after {c:0} charge cycles" : "";
            return One("battery-wear", ctx, RemarkSeverity.Info, wear >= 30
                ? $"Battery check: {wear:0}% of design capacity is gone{cycles}. Expect shorter unplugged sessions. That's chemistry aging, not paste."
                : $"Battery check: {wear:0}% of design capacity gone{cycles}. Normal aging, noted for the record.", ComponentKind.Battery);
        }),

        new Rule("ssd-wear", TimeSpan.FromDays(90), ctx =>
        {
            if (ctx.Latest?.Find(ComponentKind.Storage) is not { WearPercent: >= 50 and { } wear })
                return Enumerable.Empty<Remark>();
            return One("ssd-wear", ctx, wear >= 80 ? RemarkSeverity.Notice : RemarkSeverity.Info, wear >= 80
                ? $"SSD has used {wear:0}% of its rated write endurance. Start thinking about a successor, and keep the backups honest."
                : $"SSD wear check: {wear:0}% of rated write endurance used. No action needed, just bookkeeping.", ComponentKind.Storage);
        }),

        new Rule("night-owl", TimeSpan.FromHours(20), ctx =>
        {
            if (ctx.LocalHour is < 1 or > 4) return Enumerable.Empty<Remark>();
            bool working = ctx.Latest?.Find(ComponentKind.Cpu)?.Bucket is LoadBucket.Heavy or LoadBucket.Max
                || ctx.Latest?.Find(ComponentKind.GpuDiscrete)?.Bucket is LoadBucket.Heavy or LoadBucket.Max;
            return working
                ? One("night-owl", ctx, RemarkSeverity.Info,
                    $"Heavy load at {ctx.LocalHour} a.m., no judgment. The readings are just as good at this hour.")
                : Enumerable.Empty<Remark>();
        }),

        new Rule("throttle-free-month", TimeSpan.FromDays(30), ctx =>
            ctx is { ThrottleEventsLast30Days: 0, DaysTogether: >= 30, BaselineReady: true }
                ? One("throttle-free-month", ctx, RemarkSeverity.Info,
                    "A full month without a single thermal throttle. Whatever the workload threw at it, the cooling absorbed.")
                : Enumerable.Empty<Remark>()),

        // Anniversary notes. Ranges instead of exact days so a machine that was off
        // on the milestone day still gets its mention; the cooldown stops repeats.
        new Rule("days-together", TimeSpan.FromDays(25), ctx => ctx.DaysTogether switch
        {
            >= 365 and < 380 => One("days-together", ctx, RemarkSeverity.Notice,
                "One year of thermal history on this machine. Season-to-season comparisons are now backed by data, not guesswork."),
            >= 100 and < 110 => One("days-together", ctx, RemarkSeverity.Info,
                "Day 100 on this machine. That's enough history to tell real drift from noise at a glance."),
            >= 30 and < 38 => One("days-together", ctx, RemarkSeverity.Info,
                "One month of watching this machine. The baseline maths only gets sharper from here."),
            _ => Enumerable.Empty<Remark>(),
        }),

        new Rule("city-moved", TimeSpan.FromHours(1), ctx =>
            ctx.CityChanged && ctx.City is { } city
                ? One("city-moved", ctx, RemarkSeverity.Info,
                    $"Weather reference moved to {city}. Ambient correction follows the machine, not the old town.")
                : Enumerable.Empty<Remark>()),

        // A finished fingerprint speaks in the ticker too, not only in its window —
        // the comparison is weather-corrected, so it means the same thing in any season.
        new Rule("fingerprint-echo", TimeSpan.Zero, ctx =>
        {
            if (ctx.Fingerprint is not { } fp) return Enumerable.Empty<Remark>();
            string label = fp.Kind.Label();
            FingerprintResult r = fp.Result;
            return fp.DeltaVsPrevious switch
            {
                null => One("fingerprint-echo", ctx, RemarkSeverity.Notice,
                    $"First {label} fingerprint on record: sustained {r.SustainedC:0.#}° under full load"
                    + (r.ThrottleSamples > 0 ? $" with {r.ThrottleSamples} throttling samples" : "")
                    + ". Every future run measures drift against it.", fp.Kind),
                >= 3 and { } d => One("fingerprint-echo", ctx, RemarkSeverity.Warning,
                    $"{label} fingerprint came back {d:0.#}° hotter than the last run, weather-corrected. Once is a data point; rerun in a few days, twice is a trend.", fp.Kind),
                <= -3 and { } d => One("fingerprint-echo", ctx, RemarkSeverity.Notice,
                    $"{label} fingerprint came back {-d:0.#}° cooler than the last run, weather-corrected. Whatever changed (a cleaning, a repaste, better airflow), it's real.", fp.Kind),
                { } d => One("fingerprint-echo", ctx, RemarkSeverity.Info,
                    $"{label} fingerprint matches the last run ({d:+0.#;-0.#}° weather-corrected). Consistency is exactly what healthy paste looks like.", fp.Kind),
            };
        }),
    };

    private static IEnumerable<Remark> One(string id, RemarkContext ctx, RemarkSeverity sev, string text, ComponentKind? kind = null)
    {
        yield return new Remark(id, ctx.NowUtc, sev, text, kind);
    }

    private static string StaleGap(int days) => days switch
    {
        >= 60 => $"{days / 30} months",
        >= 21 => $"{(int)Math.Round(days / 7.0)} weeks",
        _ => $"{days} days",
    };

    /// <summary>The recommended action for a bad verdict, taken from the diagnosed cause
    /// so the remark never reflexively blames the paste for dust or a fan.</summary>
    private static string VerdictAction(ComponentScore score, bool urgent) =>
        score.Diagnosis?.Primary.Cause switch
        {
            ThermalCause.Airflow => urgent
                ? "The evidence points at dust or blocked airflow, so start with a clean-out; that should claw back several degrees."
                : "The evidence points at dust or blocked airflow; a clean-out is the first move.",
            ThermalCause.FanFault => urgent
                ? "A fan can't reach its old speed anymore, so check the fan and its intake before blaming the paste."
                : "A fan is running slower than it used to; check it and its intake first.",
            ThermalCause.Mount => urgent
                ? "The hotspot pattern points at an uneven mount or pumped-out paste, so a remount with fresh paste should claw back several degrees."
                : "The hotspot pattern points at the mount; plan a remount with fresh paste.",
            ThermalCause.CoolingHeadroom => urgent
                ? "It's throttling with no headroom left; whatever the root cause, the cooling needs attention now."
                : "It's running out of thermal headroom; plan to improve the cooling.",
            ThermalCause.Paste => urgent
                ? "Fresh paste should claw back several degrees."
                : "Start planning a repaste.",
            _ => urgent
                ? "Fresh paste or a clean-out should claw back several degrees."
                : "Start planning a repaste or a clean-out.",
        };
}

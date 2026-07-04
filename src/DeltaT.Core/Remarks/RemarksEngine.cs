using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;

namespace DeltaT.Core.Remarks;

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
    int LearningDay);

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
                $"First readings are in — {temps}.{outside} DeltaT is watching now; give it about a week to learn what normal looks like here.");
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
                        $"New record: {kind.Label()} just hit {t:0}° — the hottest DeltaT has ever seen it.", kind));
            }
            return list;
        }),

        new Rule("weather-hot", TimeSpan.FromHours(8), ctx =>
            ctx.AmbientC is { } a && a >= 38
                ? One("weather-hot", ctx, RemarkSeverity.Info,
                    $"It's {a:0}° outside. If today's numbers look scary, blame the sun before the paste — DeltaT corrects for it.")
                : Enumerable.Empty<Remark>()),

        new Rule("weather-cold", TimeSpan.FromHours(12), ctx =>
            ctx.AmbientC is { } a && a <= 8
                ? One("weather-cold", ctx, RemarkSeverity.Info,
                    $"{a:0}° outside — free cooling day. Don't let today's pretty temps fool the long-term picture; DeltaT won't.")
                : Enumerable.Empty<Remark>()),

        new Rule("weather-stale", TimeSpan.FromHours(12), ctx =>
            ctx.AmbientStale
                ? One("weather-stale", ctx, RemarkSeverity.Info,
                    "Can't reach the weather service — using the last known outside temperature until it's back.")
                : Enumerable.Empty<Remark>()),

        new Rule("on-battery", TimeSpan.FromHours(4), ctx =>
            !ctx.OnAcPower && ctx.Latest?.Find(ComponentKind.Cpu)?.Bucket == LoadBucket.Heavy
                ? One("on-battery", ctx, RemarkSeverity.Info,
                    "Heavy load on battery — these readings don't count toward paste scoring (battery power limits change the physics).")
                : Enumerable.Empty<Remark>()),

        new Rule("learning-daily", TimeSpan.FromHours(20), ctx =>
            !ctx.BaselineReady && ctx.LearningDay >= 1
                ? One("learning-daily", ctx, RemarkSeverity.Info,
                    $"Learning day {ctx.LearningDay}: baseline is {ctx.CalibrationProgress * 100:0}% assembled. Use the machine normally — games and heavy work teach DeltaT the most.")
                : Enumerable.Empty<Remark>()),

        new Rule("baseline-ready", TimeSpan.MaxValue, ctx =>
            ctx.BaselineJustBecameReady
                ? One("baseline-ready", ctx, RemarkSeverity.Notice,
                    "Baseline locked in. From now on, DeltaT compares this machine against itself — the only comparison that means anything.")
                : Enumerable.Empty<Remark>()),

        new Rule("score-drop", TimeSpan.FromHours(24), ctx =>
        {
            if (ctx.ScoreDropThisWeek is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, int drop) in ctx.ScoreDropThisWeek)
            {
                if (drop >= 8)
                    list.Add(new Remark("score-drop", ctx.NowUtc, RemarkSeverity.Warning,
                        $"{kind.Label()} paste score slid {drop} points this week. One bad week isn't a verdict, but the trend has DeltaT's attention.", kind));
            }
            return list;
        }),

        new Rule("verdict-repaste", TimeSpan.FromHours(24), ctx =>
        {
            if (ctx.Scores is null) return Enumerable.Empty<Remark>();
            var list = new List<Remark>();
            foreach ((ComponentKind kind, ComponentScore score) in ctx.Scores)
            {
                if (score.Verdict == Verdict.RepasteNow)
                    list.Add(new Remark("verdict-repaste", ctx.NowUtc, RemarkSeverity.Alert,
                        $"{kind.Label()} paste score is {score.Value}. DeltaT's honest read: it's time. Fresh paste should claw back several degrees.", kind));
                else if (score.Verdict == Verdict.Degraded)
                    list.Add(new Remark("verdict-repaste", ctx.NowUtc, RemarkSeverity.Warning,
                        $"{kind.Label()} paste is degrading (score {score.Value}). Start planning a repaste — no emergency, but the direction is clear.", kind));
            }
            return list;
        }),

        new Rule("ssd-hot", TimeSpan.FromHours(2), ctx =>
            ctx.Latest?.Components.FirstOrDefault(c => c.Kind == ComponentKind.Storage && c.TemperatureC >= 70) is { TemperatureC: { } t }
                ? One("ssd-hot", ctx, RemarkSeverity.Warning,
                    $"SSD at {t:0}° — beyond comfortable. It has no paste to blame; check airflow around the drive bay.")
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
    };

    private static IEnumerable<Remark> One(string id, RemarkContext ctx, RemarkSeverity sev, string text, ComponentKind? kind = null)
    {
        yield return new Remark(id, ctx.NowUtc, sev, text, kind);
    }
}

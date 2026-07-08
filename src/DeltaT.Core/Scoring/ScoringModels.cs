using DeltaT.Core.Knowledge;
using DeltaT.Core.Monitoring;

namespace DeltaT.Core.Scoring;

public enum Verdict
{
    Calibrating,
    Fresh,       // 85–100
    Good,        // 70–84
    Aging,       // 50–69
    Degraded,    // 30–49
    RepasteNow,  // 0–29
}

public enum PatternHint
{
    None,
    LooksLikePaste,   // fast soak, throttle kisses, load-dependent excess
    LooksLikeDust,    // broad steady-state excess, elevated fans, normal soak
    Mixed,
}

/// <summary>One contributor to a score, kept structured so the UI can show
/// "why this number" honestly.</summary>
public sealed record ScoreReason(string Code, string Text, double PointsLost);

public sealed record ComponentScore(
    ComponentKind Kind,
    string Name,
    int Value,
    Verdict Verdict,
    bool Calibrating,
    double CalibrationProgress,     // 0..1, meaningful while calibrating — the honest confidence meter
    IReadOnlyList<ScoreReason> Reasons,
    PatternHint Hint,
    // While calibrating: the one thing holding the baseline back (empty once ready).
    string CalibrationConstraint = "",
    // A real number computed before the baseline has locked — shown as an estimate
    // with its confidence, never as a final verdict. False both for the pure
    // "not enough data yet" state and for a locked score.
    bool Provisional = false)
{
    public static ComponentScore CalibratingScore(ComponentKind kind, string name, double progress, IReadOnlyList<ScoreReason> reasons, string constraint = "") =>
        new(kind, name, 0, Verdict.Calibrating, true, progress, reasons, PatternHint.None, constraint);
}

/// <summary>Recent behaviour of one (bucket, ambient band) cell.</summary>
public sealed record RecentBucketObs(
    LoadBucket Bucket,
    int Band,
    int Minutes,
    double? DeltaAvg,
    double TempAvg,
    double TempMax,
    double? FanAvg,
    int ThrottleSampleCount);

public sealed record BaselineBucket(
    LoadBucket Bucket,
    int Band,
    double DeltaAvg,
    double? DeltaP95,
    double? FanAvg,
    int Minutes);

/// <summary>Everything the engine needs. Assembled by ScoreCoordinator from the
/// database; the engine itself never touches clocks, sensors or storage.</summary>
public sealed record ScoreInput(
    ComponentKind Kind,
    string Name,
    IReadOnlyList<RecentBucketObs> Recent,
    IReadOnlyList<BaselineBucket> Baseline,
    double RecentWindowHours,
    int ThrottleEvents,
    double? SoakRateRecent,
    double? SoakRateBaseline,
    double? LimitC,
    ComponentProfile? Profile,
    bool BaselineReady,
    double CalibrationProgress,
    // True when the machine was dormant long enough that the learned baseline may
    // no longer describe the current physical setup (dust, cooler swap, unlogged
    // repaste). A confidence note only — it never moves the number.
    bool BaselineStale = false,
    int DormantDays = 0,
    // While calibrating: the binding constraint on baseline confidence, for the UI.
    string CalibrationConstraint = "");

public static class Verdicts
{
    public static Verdict FromScore(int score) => score switch
    {
        >= 85 => Verdict.Fresh,
        >= 70 => Verdict.Good,
        >= 50 => Verdict.Aging,
        >= 30 => Verdict.Degraded,
        _ => Verdict.RepasteNow,
    };

    public static string Label(this Verdict verdict) => verdict switch
    {
        Verdict.Calibrating => "Calibrating",
        Verdict.Fresh => "Fresh",
        Verdict.Good => "Good",
        Verdict.Aging => "Aging - watch it",
        Verdict.Degraded => "Degraded - plan a repaste",
        _ => "Repaste now",
    };

    /// <summary>The bare verdict word, for tight spots like the score dial where
    /// the full "— plan a repaste" qualifier won't fit. The qualifier still shows
    /// in the hero title/detail, which has room to wrap.</summary>
    public static string ShortLabel(this Verdict verdict) => verdict switch
    {
        Verdict.Calibrating => "Calibrating",
        Verdict.Fresh => "Fresh",
        Verdict.Good => "Good",
        Verdict.Aging => "Aging",
        Verdict.Degraded => "Degraded",
        _ => "Repaste now",
    };
}

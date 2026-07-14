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
    double CalibrationProgress,     // 0..1, meaningful while calibrating — the smoothed, evidence-driven meter value (BaselineBuilder.MeterProgress), not raw confidence
    IReadOnlyList<ScoreReason> Reasons,
    PatternHint Hint,
    // While calibrating: the one thing holding the baseline back (empty once ready).
    string CalibrationConstraint = "",
    // A real number computed before the baseline has locked — shown as an estimate
    // with its confidence, never as a final verdict. False both for the pure
    // "not enough data yet" state and for a locked score.
    bool Provisional = false)
{
    public ScoringEngine.FanNormalization? Fan { get; init; }
    public ScoringEngine.PowerNormalization? Power { get; init; }

    /// <summary>The weighted fan+power-normalized excess over baseline (°C), the number
    /// behind the "running N° hotter" sentence, exposed so the UI can draw it as an
    /// instrument readout instead of prose. Null when nothing was comparable.</summary>
    public double? ExcessC { get; init; }

    /// <summary>Why the component runs the way it does, ranked by confidence. The number
    /// says how healthy; this says what to do about it (and stops DeltaT reflexively
    /// blaming the paste). Null while calibrating, or when there's simply nothing to say.</summary>
    public ThermalDiagnosis? Diagnosis { get; init; }

    /// <summary>Per-subsystem health readout (paste, airflow, fans, mount, headroom,
    /// power state), from the same evidence as the diagnosis. Empty while calibrating
    /// without a provisional estimate.</summary>
    public IReadOnlyList<AspectHealth> Aspects { get; init; } = Array.Empty<AspectHealth>();

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
    int ThrottleSampleCount,
    // Mean hotspot-to-edge gap (°C) when the sensor exposes a hotspot; null otherwise.
    double? GapAvg = null,
    // Mean package power (watts) over the cell; null when the sensor exposes no power.
    // Lets scoring compare thermal resistance (ΔT/P) instead of raw rise.
    double? PowerAvg = null);

public sealed record BaselineBucket(
    LoadBucket Bucket,
    int Band,
    double DeltaAvg,
    double? DeltaP95,
    double? FanAvg,
    int Minutes,
    // Mean absolute die temperature (°C) learned for this cell. The physical anchor
    // that lets scoring compare across ambient bands without inflating the rise:
    // ambient of this cell ≈ TempAvg − DeltaAvg. Null on legacy rows (pre-rebuild).
    double? TempAvg = null,
    // This machine's own healthy hotspot-to-edge gap (°C). Different GPU models run
    // very different natural gaps, so paste judgement on the gap is drift against
    // this, never a universal number. Null when no hotspot sensor exists.
    double? GapAvg = null,
    // Mean package power (watts) this cell's rise was learned at. When both baseline
    // and recent cells carry power, scoring compares thermal resistance (ΔT/P) so a
    // power-limit/undervolt/overclock change doesn't masquerade as paste drift.
    double? PowerAvg = null);

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
    // Falling-edge cooldown rate (°C/min, positive): how fast the die sheds heat when
    // load drops. Degraded paste cools SLOWER, so recent < baseline is the warning sign
    // — the opposite direction to soak. Null until cooldown edges have been observed.
    double? CooldownRateRecent,
    double? CooldownRateBaseline,
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
    string CalibrationConstraint = "",
    // Statistical confidence in the baseline data itself (independent of paste cure).
    // A provisional score is only shown once this is solid, so a thin, half-learned
    // baseline can't flash a misleading number before it locks.
    double CalibrationDataConfidence = 1.0,
    // True when a provisional score was already shown this epoch. The confidence
    // floor is an entry gate, not a hold requirement: once a number is on screen,
    // it keeps updating live instead of vanishing when a noisy session dips
    // confidence below the floor again.
    bool ProvisionalEverShown = false,
    // User override of the absolute concern temperature (°C). Overclockers run hot on
    // purpose and don't want warning spam from a stock magic number; when set, this
    // replaces the profile's ConcernC for the absolute-temperature check. Null = profile.
    double? ConcernOverrideC = null,
    // Whether the "no headroom left" near-silicon-limit warning is active. Off for
    // rigs deliberately pinned near TjMax. Real throttle EVENTS are always counted.
    bool HeadroomWarnings = true);

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

    // The score is OVERALL thermal health against this machine's own baseline; the
    // diagnosis names the cause. So the verdict words stay cause-neutral: "repaste now"
    // as a verdict would blame the paste even when the evidence says dust or a fan.
    public static string Label(this Verdict verdict) => verdict switch
    {
        Verdict.Calibrating => "Calibrating",
        Verdict.Fresh => "Excellent",
        Verdict.Good => "Good",
        Verdict.Aging => "Drifting, watch it",
        Verdict.Degraded => "Degraded, needs attention",
        _ => "Critical, act now",
    };

    /// <summary>The bare verdict word, for tight spots like the score dial where
    /// the full qualifier won't fit. The qualifier still shows in the hero
    /// title/detail, which has room to wrap.</summary>
    public static string ShortLabel(this Verdict verdict) => verdict switch
    {
        Verdict.Calibrating => "Calibrating",
        Verdict.Fresh => "Excellent",
        Verdict.Good => "Good",
        Verdict.Aging => "Drifting",
        Verdict.Degraded => "Degraded",
        _ => "Critical",
    };
}

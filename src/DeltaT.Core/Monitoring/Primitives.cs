namespace DeltaT.Core.Monitoring;

public enum ComponentKind
{
    Cpu,
    GpuDiscrete,
    GpuIntegrated,
    Storage,
    Battery,
    Motherboard,
}

/// <summary>Utilization bands. All per-component statistics are kept per bucket
/// so "temp under 100% load" and "temp at idle" never get mixed together.
///
/// <b>Heavy is split from Max at 90%.</b> Temp-rise-over-ambient scales with load,
/// so pooling everything ≥70% into one bucket mixed a 100%-pinned load (a stress
/// test, the fingerprint's CpuBurner, or a GPU-bound game that pegs the card at 99%)
/// with organic 70–90% work. Those run at genuinely different temperatures, so their
/// per-session mean deltas scatter — which the calibration model reads as "I don't
/// know this machine's normal", inflating the standard error and collapsing
/// confidence (the "CPU won't calibrate / confidence fell" bug). Keeping full load in
/// its own cell restores a true like-for-like comparison. GPUs mostly live in Max
/// (they peg in load); CPUs spread across both, which is exactly why the CPU used to
/// look stuck while the GPU locked instantly.</summary>
public enum LoadBucket
{
    Idle = 0,   // < 10 %
    Light = 1,  // 10–40 %
    Medium = 2, // 40–70 %
    Heavy = 3,  // 70–90 %
    Max = 4,    // ≥ 90 % (pinned / full load)
}

/// <summary>Outside-temperature bands. Baselines are learned per band so a July
/// reading is only ever compared against other hot-weather readings.</summary>
public enum AmbientBand
{
    Cold = 0,  // < 15 °C
    Mild = 1,  // 15–25 °C
    Warm = 2,  // 25–35 °C
    Hot = 3,   // ≥ 35 °C
}

public static class LoadBuckets
{
    public static LoadBucket FromPercent(double percent) => percent switch
    {
        < 10 => LoadBucket.Idle,
        < 40 => LoadBucket.Light,
        < 70 => LoadBucket.Medium,
        < 90 => LoadBucket.Heavy,
        _ => LoadBucket.Max,
    };

    public static string Label(this LoadBucket bucket) => bucket switch
    {
        LoadBucket.Idle => "idle",
        LoadBucket.Light => "light load",
        LoadBucket.Medium => "medium load",
        LoadBucket.Heavy => "heavy load",
        _ => "full load",
    };
}

public static class AmbientBands
{
    public static AmbientBand FromCelsius(double c) => c switch
    {
        < 15 => AmbientBand.Cold,
        < 25 => AmbientBand.Mild,
        < 35 => AmbientBand.Warm,
        _ => AmbientBand.Hot,
    };

    public static string Label(this AmbientBand band) => band switch
    {
        AmbientBand.Cold => "cold weather",
        AmbientBand.Mild => "mild weather",
        AmbientBand.Warm => "warm weather",
        _ => "hot weather",
    };
}

public static class ComponentKinds
{
    public static string Label(this ComponentKind kind) => kind switch
    {
        ComponentKind.Cpu => "CPU",
        ComponentKind.GpuDiscrete => "GPU",
        ComponentKind.GpuIntegrated => "iGPU",
        ComponentKind.Storage => "SSD",
        ComponentKind.Battery => "Battery",
        _ => "Board",
    };

    /// <summary>Only parts that actually have thermal paste get a paste score;
    /// everything else gets a plain thermal-health readout.</summary>
    public static bool HasPaste(this ComponentKind kind) =>
        kind is ComponentKind.Cpu or ComponentKind.GpuDiscrete;
}

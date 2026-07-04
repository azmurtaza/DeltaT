namespace Kelvin.Core.Monitoring;

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
/// so "temp under 100% load" and "temp at idle" never get mixed together.</summary>
public enum LoadBucket
{
    Idle = 0,   // < 10 %
    Light = 1,  // 10–40 %
    Medium = 2, // 40–70 %
    Heavy = 3,  // ≥ 70 %
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
        _ => LoadBucket.Heavy,
    };

    public static string Label(this LoadBucket bucket) => bucket switch
    {
        LoadBucket.Idle => "idle",
        LoadBucket.Light => "light load",
        LoadBucket.Medium => "medium load",
        _ => "heavy load",
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

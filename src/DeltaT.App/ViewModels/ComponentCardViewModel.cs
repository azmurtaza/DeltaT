using CommunityToolkit.Mvvm.ComponentModel;
using DeltaT.Core.Monitoring;

namespace DeltaT.App.ViewModels;

public partial class ComponentCardViewModel : ObservableObject
{
    private const int SparkCapacity = 300; // ~10 min at 2 s
    private readonly Queue<double> _history = new(SparkCapacity + 1);

    public ComponentKind Kind { get; }
    public string Badge { get; }
    public string Name { get; }

    [ObservableProperty] private double _temp;
    [ObservableProperty] private bool _hasTemp;
    [ObservableProperty] private double _limit;
    [ObservableProperty] private double _rawFraction; // always °C-based, drives ring color
    [ObservableProperty] private string _unit = "°C";
    [ObservableProperty] private double _load;
    [ObservableProperty] private string _loadText = "";
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private IReadOnlyList<double>? _spark;

    public ComponentCardViewModel(ComponentReading first)
    {
        Kind = first.Kind;
        Name = first.Name;
        Badge = first.Kind switch
        {
            ComponentKind.Cpu => "CPU",
            ComponentKind.GpuDiscrete => "GPU",
            ComponentKind.GpuIntegrated => "iGPU",
            ComponentKind.Storage => "SSD",
            ComponentKind.Battery => "BAT",
            _ => "SYS",
        };
        Limit = first.ThrottleLimitC ?? DefaultScaleC(first.Kind);
    }

    private static double DefaultScaleC(ComponentKind kind) => kind switch
    {
        ComponentKind.Storage => 90,  // NVMe throttle territory
        ComponentKind.Battery => 60,
        _ => 100,
    };

    // The raw rise-over-outside number used to live on each card, but it swings
    // with the seasons (silicon idles against a heat floor, so a cold day makes
    // the delta look alarming on a healthy machine). The scoring engine still
    // uses ambient - banded and floor-corrected - the dashboard just doesn't
    // show the uncorrected number anymore.
    public void Update(ComponentReading r, bool fahrenheit)
    {
        double Conv(double c) => fahrenheit ? c * 9 / 5 + 32 : c;
        Unit = fahrenheit ? "°F" : "°C";

        double limitC = r.ThrottleLimitC ?? DefaultScaleC(Kind);
        Limit = Math.Round(Conv(limitC), 1);

        HasTemp = r.TemperatureC.HasValue;
        if (r.TemperatureC is { } t)
        {
            Temp = Math.Round(Conv(t), 1);
            RawFraction = limitC > 0 ? t / limitC : 0;
            _history.Enqueue(t);
            while (_history.Count > SparkCapacity)
                _history.Dequeue();
            Spark = _history.ToArray();
        }

        Load = r.LoadPercent ?? 0;
        LoadText = r.LoadPercent is { } l ? $"{l:0}% load" : "";

        var meta = new List<string>(3);
        if (r.PowerW is { } p && p > 0.05) meta.Add($"{p:0.#} W");
        if (r.HotspotC is { } h) meta.Add($"hotspot {Conv(h):0}°");
        if (r.FanRpm is { } f) meta.Add($"{f:0} rpm");
        if (r.WearPercent is { } w) meta.Add($"wear {w:0.#}%");
        if (r.BatteryCycles is { } cyc) meta.Add($"{cyc:0} cycles");
        if (r.IsThrottling) meta.Add("THROTTLING");
        MetaText = string.Join("  ·  ", meta);
    }
}

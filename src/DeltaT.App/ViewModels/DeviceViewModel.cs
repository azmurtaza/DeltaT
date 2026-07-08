using CommunityToolkit.Mvvm.ComponentModel;
using DeltaT.Core.Knowledge;
using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Weather;

namespace DeltaT.App.ViewModels;

/// <summary>Device intelligence: what machine this is, what its chassis class
/// is *supposed* to do thermally (from the embedded profile knowledge), and
/// what it is doing right now. Refresh() re-reads the latest snapshot — no
/// timers; the page updates when navigated to.</summary>
public partial class DeviceViewModel : ObservableObject
{
    private readonly ThermalProfile _profile;
    private readonly Func<SensorSnapshot?> _latest;
    private readonly AmbientService _ambient;
    private readonly SettingsStore _settings;

    public string ModelName { get; }
    public string CpuName { get; }
    public string GpuText { get; }
    public string FormFactor { get; }
    public string ProfileName { get; }
    public string ProfileMatchLabel { get; }
    public string ProfileClass { get; }
    public string ProfileNotes { get; }
    public bool HasNotes => ProfileNotes.Length > 0;

    public ComponentIntelViewModel CpuIntel { get; }
    public ComponentIntelViewModel GpuIntel { get; }

    [ObservableProperty] private string _limitsText = "";
    [ObservableProperty] private string _ambientText = "";

    public DeviceViewModel(MachineIdentity machine, ThermalProfile profile,
        Func<SensorSnapshot?> latest, AmbientService ambient, SettingsStore settings)
    {
        _profile = profile;
        _latest = latest;
        _ambient = ambient;
        _settings = settings;

        ModelName = machine.Display;
        CpuName = machine.CpuName;
        GpuText = string.Join("\n", machine.GpuNames);
        FormFactor = machine.IsLaptop ? "Laptop" : "Desktop";
        ProfileName = profile.DisplayName;
        ProfileClass = profile.Class.Replace('-', ' ').ToUpperInvariant();
        ProfileMatchLabel = profile.Priority switch
        {
            >= 100 => "EXACT MODEL",
            >= 90 => "BRAND SERIES",
            _ => "CATEGORY BASELINE",
        };
        ProfileNotes = profile.Notes ?? "";

        CpuIntel = new ComponentIntelViewModel("CPU", profile.Cpu);
        GpuIntel = new ComponentIntelViewModel("GPU", profile.Gpu);
        Refresh();
    }

    public void Refresh()
    {
        bool fahrenheit = _settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        double roomOffset = _settings.GetDouble(SettingsKeys.IndoorOffsetC) ?? 0;
        double? ambientC = _ambient.CurrentAmbientC;
        SensorSnapshot? snap = _latest();

        CpuIntel.Update(snap?.Find(ComponentKind.Cpu), ambientC, roomOffset, fahrenheit);
        GpuIntel.Update(snap?.Find(ComponentKind.GpuDiscrete), ambientC, roomOffset, fahrenheit);

        string unit = fahrenheit ? "°F" : "°C";
        AmbientText = ambientC is { } amb
            ? $"outside reference {Temp(amb, fahrenheit):0.#}{unit}"
            : "outside reference unavailable";

        var limits = new List<string>();
        if (snap?.Find(ComponentKind.Cpu)?.ThrottleLimitC is { } cpuLimit)
            limits.Add($"CPU TjMax {Temp(cpuLimit, fahrenheit):0}{unit} - read from the silicon");
        if (snap?.Find(ComponentKind.GpuDiscrete)?.ThrottleLimitC is { } gpuLimit)
            limits.Add($"GPU throttle point {Temp(gpuLimit, fahrenheit):0}{unit}");
        LimitsText = limits.Count > 0 ? string.Join("\n", limits) : "No limits reported yet - sensors still warming up.";
    }

    internal static double Temp(double c, bool fahrenheit) => fahrenheit ? c * 9 / 5 + 32 : c;
    internal static double Delta(double c, bool fahrenheit) => fahrenheit ? c * 9 / 5 : c;
}

/// <summary>One component's expected-vs-measured block on the Device page.</summary>
public partial class ComponentIntelViewModel : ObservableObject
{
    private readonly ComponentProfile? _spec;

    public string Label { get; }
    public bool HasProfile => _spec is not null;

    [ObservableProperty] private string _idleDeltaText = "-";
    [ObservableProperty] private string _heavyDeltaText = "-";
    [ObservableProperty] private string _sustainedText = "-";
    [ObservableProperty] private string _concernText = "-";
    [ObservableProperty] private string _nowText = "no reading yet";
    [ObservableProperty] private double _colorFraction;
    [ObservableProperty] private bool _hasReading;
    [ObservableProperty] private double _stripValue;
    [ObservableProperty] private double _stripMin = 25;
    [ObservableProperty] private double _stripNorm = 80;
    [ObservableProperty] private double _stripConcern = 95;
    [ObservableProperty] private double _stripMax = 102;

    public ComponentIntelViewModel(string label, ComponentProfile? spec)
    {
        Label = label;
        _spec = spec;
    }

    public void Update(ComponentReading? reading, double? ambientC, double roomOffsetC, bool fahrenheit)
    {
        string unit = fahrenheit ? "°F" : "°C";

        if (_spec is { } spec)
        {
            IdleDeltaText = $"+{DeviceViewModel.Delta(spec.TypicalIdleDeltaC, fahrenheit):0}° over ambient";
            HeavyDeltaText = $"+{DeviceViewModel.Delta(spec.TypicalHeavyDeltaC, fahrenheit):0}° over ambient";
            SustainedText = $"{DeviceViewModel.Temp(spec.SustainedNormC, fahrenheit):0}{unit}";
            ConcernText = $"above {DeviceViewModel.Temp(spec.ConcernC, fahrenheit):0}{unit}";

            StripMin = DeviceViewModel.Temp(25, fahrenheit);
            StripNorm = DeviceViewModel.Temp(spec.SustainedNormC, fahrenheit);
            StripConcern = DeviceViewModel.Temp(spec.ConcernC, fahrenheit);
            StripMax = DeviceViewModel.Temp(spec.ConcernC + 7, fahrenheit);
        }

        if (reading?.TemperatureC is { } t)
        {
            HasReading = true;
            StripValue = DeviceViewModel.Temp(t, fahrenheit);
            double limit = reading.ThrottleLimitC ?? 100;
            ColorFraction = limit > 0 ? t / limit : 0;

            var parts = new List<string> { $"{DeviceViewModel.Temp(t, fahrenheit):0.0}{unit} now" };
            if (ambientC is { } amb)
                parts.Add($"Δ {DeviceViewModel.Delta(t - (amb + roomOffsetC), fahrenheit):+0.#;-0.#}°");
            if (reading.LoadPercent is { } load)
                parts.Add($"{load:0}% load");
            if (reading.IsThrottling)
                parts.Add("THROTTLING");
            NowText = string.Join("  ·  ", parts);
        }
        else
        {
            HasReading = false;
            NowText = "no reading yet";
        }
    }
}

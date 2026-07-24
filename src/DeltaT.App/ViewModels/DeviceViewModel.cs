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
    public ComponentIntelViewModel SsdIntel { get; }

    [ObservableProperty] private string _limitsText = "";
    [ObservableProperty] private string _ambientText = "";
    [ObservableProperty] private string _powerBudgetText = "";
    [ObservableProperty] private bool _hasPowerBudget;

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
        SsdIntel = ComponentIntelViewModel.ForSsd();
        Refresh();
    }

    public void Refresh()
    {
        bool fahrenheit = _settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        // In fixed-indoor mode the ambient reference IS the user's set temperature, so the
        // weather-mode display offset does not apply (it would double-count).
        bool fixedMode = _ambient.FixedMode;
        double roomOffset = fixedMode ? 0 : (_settings.GetDouble(SettingsKeys.IndoorOffsetC) ?? 0);
        double? ambientC = _ambient.CurrentAmbientC;
        SensorSnapshot? snap = _latest();

        CpuIntel.Update(snap?.Find(ComponentKind.Cpu), ambientC, roomOffset, fahrenheit);
        GpuIntel.Update(snap?.Find(ComponentKind.GpuDiscrete), ambientC, roomOffset, fahrenheit);
        SsdIntel.Update(snap?.Find(ComponentKind.Storage), ambientC, roomOffset, fahrenheit);

        string unit = fahrenheit ? "°F" : "°C";
        AmbientText = ambientC is { } amb
            ? fixedMode
                ? $"indoor reference {Temp(amb, fahrenheit):0.#}{unit} (fixed)"
                : $"outside reference {Temp(amb, fahrenheit):0.#}{unit}"
            : "outside reference unavailable";

        ComponentReading? cpu = snap?.Find(ComponentKind.Cpu);
        var limits = new List<string>();
        if (cpu?.ThrottleLimitC is { } cpuLimit)
            limits.Add($"CPU TjMax {Temp(cpuLimit, fahrenheit):0}{unit}, read from the silicon");
        if (snap?.Find(ComponentKind.GpuDiscrete)?.ThrottleLimitC is { } gpuLimit)
            limits.Add($"GPU throttle point {Temp(gpuLimit, fahrenheit):0}{unit}");
        LimitsText = limits.Count > 0 ? string.Join("\n", limits) : "No limits reported yet. Sensors still warming up.";

        PowerBudgetText = BuildPowerBudget(cpu);
        HasPowerBudget = PowerBudgetText.Length > 0;
    }

    /// <summary>The Intel-only absolute power-budget readout: the CPU's configured PL1/PL2, and
    /// whether it is currently reaching that budget or being held below it by heat. Strictly
    /// gated so a deliberately power-limited machine (boost off, low power plan) reads as
    /// "by configuration", never as a thermal problem. Empty (row hidden) on an Intel CPU whose
    /// MSRs can't be read yet; a one-line note on AMD, whose budget lives in a different register
    /// set.</summary>
    private string BuildPowerBudget(ComponentReading? cpu)
    {
        if (cpu?.PowerLimit is not { } pl)
        {
            bool amd = CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                       || CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
            return amd
                ? "Power budget: this is an Intel-only reading. AMD exposes its limits (PPT/TDC/EDC) through a different interface."
                : "";
        }

        var lines = new List<string>();
        if (pl.Pl2W is { } pl2)
            lines.Add($"PL2 (short-term turbo): {pl2:0} W" + (pl.TauSeconds is { } tau ? $" for {tau:0.#} s" : ""));
        if (pl.Pl1W is { } pl1)
            lines.Add($"PL1 (sustained): {pl1:0} W");

        if (cpu.PowerW is { } w)
        {
            if (pl.ThermalActive && pl.Pl2W is { } p2 && w < p2 * 0.85)
                lines.Add($"Now drawing {w:0} W, held below the budget by heat. Cooling, not power, is the ceiling.");
            else if (pl.PowerLimitActive || pl.CurrentLimitActive)
                lines.Add($"Now drawing {w:0} W, capped by the power configuration (not heat). That is by design.");
            else
                lines.Add($"Now drawing {w:0} W, within the configured budget.");
        }
        return string.Join("\n", lines);
    }

    internal static double Temp(double c, bool fahrenheit) => fahrenheit ? c * 9 / 5 + 32 : c;
    internal static double Delta(double c, bool fahrenheit) => fahrenheit ? c * 9 / 5 : c;
}

/// <summary>One component's expected-vs-measured block on the Device page.</summary>
public partial class ComponentIntelViewModel : ObservableObject
{
    // NVMe controllers are comfortable to ~70 °C and start throttling around 80 —
    // silicon behavior, not chassis-specific, so no knowledge profile is needed.
    // Matches the 70 °C hot-SSD remark and the tray readout.
    private static readonly ComponentProfile SsdEnvelope = new(0, 0, 70, 80);

    private readonly ComponentProfile? _spec;
    private readonly double _fallbackLimitC;

    public string Label { get; }
    public bool HasProfile => _spec is not null;
    /// <summary>Idle/heavy expected-temperature rows — pasteable parts only.</summary>
    public bool ShowRises { get; }
    /// <summary>SMART media-wear row — SSD only.</summary>
    public bool ShowWear { get; }

    [ObservableProperty] private string _idleExpectedText = "-";
    [ObservableProperty] private string _heavyExpectedText = "-";
    [ObservableProperty] private string _sustainedText = "-";
    [ObservableProperty] private string _concernText = "-";
    [ObservableProperty] private string _wearText = "--";
    [ObservableProperty] private string _basisText = "";
    [ObservableProperty] private string _nowText = "no reading yet";
    [ObservableProperty] private double _colorFraction;
    [ObservableProperty] private bool _hasReading;
    [ObservableProperty] private double _stripValue;
    [ObservableProperty] private double _stripMin = 25;
    [ObservableProperty] private double _stripNorm = 80;
    [ObservableProperty] private double _stripConcern = 95;
    [ObservableProperty] private double _stripMax = 102;

    public ComponentIntelViewModel(string label, ComponentProfile? spec)
        : this(label, spec, showRises: true, showWear: false, fallbackLimitC: 100) { }

    private ComponentIntelViewModel(string label, ComponentProfile? spec,
        bool showRises, bool showWear, double fallbackLimitC)
    {
        Label = label;
        _spec = spec;
        ShowRises = showRises;
        ShowWear = showWear;
        _fallbackLimitC = fallbackLimitC;
    }

    public static ComponentIntelViewModel ForSsd() =>
        new("SSD", SsdEnvelope, showRises: false, showWear: true, fallbackLimitC: 90);

    public void Update(ComponentReading? reading, double? ambientC, double roomOffsetC, bool fahrenheit)
    {
        string unit = fahrenheit ? "°F" : "°C";
        double? roomC = ambientC + roomOffsetC;

        if (_spec is { } spec)
        {
            if (ShowRises)
            {
                // Expected temps track the current outside reference instead of quoting
                // the raw "+N° over ambient" delta: silicon idles against its own heat
                // floor, so in cold weather the literal delta reads huge on a perfectly
                // healthy machine (the 40°-die-at-0°-outside loophole). Cold holds the
                // room-anchored figure; warm raises it (ProfileExpectation).
                double idle = ProfileExpectation.ExpectedTempC(spec.TypicalIdleDeltaC, roomC, spec.ConcernC);
                double heavy = ProfileExpectation.ExpectedTempC(spec.TypicalHeavyDeltaC, roomC, spec.ConcernC);
                IdleExpectedText = $"≈{DeviceViewModel.Temp(idle, fahrenheit):0}{unit}";
                HeavyExpectedText = $"≈{DeviceViewModel.Temp(heavy, fahrenheit):0}{unit}";
                BasisText = roomC is { } r
                    ? $"expectations for {DeviceViewModel.Temp(r, fahrenheit):0.#}° outside now"
                    : $"expectations for a {DeviceViewModel.Temp(ProfileExpectation.ReferenceAmbientC, fahrenheit):0}° room";
            }
            SustainedText = $"{DeviceViewModel.Temp(spec.SustainedNormC, fahrenheit):0}{unit}";
            ConcernText = $"above {DeviceViewModel.Temp(spec.ConcernC, fahrenheit):0}{unit}";

            StripMin = DeviceViewModel.Temp(25, fahrenheit);
            StripNorm = DeviceViewModel.Temp(spec.SustainedNormC, fahrenheit);
            StripConcern = DeviceViewModel.Temp(spec.ConcernC, fahrenheit);
            StripMax = DeviceViewModel.Temp(spec.ConcernC + 7, fahrenheit);
        }

        if (ShowWear)
        {
            WearText = reading?.WearPercent is { } wear ? $"{wear:0}% used" : "--";
            BasisText = reading?.Name ?? "";
        }

        if (reading?.TemperatureC is { } t)
        {
            HasReading = true;
            StripValue = DeviceViewModel.Temp(t, fahrenheit);
            double limit = reading.ThrottleLimitC ?? _fallbackLimitC;
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

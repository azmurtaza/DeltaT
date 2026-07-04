using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kelvin.App.Services;
using Kelvin.Core.Knowledge;
using Kelvin.Core.Machine;
using Kelvin.Core.Monitoring;
using Kelvin.Core.Scoring;
using Kelvin.Core.Storage;
using Kelvin.Core.Weather;

namespace Kelvin.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly AmbientService _ambient;
    private readonly ScoreCoordinator _scores;
    private readonly KelvinDb _db;
    private readonly Func<SensorSnapshot?> _latest;

    public string MachineText { get; }
    public string ProfileNotes { get; }
    public bool AutostartAvailable { get; }

    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private string _cityQuery = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private bool _fahrenheit;
    [ObservableProperty] private double _indoorOffset;
    [ObservableProperty] private int _sampleIntervalSeconds;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private string _dbText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _limitsText = "";

    public ObservableCollection<GeoLocation> CityResults { get; } = new();

    public SettingsViewModel(
        SettingsStore settings, AmbientService ambient, ScoreCoordinator scores,
        MachineIdentity machine, ThermalProfile profile, KelvinDb db,
        Func<SensorSnapshot?> latest, bool simulated)
    {
        _settings = settings;
        _ambient = ambient;
        _scores = scores;
        _db = db;
        _latest = latest;

        MachineText = $"{machine.Display}\n{machine.CpuName}\n{string.Join("\n", machine.GpuNames)}\n"
                    + $"{(machine.IsLaptop ? "Laptop" : "Desktop")} · profile: {profile.DisplayName}";
        ProfileNotes = profile.Notes ?? "";
        AutostartAvailable = !simulated;

        _fahrenheit = settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        _indoorOffset = settings.GetDouble(SettingsKeys.IndoorOffsetC) ?? 0;
        _sampleIntervalSeconds = settings.GetInt(SettingsKeys.SampleIntervalSeconds) ?? 2;
        _closeToTray = settings.GetBool(SettingsKeys.CloseToTray, true);
        _notificationsEnabled = settings.GetBool(SettingsKeys.NotificationsEnabled, true);
        _autostartEnabled = AutostartAvailable && AutostartService.IsEnabled();

        RefreshInfo();
    }

    public void RefreshInfo()
    {
        GeoLocation? loc = _ambient.Location;
        LocationText = loc is null
            ? "No location set — auto-detect or search below."
            : $"{loc.Display}   ({(loc.Source == "ip" ? "auto-detected" : "set manually")})";

        try
        {
            var info = new FileInfo(_db.DbPath);
            DbText = info.Exists ? $"{_db.DbPath}\n{info.Length / 1024.0 / 1024.0:0.##} MB" : _db.DbPath;
        }
        catch { DbText = _db.DbPath; }

        SensorSnapshot? snap = _latest();
        var limits = new List<string>();
        if (snap?.Find(ComponentKind.Cpu)?.ThrottleLimitC is { } cpuLimit)
            limits.Add($"CPU TjMax {cpuLimit:0}° (read from the silicon)");
        if (snap?.Find(ComponentKind.GpuDiscrete)?.ThrottleLimitC is { } gpuLimit)
            limits.Add($"GPU throttle point {gpuLimit:0}°");
        LimitsText = string.Join("\n", limits);
    }

    partial void OnFahrenheitChanged(bool value) => _settings.SetBool(SettingsKeys.UnitsFahrenheit, value);

    partial void OnIndoorOffsetChanged(double value) =>
        _settings.SetDouble(SettingsKeys.IndoorOffsetC, Math.Round(value, 1));

    partial void OnSampleIntervalSecondsChanged(int value)
    {
        _settings.SetInt(SettingsKeys.SampleIntervalSeconds, Math.Clamp(value, 1, 10));
        StatusText = "Sampling interval applies on next launch.";
    }

    partial void OnCloseToTrayChanged(bool value) => _settings.SetBool(SettingsKeys.CloseToTray, value);

    partial void OnNotificationsEnabledChanged(bool value) => _settings.SetBool(SettingsKeys.NotificationsEnabled, value);

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (!AutostartAvailable) return;
        bool ok = value ? AutostartService.Enable() : AutostartService.Disable();
        StatusText = ok
            ? value ? "Kelvin will start with Windows (elevated, no UAC prompt)." : "Autostart removed."
            : "Task Scheduler said no — is Kelvin running elevated?";
    }

    [RelayCommand]
    private async Task AutoDetectAsync()
    {
        Searching = true;
        StatusText = "Detecting location…";
        GeoLocation? loc = await _ambient.TryAutoLocateAsync();
        Searching = false;
        if (loc is null)
        {
            StatusText = "Auto-detect failed — search for your city instead.";
            return;
        }
        _ambient.SetLocation(loc);
        StatusText = $"Location set to {loc.Display}.";
        RefreshInfo();
    }

    [RelayCommand]
    private async Task SearchCityAsync()
    {
        if (string.IsNullOrWhiteSpace(CityQuery)) return;
        Searching = true;
        CityResults.Clear();
        IReadOnlyList<GeoLocation> results = await _ambient.SearchCityAsync(CityQuery.Trim());
        foreach (GeoLocation r in results)
            CityResults.Add(r);
        Searching = false;
        StatusText = results.Count == 0 ? "No matches — try a bigger nearby city." : "";
    }

    [RelayCommand]
    private void PickCity(GeoLocation location)
    {
        _ambient.SetLocation(location);
        CityResults.Clear();
        CityQuery = "";
        StatusText = $"Location set to {location.Display}.";
        RefreshInfo();
    }

    [RelayCommand]
    private void RegisterRepaste()
    {
        MessageBoxResult answer = MessageBox.Show(
            "Tell Kelvin you just replaced the thermal paste?\n\n"
            + "The learned baseline resets and a fresh learning week starts. "
            + "Once the new baseline locks, Kelvin reports exactly what the repaste bought you.",
            "Kelvin — repaste", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _scores.RegisterRepaste(DateTimeOffset.UtcNow);
        StatusText = "Repaste logged. New baseline learning started — verdict in about a week.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"kelvin-export-{DateTime.Now:yyyyMMdd}.csv",
            Filter = "CSV files|*.csv",
        };
        if (dialog.ShowDialog() != true) return;
        StatusText = "Exporting…";
        try
        {
            int rows = await Task.Run(() => CsvExporter.Export(_db, dialog.FileName));
            StatusText = $"Exported {rows:n0} minute-rows to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }
}

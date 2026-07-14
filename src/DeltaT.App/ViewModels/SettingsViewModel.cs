using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Services;
using DeltaT.Core.Knowledge;
using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Updates;
using DeltaT.Core.Weather;

namespace DeltaT.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly AmbientService _ambient;
    private readonly ScoreCoordinator _scores;
    private readonly DeltaTDb _db;
    private readonly UpdateService _updates;
    private readonly MonitoringService _monitor;
    private readonly double _cpuConcernDefault;
    private readonly double _gpuConcernDefault;

    public bool AutostartAvailable { get; }

    /// <summary>Self-update is meaningless against the simulated store, so hide it there.</summary>
    public bool UpdatesAvailable { get; }

    public string CurrentVersionText => UpdateService.CurrentVersionLabel;

    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private string _cityQuery = "";
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private bool _fahrenheit;
    [ObservableProperty] private double _indoorOffset;
    [ObservableProperty] private int _sampleIntervalSeconds;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private bool _autoUpdateEnabled;
    [ObservableProperty] private bool _checkingUpdate;
    [ObservableProperty] private bool _captureEnabled;
    [ObservableProperty] private double _cpuConcernC;
    [ObservableProperty] private double _gpuConcernC;
    [ObservableProperty] private bool _headroomWarnings;
    [ObservableProperty] private string _dbText = "";
    [ObservableProperty] private string _statusText = "";

    public ObservableCollection<GeoLocation> CityResults { get; } = new();

    public SettingsViewModel(
        SettingsStore settings, AmbientService ambient, ScoreCoordinator scores,
        MachineIdentity machine, ThermalProfile profile, DeltaTDb db,
        UpdateService updates, MonitoringService monitor, Func<SensorSnapshot?> latest, bool simulated,
        bool showUpdatesPanel = false)
    {
        _settings = settings;
        _ambient = ambient;
        _scores = scores;
        _db = db;
        _updates = updates;
        _monitor = monitor;

        AutostartAvailable = !simulated;
        // The auto-update panel is hidden under --simulate (no real update flow), but
        // the screenshot harness forces it on so promo shots show the shipping layout.
        UpdatesAvailable = !simulated || showUpdatesPanel;

        _fahrenheit = settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        _indoorOffset = settings.GetDouble(SettingsKeys.IndoorOffsetC) ?? 0;
        _sampleIntervalSeconds = settings.GetInt(SettingsKeys.SampleIntervalSeconds) ?? 2;
        _closeToTray = settings.GetBool(SettingsKeys.CloseToTray, true);
        _notificationsEnabled = settings.GetBool(SettingsKeys.NotificationsEnabled, true);
        _autostartEnabled = AutostartAvailable && AutostartService.IsEnabled();
        _autoUpdateEnabled = updates.AutoUpdateEnabled;
        _captureEnabled = settings.GetBool(SettingsKeys.CaptureEnabled, true);

        // Custom warning limits. The field shows the profile's own concern number as the
        // starting point; setting it back to (near) that number means "use the default".
        _cpuConcernDefault = profile.Cpu?.ConcernC ?? 95;
        _gpuConcernDefault = profile.Gpu?.ConcernC ?? 90;
        _cpuConcernC = settings.GetDouble(SettingsKeys.ConcernOverrideCpuC) ?? _cpuConcernDefault;
        _gpuConcernC = settings.GetDouble(SettingsKeys.ConcernOverrideGpuC) ?? _gpuConcernDefault;
        _headroomWarnings = settings.GetBool(SettingsKeys.HeadroomWarnings, true);

        RefreshInfo();
    }

    public void RefreshInfo()
    {
        GeoLocation? loc = _ambient.Location;
        LocationText = loc is null
            ? "No location set. Auto-detect or search below."
            : $"{loc.Display}   ({(loc.Source == "ip" ? "auto-detected" : "set manually")})";

        try
        {
            var info = new FileInfo(_db.DbPath);
            DbText = info.Exists ? $"{_db.DbPath}\n{info.Length / 1024.0 / 1024.0:0.##} MB" : _db.DbPath;
        }
        catch { DbText = _db.DbPath; }
    }

    partial void OnFahrenheitChanged(bool value) => _settings.SetBool(SettingsKeys.UnitsFahrenheit, value);

    partial void OnIndoorOffsetChanged(double value) =>
        _settings.SetDouble(SettingsKeys.IndoorOffsetC, Math.Round(value, 1));

    partial void OnSampleIntervalSecondsChanged(int value)
    {
        _settings.SetInt(SettingsKeys.SampleIntervalSeconds, Math.Clamp(value, 1, 10));
        StatusText = "Sampling interval applies on next launch.";
    }

    partial void OnCaptureEnabledChanged(bool value)
    {
        _settings.SetBool(SettingsKeys.CaptureEnabled, value);
        _monitor.IsPaused = !value;
        StatusText = value
            ? "Background monitoring on. DeltaT samples your sensors and keeps learning."
            : "Background monitoring paused. No sensor polling until you switch it back on, so nothing can touch in-game performance.";
    }

    partial void OnHeadroomWarningsChanged(bool value)
    {
        _settings.SetBool(SettingsKeys.HeadroomWarnings, value);
        StatusText = value
            ? "Headroom warnings on."
            : "Headroom warnings off. DeltaT won't flag peaks that sit near the silicon limit, though real throttling still counts.";
    }

    partial void OnCpuConcernCChanged(double value) => SaveConcern(SettingsKeys.ConcernOverrideCpuC, value, _cpuConcernDefault, "CPU");

    partial void OnGpuConcernCChanged(double value) => SaveConcern(SettingsKeys.ConcernOverrideGpuC, value, _gpuConcernDefault, "GPU");

    /// <summary>Persist a concern override, or clear it back to the chassis default when
    /// the user parks the value on (or below) the profile's own number.</summary>
    private void SaveConcern(string key, double value, double defaultC, string label)
    {
        if (value <= 0 || Math.Abs(value - defaultC) < 0.5)
        {
            _settings.Set(key, "");
            StatusText = $"{label} warning limit back to this chassis's default ({defaultC:0} °C).";
        }
        else
        {
            _settings.SetDouble(key, Math.Round(value, 0));
            StatusText = $"{label} warning limit set to {value:0} °C. DeltaT only flags sustained heat past that.";
        }
    }

    partial void OnCloseToTrayChanged(bool value) => _settings.SetBool(SettingsKeys.CloseToTray, value);

    partial void OnNotificationsEnabledChanged(bool value) => _settings.SetBool(SettingsKeys.NotificationsEnabled, value);

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (!AutostartAvailable) return;
        bool ok = value ? AutostartService.Enable() : AutostartService.Disable();
        StatusText = ok
            ? value ? "DeltaT will start with Windows (elevated, no UAC prompt)." : "Autostart removed."
            : "Task Scheduler said no. Is DeltaT running elevated?";
    }

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        _updates.AutoUpdateEnabled = value;
        StatusText = value
            ? "DeltaT will install new versions automatically on startup."
            : "Auto-update off. Check for new versions manually below.";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!UpdatesAvailable || CheckingUpdate) return;
        CheckingUpdate = true;
        StatusText = "Checking for updates…";
        try
        {
            ReleaseInfo? release = await _updates.CheckAsync();
            if (release is null)
            {
                StatusText = $"You're on the latest version ({CurrentVersionText}).";
                return;
            }

            MessageBoxResult answer = MessageBox.Show(
                $"DeltaT {release.Tag} is available. You have {CurrentVersionText}.\n\n"
                + "Download and install it now? DeltaT will close and reopen once it's done.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = $"Update {release.Tag} is ready whenever you are.";
                return;
            }

            StatusText = $"Downloading DeltaT {release.Tag}…";
            string? installer = await _updates.DownloadAsync(release);
            if (installer is null)
            {
                StatusText = "Download failed. Try again, or grab the installer from GitHub.";
                return;
            }

            StatusText = "Installing… DeltaT will restart in a moment.";
            _updates.ApplyAndRelaunch(installer); // shuts the app down; the helper relaunches it
        }
        catch (Exception ex)
        {
            StatusText = $"Update check failed: {ex.Message}";
        }
        finally
        {
            CheckingUpdate = false;
        }
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
            StatusText = "Auto-detect failed. Search for your city instead.";
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
        StatusText = results.Count == 0 ? "No matches. Try a bigger nearby city." : "";
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
            "Tell DeltaT you just replaced the thermal paste?\n\n"
            + "The learned baseline resets and relearning starts (fresh paste needs a few days "
            + "to settle, so the lock waits for that). Once the new baseline locks, DeltaT "
            + "reports exactly what the repaste bought you. History and trends stay put.",
            "Log a repaste", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _scores.RegisterRepaste(DateTimeOffset.UtcNow);
        StatusText = "Repaste logged. New baseline learning started. The verdict lands once the fresh paste settles and enough load is seen.";
    }

    [RelayCommand]
    private void Recalibrate()
    {
        MessageBoxResult answer = MessageBox.Show(
            "Recalibrate the learned baseline?\n\n"
            + "Use this when the cooling changed but the paste didn't (new fans, a clean-out, "
            + "a moved rig), or after DeltaT has been off for a long stretch. DeltaT re-checks "
            + "the machine against its old baseline under real load: if nothing actually changed, "
            + "the old reference is kept and scoring resumes within the hour; only genuine change "
            + "is relearned. History and trends are never touched.",
            "Recalibrate", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _scores.Recalibrate(DateTimeOffset.UtcNow);
        StatusText = "Recalibration started. DeltaT verifies against the old baseline under load and relearns only what changed.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"deltat-export-{DateTime.Now:yyyyMMdd}.csv",
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

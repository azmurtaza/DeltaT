using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.Core.Machine;
using DeltaT.Core.Storage;
using DeltaT.Core.Weather;

namespace DeltaT.App.ViewModels;

public partial class OnboardingViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly AmbientService _ambient;

    public string MachineLine { get; }

    [ObservableProperty] private string _locationStatus = "No location yet.";
    [ObservableProperty] private bool _busy;

    public event Action? Completed;

    public OnboardingViewModel(SettingsStore settings, AmbientService ambient, MachineIdentity machine)
    {
        _settings = settings;
        _ambient = ambient;
        MachineLine = $"Detected: {machine.Display} — {(machine.IsLaptop ? "laptop" : "desktop")}.";
        if (ambient.Location is { } loc)
            LocationStatus = $"Location: {loc.Display} ✓";

        // Background auto-locate may finish after this VM is built; reflect it when it does.
        ambient.Updated += _ => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!Busy && _ambient.Location is { } l)
                LocationStatus = $"Location: {l.Display} ✓ — outside temperature refreshes every 3 h.";
        });
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        Busy = true;
        LocationStatus = "Looking up your city from your IP (one time, then cached)…";
        GeoLocation? loc = await _ambient.TryAutoLocateAsync();
        if (loc is not null)
        {
            _ambient.SetLocation(loc);
            LocationStatus = $"Location: {loc.Display} ✓ — outside temperature will refresh every 3 h.";
        }
        else
        {
            LocationStatus = "Couldn't auto-detect. You can set your city later in Settings — DeltaT works without it, just less weather-smart.";
        }
        Busy = false;
    }

    [RelayCommand]
    private void Start()
    {
        _settings.SetBool(SettingsKeys.FirstRunDone, true);
        Completed?.Invoke();
    }
}

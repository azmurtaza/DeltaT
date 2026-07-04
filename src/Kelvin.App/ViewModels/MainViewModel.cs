using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kelvin.App.Controls;
using Kelvin.Core.Knowledge;
using Kelvin.Core.Machine;
using Kelvin.Core.Monitoring;
using Kelvin.Core.Remarks;
using Kelvin.Core.Scoring;
using Kelvin.Core.Storage;
using Kelvin.Core.Weather;

namespace Kelvin.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly ComponentKind[] CardOrder =
    {
        ComponentKind.Cpu, ComponentKind.GpuDiscrete, ComponentKind.Storage, ComponentKind.Battery,
    };

    private readonly Dispatcher _dispatcher;
    private readonly AmbientService _ambient;
    private readonly ScoreCoordinator _scores;
    private readonly SettingsStore _settings;
    private readonly Dictionary<string, ComponentCardViewModel> _cardsById = new();

    public string MachineName { get; }
    public string ProfileName { get; }
    public ObservableCollection<ComponentCardViewModel> Cards { get; } = new();
    public ScoreViewModel CpuScore { get; } = new("CPU");
    public ScoreViewModel GpuScore { get; } = new("GPU");

    public TrendsViewModel Trends { get; }
    public RemarksViewModel RemarksFeed { get; }
    public SettingsViewModel Settings { get; }
    public OnboardingViewModel Onboarding { get; }
    public bool IsFirstRun { get; }

    [ObservableProperty] private string _verdictTitle = "Learning your machine";
    [ObservableProperty] private string _verdictDetail = "Kelvin watches temperature rise over the weather outside and compares this machine against itself. First verdict lands after about a week of normal use.";
    [ObservableProperty] private string _weatherText = "locating…";
    [ObservableProperty] private string _weatherTooltip = "Resolving your location (cached afterwards — Kelvin never tracks you)";
    [ObservableProperty] private bool _weatherStale;
    [ObservableProperty] private string _latestRemark = "Kelvin is warming up…";
    [ObservableProperty] private string _latestRemarkTime = "";
    [ObservableProperty] private Brush _remarkDot = new SolidColorBrush(ThermalPalette.Accent);
    [ObservableProperty] private bool _simulated;

    /// <summary>UI updates pause entirely while the window is hidden (perf budget).</summary>
    public bool UiVisible { get; set; } = true;

    public MainViewModel(
        MachineIdentity machine,
        ThermalProfile profile,
        MonitoringService monitor,
        AmbientService ambient,
        ScoreCoordinator scores,
        SettingsStore settings,
        TrendsViewModel trends,
        RemarksViewModel remarksFeed,
        SettingsViewModel settingsVm,
        OnboardingViewModel onboarding)
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _ambient = ambient;
        _scores = scores;
        _settings = settings;
        MachineName = machine.Display;
        ProfileName = profile.DisplayName;
        Trends = trends;
        RemarksFeed = remarksFeed;
        Settings = settingsVm;
        Onboarding = onboarding;
        IsFirstRun = !settings.GetBool(SettingsKeys.FirstRunDone, false);

        monitor.SnapshotCaptured += snap =>
        {
            if (!UiVisible && _cardsById.Count > 0)
                return;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnSnapshot(snap));
        };
        ambient.Updated += _ => _dispatcher.BeginInvoke(UpdateWeather);
        scores.ScoresUpdated += dict => _dispatcher.BeginInvoke(() => UpdateScores(dict));

        UpdateWeather();
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        double? ambient = _ambient.CurrentAmbientC;
        bool fahrenheit = _settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        double roomOffset = _settings.GetDouble(SettingsKeys.IndoorOffsetC) ?? 0;

        foreach (ComponentKind kind in CardOrder)
        {
            foreach (ComponentReading reading in snap.Components.Where(c => c.Kind == kind))
            {
                if (!_cardsById.TryGetValue(reading.Id, out ComponentCardViewModel? card))
                {
                    card = new ComponentCardViewModel(reading);
                    _cardsById[reading.Id] = card;
                    Cards.Add(card);
                }
                card.Update(reading, ambient, roomOffset, fahrenheit);
            }
        }
    }

    private void UpdateWeather()
    {
        AmbientReading? reading = _ambient.Current;
        if (reading is null)
        {
            WeatherText = _ambient.Location is null ? "location unknown" : "weather offline";
            WeatherTooltip = _ambient.Location is null
                ? "Couldn't resolve a location automatically — set one in Settings."
                : "Weather service unreachable; using the last known value once it exists.";
            WeatherStale = true;
            return;
        }
        bool fahrenheit = _settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        double shown = fahrenheit ? reading.OutsideC * 9 / 5 + 32 : reading.OutsideC;
        WeatherText = $"{shown:0.#}°  ·  {reading.Location.City}";
        WeatherStale = _ambient.IsStale;
        double minutes = Math.Max(1, (DateTimeOffset.UtcNow - reading.FetchedUtc).TotalMinutes);
        string age = minutes > 90 ? $"{minutes / 60:0.#} h ago" : $"{minutes:0} min ago";
        WeatherTooltip = $"Outside temperature in {reading.Location.Display} — fetched {age}. Refreshes every 3 h.";
    }

    private void UpdateScores(IReadOnlyDictionary<ComponentKind, ComponentScore> scores)
    {
        if (scores.TryGetValue(ComponentKind.Cpu, out ComponentScore? cpu))
            CpuScore.Update(cpu);
        if (scores.TryGetValue(ComponentKind.GpuDiscrete, out ComponentScore? gpu))
            GpuScore.Update(gpu);

        var all = scores.Values.ToList();
        if (all.Count == 0)
            return;

        if (all.All(s => s.Calibrating))
        {
            double progress = all.Max(s => s.CalibrationProgress);
            int day = Math.Max(1, (int)(DateTimeOffset.UtcNow - _scores.EpochStart).TotalDays + 1);
            VerdictTitle = $"Learning your machine — day {day}";
            VerdictDetail = $"Baseline {progress * 100:0}% assembled. Use the machine normally; games and heavy work teach Kelvin fastest. Hard limits are enforced from day one.";
            return;
        }

        ComponentScore worst = all.Where(s => !s.Calibrating).OrderBy(s => s.Value).First();
        VerdictTitle = $"{worst.Kind.Label()}: {worst.Verdict.Label()}";
        VerdictDetail = worst.Reasons.Count > 0
            ? worst.Reasons[0].Text + (worst.Hint switch
              {
                  PatternHint.LooksLikeDust => "  Pattern points at dust/airflow more than paste — try compressed air first.",
                  PatternHint.LooksLikePaste => "  Pattern (fast heat-soak, throttling) points squarely at the paste.",
                  PatternHint.Mixed => "  Signals are mixed — likely both dust and aging paste.",
                  _ => "",
              })
            : "All quiet.";
    }

    public void OnRemark(Remark remark)
    {
        LatestRemark = remark.Text;
        LatestRemarkTime = remark.TimestampUtc.ToLocalTime().ToString("HH:mm");
        Color c = remark.Severity switch
        {
            RemarkSeverity.Alert => ThermalPalette.Hot,
            RemarkSeverity.Warning => ThermalPalette.HotWarn,
            RemarkSeverity.Notice => ThermalPalette.Accent,
            _ => ThermalPalette.TextDim,
        };
        RemarkDot = new SolidColorBrush(c);
        RemarksFeed.Prepend(remark);
    }
}

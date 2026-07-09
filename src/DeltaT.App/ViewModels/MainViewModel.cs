using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Controls;
using DeltaT.Core.Knowledge;
using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Remarks;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Weather;

namespace DeltaT.App.ViewModels;

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
    private readonly MonitoringService _monitor;
    private readonly Dictionary<string, ComponentCardViewModel> _cardsById = new();
    private SensorSnapshot? _pendingSnapshot;
    private DispatcherTimer? _healthTimer;
    private bool _needsAdmin;

    public string MachineName { get; }
    public string ProfileName { get; }
    public ObservableCollection<ComponentCardViewModel> Cards { get; } = new();
    public ScoreViewModel CpuScore { get; } = new("CPU");
    public ScoreViewModel GpuScore { get; } = new("GPU");

    public TrendsViewModel Trends { get; }
    public RemarksViewModel RemarksFeed { get; }
    public SettingsViewModel Settings { get; }
    public DeviceViewModel Device { get; }
    public OnboardingViewModel Onboarding { get; }
    public bool IsFirstRun { get; }

    [ObservableProperty] private string _verdictTitle = "Learning your machine";
    [ObservableProperty] private string _verdictDetail = "DeltaT watches temperature rise over the weather outside and compares this machine against itself. First verdict lands after about a week of normal use.";
    [ObservableProperty] private string _scoringBasis = "Normalized for weather · load";
    [ObservableProperty] private string _fanNote = "";
    [ObservableProperty] private bool _fanNoteActive;
    [ObservableProperty] private string _weatherText = "locating…";
    [ObservableProperty] private string _weatherTooltip = "Resolving your location (cached afterwards - DeltaT never tracks you)";
    [ObservableProperty] private bool _weatherStale;
    [ObservableProperty] private string _latestRemark = "DeltaT is warming up…";
    [ObservableProperty] private string _latestRemarkTime = "";
    [ObservableProperty] private Brush _remarkDot = new SolidColorBrush(ThermalPalette.Accent);
    [ObservableProperty] private bool _simulated;
    private bool _hasFanSensor;

    /// <summary>Non-empty when the readings can't be trusted right now
    /// (monitoring paused, sensors stalled, CPU sensor locked behind elevation).
    /// The dashboard shows it as a warning strip — stale data must never pass
    /// silently as live data.</summary>
    [ObservableProperty] private string _sensorNotice = "";

    /// <summary>True when the CPU sensor is dark purely because DeltaT isn't elevated —
    /// drives the dashboard's one-click "Restart as administrator" recovery.</summary>
    [ObservableProperty] private bool _elevationOffered;

    /// <summary>Whether the process holds admin rights (CPU temps need the kernel driver).</summary>
    public bool Elevated { get; init; } = true;

    /// <summary>Invoked to relaunch DeltaT elevated (wired to App by the composition root).</summary>
    public Action? RequestElevation { get; init; }

    [RelayCommand]
    private void RestartAsAdmin() => RequestElevation?.Invoke();

    private bool _uiVisible = true;

    /// <summary>UI updates pause entirely while the window is hidden (perf budget).
    /// On re-show, the latest reading is applied immediately — never a stale frame.</summary>
    public bool UiVisible
    {
        get => _uiVisible;
        set
        {
            if (_uiVisible == value)
                return;
            _uiVisible = value;
            if (value)
            {
                if (_monitor.Latest is { } latest)
                    OnSnapshot(latest);
                UpdateSensorNotice();
                _healthTimer ??= CreateHealthTimer();
                _healthTimer.Start();
            }
            else
            {
                _healthTimer?.Stop();
            }
        }
    }

    private DispatcherTimer CreateHealthTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => UpdateSensorNotice();
        return timer;
    }

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
        DeviceViewModel deviceVm,
        OnboardingViewModel onboarding)
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _ambient = ambient;
        _scores = scores;
        _settings = settings;
        _monitor = monitor;
        MachineName = machine.Display;
        ProfileName = profile.DisplayName;
        Trends = trends;
        RemarksFeed = remarksFeed;
        Settings = settingsVm;
        Device = deviceVm;
        Onboarding = onboarding;
        IsFirstRun = !settings.GetBool(SettingsKeys.FirstRunDone, false);

        // Latest-wins delivery: at most one dispatcher hop is ever in flight, and
        // it always applies the newest snapshot. The old per-snapshot Background-
        // priority queue could be starved for minutes by a full-tilt CPU (stress
        // tests, games), leaving the dashboard frozen at pre-load temperatures.
        monitor.SnapshotCaptured += snap =>
        {
            if (!UiVisible && _cardsById.Count > 0)
                return;
            if (Interlocked.Exchange(ref _pendingSnapshot, snap) is null)
                _dispatcher.BeginInvoke(DispatcherPriority.DataBind, DrainPendingSnapshot);
        };
        ambient.Updated += _ => _dispatcher.BeginInvoke(UpdateWeather);
        scores.ScoresUpdated += dict => _dispatcher.BeginInvoke(() => UpdateScores(dict));

        UpdateWeather();
        _healthTimer = CreateHealthTimer();
        _healthTimer.Start();
    }

    private void DrainPendingSnapshot()
    {
        if (Interlocked.Exchange(ref _pendingSnapshot, null) is { } snap)
            OnSnapshot(snap);
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        double? ambient = _ambient.CurrentAmbientC;
        bool fahrenheit = _settings.GetBool(SettingsKeys.UnitsFahrenheit, false);
        double roomOffset = _settings.GetDouble(SettingsKeys.IndoorOffsetC) ?? 0;

        foreach (ComponentKind kind in CardOrder)
        {
            for (int i = 0; i < snap.Components.Count; i++)
            {
                ComponentReading reading = snap.Components[i];
                if (reading.Kind != kind)
                    continue;
                if (!_cardsById.TryGetValue(reading.Id, out ComponentCardViewModel? card))
                {
                    card = new ComponentCardViewModel(reading);
                    _cardsById[reading.Id] = card;
                    Cards.Add(card);
                }
                card.Update(reading, ambient, roomOffset, fahrenheit);
            }
        }

        _needsAdmin = !Simulated && !Elevated && snap.Find(ComponentKind.Cpu) is { TemperatureC: null };
        if (!_hasFanSensor && snap.Components.Any(c => c.FanRpm.HasValue))
            OnFanSensorFound();
    }

    private void OnFanSensorFound()
    {
        _hasFanSensor = true;
        ScoringBasis = "Normalized for weather · load · fan speed";
        if (!FanNoteActive && FanNote.Length > 0)
            FanNote = "";
    }

    /// <summary>Runs every 5 s while the window is visible; never while hidden.</summary>
    private void UpdateSensorNotice()
    {
        string notice = "";
        if (_monitor.IsPaused)
        {
            notice = "MONITORING PAUSED - resume from the tray menu; readings below are frozen";
        }
        else if (_monitor.Latest is { } latest)
        {
            double ageSeconds = (DateTimeOffset.UtcNow - latest.TimestampUtc).TotalSeconds;
            double limit = Math.Max(10, _monitor.Interval.TotalSeconds * 4);
            if (ageSeconds > limit)
                notice = $"SENSORS STALLED - last reading {ageSeconds:0} s ago; values shown may be outdated";
            else if (_needsAdmin)
                notice = "CPU TEMPERATURE LOCKED - reading the CPU needs administrator access (the kernel driver). Restart elevated to unlock it.";
        }
        SensorNotice = notice;
        // The recovery button only makes sense for the elevation case, never for a
        // pause/stall (and never in sim, which has no real sensors to unlock).
        ElevationOffered = _needsAdmin && !Simulated && !Elevated;
    }

    private void UpdateWeather()
    {
        AmbientReading? reading = _ambient.Current;
        if (reading is null)
        {
            WeatherText = _ambient.Location is null ? "location unknown" : "weather offline";
            WeatherTooltip = _ambient.Location is null
                ? "Couldn't resolve a location automatically - set one in Settings."
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
        WeatherTooltip = $"Outside temperature in {reading.Location.Display} - fetched {age}. Refreshes every 3 h.";
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

        if (!_hasFanSensor && all.Any(s => s.Fan is not null))
            OnFanSensorFound();

        if (all.All(s => s.Calibrating))
        {
            // Once there's real load to compare, show the estimated verdict (worst
            // first) with its confidence, rather than hiding behind a bare percentage.
            List<ComponentScore> estimates = all.Where(s => s.Provisional).OrderBy(s => s.Value).ToList();
            if (estimates.Count > 0)
            {
                ComponentScore worstEstimate = estimates[0];
                UpdateFanNote(worstEstimate);
                string why = worstEstimate.Reasons.Count > 0 ? worstEstimate.Reasons[0].Text : "";
                VerdictTitle = $"{worstEstimate.Kind.Label()}: {worstEstimate.Verdict.Label()} (estimate)";
                VerdictDetail = $"{why} Still calibrating, {worstEstimate.CalibrationProgress * 100:0}% confident"
                    + (string.IsNullOrWhiteSpace(worstEstimate.CalibrationConstraint) ? "." : $"; {worstEstimate.CalibrationConstraint}.");
                return;
            }

            // Nothing comparable yet: show the furthest-along component and, honestly,
            // the one thing still holding its baseline back — confidence, not a countdown.
            ComponentScore lead = all.OrderByDescending(s => s.CalibrationProgress).First();
            UpdateFanNote(null);
            string constraint = lead.CalibrationConstraint;
            VerdictTitle = $"Learning your machine - {lead.CalibrationProgress * 100:0}% confident";
            VerdictDetail = (string.IsNullOrWhiteSpace(constraint)
                ? "Use the machine normally; games and heavy work teach DeltaT fastest."
                : $"What's next: {constraint}.") + " Hard limits are enforced from day one.";
            return;
        }

        ComponentScore worst = all.Where(s => !s.Calibrating).OrderBy(s => s.Value).First();
        UpdateFanNote(worst);
        VerdictTitle = $"{worst.Kind.Label()}: {worst.Verdict.Label()}";
        VerdictDetail = worst.Reasons.Count > 0
            ? worst.Reasons[0].Text + (worst.Hint switch
              {
                  PatternHint.LooksLikeDust => "  Pattern points at dust/airflow more than paste - try compressed air first.",
                  PatternHint.LooksLikePaste => "  Pattern (fast heat-soak, throttling) points squarely at the paste.",
                  PatternHint.Mixed => "  Signals are mixed - likely both dust and aging paste.",
                  _ => "",
              })
            : "All quiet.";
    }

    private void UpdateFanNote(ComponentScore? worst)
    {
        if (_hasFanSensor)
        {
            if (worst is null)
            {
                FanNote = "";
                FanNoteActive = false;
            }
            else
            {
                (FanNote, FanNoteActive) = ScoreViewModel.DescribeFan(worst);
            }
        }
        else
        {
            FanNote = _monitor.Latest is not null
                ? "Fan speed isn't exposed on this machine (often locked behind vendor software), so airflow can't be scored - the verdict rests on weather-corrected rise."
                : "";
            FanNoteActive = false;
        }
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

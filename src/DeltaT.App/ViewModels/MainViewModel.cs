using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Controls;
using DeltaT.App.Services;
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
    // The dashboard's telemetry list is ordered by THIS array, not by snapshot order, and it
    // doubles as an allowlist: a component kind missing here never gets a card at all.
    private static readonly ComponentKind[] CardOrder =
    {
        ComponentKind.Cpu, ComponentKind.GpuDiscrete, ComponentKind.Storage, ComponentKind.Battery,
        ComponentKind.Ram,
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

    /// <summary>The health matrix: every characteristic the engine judges (paste,
    /// airflow, fans, mount, headroom, power state) as its own CPU/GPU readout, so
    /// a healthy subsystem is visibly healthy instead of silently absent.</summary>
    public IReadOnlyList<AspectColumnViewModel> AspectColumns { get; } = new[]
    {
        new AspectColumnViewModel(HealthAspect.Paste, "PASTE", showSeparator: false),
        new AspectColumnViewModel(HealthAspect.Airflow, "AIRFLOW"),
        new AspectColumnViewModel(HealthAspect.Fans, "FANS"),
        new AspectColumnViewModel(HealthAspect.Mount, "MOUNT"),
        new AspectColumnViewModel(HealthAspect.Headroom, "HEADROOM"),
        new AspectColumnViewModel(HealthAspect.Power, "POWER"),
    };

    public TrendsViewModel Trends { get; }
    public RemarksViewModel RemarksFeed { get; }
    public SettingsViewModel Settings { get; }
    public DeviceViewModel Device { get; }
    public OnboardingViewModel Onboarding { get; }
    public bool IsFirstRun { get; }

    [ObservableProperty] private string _verdictTitle = "Learning your machine";
    // Colors the headline verdict the same way the dial does (green = healthy, amber/red =
    // watch/act), so "Excellent" reads as visibly excellent instead of plain white text.
    // Neutral (Text) while there's no verdict yet to color (calibrating/waiting states).
    [ObservableProperty] private Brush _verdictColor = new SolidColorBrush(ThermalPalette.Text);
    [ObservableProperty] private string _verdictDetail = "DeltaT watches temperature rise over the weather outside and compares this machine against itself. The first verdict lands as soon as it has seen enough real load to be sure. Games and heavy work teach it fastest.";
    [ObservableProperty] private string _scoringBasis = "Normalized for weather · load";
    // While nothing is scored yet, the hero leads with the calibration confidence
    // itself: a big numeral and a fill bar, not a percentage buried in a sentence.
    [ObservableProperty] private bool _heroCalibrating = true;
    [ObservableProperty] private string _calibrationPercent = "0%";
    [ObservableProperty] private double _calibrationPercentValue;

    /// <summary>The hero's instrument readouts (Δ vs baseline, fan correction, power
    /// correction, confidence): the numbers that used to live inside prose, drawn as
    /// numerals with the sentence demoted to the tooltip.</summary>
    public ObservableCollection<HeroStatViewModel> HeroStats { get; } = new();
    // The leading likely cause and its evidence, so the dashboard says WHAT is wrong
    // (airflow, a fan, the paste, a power change), not just how healthy. Empty when
    // there's nothing worth diagnosing.
    [ObservableProperty] private string _diagnosisHeadline = "";
    [ObservableProperty] private string _diagnosisText = "";
    [ObservableProperty] private bool _hasDiagnosis;
    [ObservableProperty] private Brush _diagnosisColor = new SolidColorBrush(ThermalPalette.Accent);
    [ObservableProperty] private string _weatherText = "locating…";
    [ObservableProperty] private string _weatherTooltip = "Resolving your location (cached afterwards, and DeltaT never tracks you)";
    [ObservableProperty] private bool _weatherStale;
    [ObservableProperty] private string _latestRemark = "DeltaT is warming up…";
    [ObservableProperty] private string _latestRemarkTime = "";
    [ObservableProperty] private string _latestRemarkTag = "LOG";
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
        bool fahrenheit = _settings.GetBool(SettingsKeys.UnitsFahrenheit, false);

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
                card.Update(reading, fahrenheit);
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
    }

    /// <summary>Runs every 5 s while the window is visible; never while hidden.</summary>
    private void UpdateSensorNotice()
    {
        string notice = "";
        if (_monitor.IsPaused)
        {
            notice = "MONITORING PAUSED. Resume from the tray menu; readings below are frozen";
        }
        else if (_monitor.Latest is { } latest)
        {
            double ageSeconds = (DateTimeOffset.UtcNow - latest.TimestampUtc).TotalSeconds;
            double limit = Math.Max(10, _monitor.Interval.TotalSeconds * 4);
            if (ageSeconds > limit)
                notice = $"SENSORS STALLED. Last reading {ageSeconds:0} s ago; values shown may be outdated";
            else if (_needsAdmin)
                notice = "CPU TEMPERATURE LOCKED. Reading the CPU needs administrator access (the kernel driver). Restart elevated to unlock it.";
            else if (!Simulated && !PawnIoStatus.IsInstalled)
                notice = "SENSOR DRIVER MISSING. CPU temperature, package power and throttle detection need the PawnIO driver. Install it from Settings.";
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
                ? "Couldn't resolve a location automatically. Set one in Settings."
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
        WeatherTooltip = $"Outside temperature in {reading.Location.Display}, fetched {age}. Refreshes every 3 h.";
    }

    private void UpdateScores(IReadOnlyDictionary<ComponentKind, ComponentScore> scores)
    {
        scores.TryGetValue(ComponentKind.Cpu, out ComponentScore? cpu);
        scores.TryGetValue(ComponentKind.GpuDiscrete, out ComponentScore? gpu);
        if (cpu is not null)
            CpuScore.Update(cpu);
        if (gpu is not null)
            GpuScore.Update(gpu);
        UpdateAspects(cpu, gpu);

        var all = scores.Values.ToList();
        if (all.Count == 0)
            return;

        if (!_hasFanSensor && all.Any(s => s.Fan is not null))
            OnFanSensorFound();

        List<ComponentScore> scored = all.Where(s => s.Scored).OrderBy(s => s.Value).ToList();
        if (scored.Count == 0)
        {
            // Once there's real load to compare, show the estimated verdict (worst
            // first) with its confidence, rather than hiding behind a bare percentage.
            List<ComponentScore> estimates = all.Where(s => s.Provisional).OrderBy(s => s.Value).ToList();
            if (estimates.Count > 0)
            {
                ComponentScore worstEstimate = estimates[0];
                HeroCalibrating = false;
                BuildHeroStats(worstEstimate, calibrating: true);
                VerdictTitle = $"{worstEstimate.Kind.Label()}: {worstEstimate.Verdict.Label()} (estimate)";
                VerdictColor = new SolidColorBrush(ThermalPalette.VerdictColor(worstEstimate.Value));
                VerdictDetail = string.IsNullOrWhiteSpace(worstEstimate.CalibrationConstraint)
                    ? "Still calibrating; the estimate sharpens as more comparable load lands."
                    : $"Still calibrating; {worstEstimate.CalibrationConstraint}.";
                UpdateDiagnosis(worstEstimate);
                return;
            }

            // Every baseline is locked, but nothing comparable has run against them yet,
            // so there is genuinely nothing to score. Say so instead of letting the "100
            // minus evidence" arithmetic report a hollow Excellent.
            List<ComponentScore> waiting = all.Where(s => s.AwaitingData).ToList();
            if (waiting.Count == all.Count)
            {
                ComponentScore first = waiting[0];
                HeroCalibrating = false;
                BuildHeroStats(first, calibrating: false);
                VerdictTitle = "Waiting for a comparable load";
                VerdictColor = new SolidColorBrush(ThermalPalette.Text);
                VerdictDetail = "The baseline is locked, but nothing has run against it since. Play a game, run a render, or run the fingerprint test, and DeltaT will score it.";
                UpdateDiagnosis(null);
                return;
            }

            // Nothing comparable yet: lead with the calibration confidence itself
            // (big numeral, fill bar) and, honestly, the one thing still holding
            // the baseline back — confidence, not a countdown. The headline is the
            // LEAST confident component: the baseline isn't trustworthy until every
            // component locks, so a confident CPU must not paper over a raw GPU.
            ComponentScore lead = all.Where(s => s.Calibrating).OrderBy(s => s.CalibrationProgress).First();
            HeroStats.Clear();
            string constraint = lead.CalibrationConstraint;
            HeroCalibrating = true;
            // Caps at 99 like the dials: "100% confident" while still calibrating reads broken.
            CalibrationPercent = $"{Math.Min(lead.CalibrationProgress * 100, 99):0}%";
            CalibrationPercentValue = Math.Clamp(lead.CalibrationProgress, 0, 1) * 100;
            VerdictTitle = "Learning your machine";
            VerdictColor = new SolidColorBrush(ThermalPalette.Text);
            // Name the component when several are learning: the number belongs to
            // whichever one is furthest behind, so the "what's next" must say which.
            string who = all.Count > 1 ? $" ({lead.Kind.Label()})" : "";
            VerdictDetail = (string.IsNullOrWhiteSpace(constraint)
                ? "Use the machine normally; games and heavy work teach DeltaT fastest."
                : $"What's next{who}: {constraint}.") + " Hard limits are enforced from day one.";
            UpdateDiagnosis(null);
            return;
        }

        ComponentScore worst = scored[0];
        HeroCalibrating = false;
        BuildHeroStats(worst, calibrating: false);
        // CPU and GPU each already carry their own verdict on their own dial, so naming
        // just the worse of the two here ("GPU: Excellent") reads as if the other one
        // wasn't checked. When both agree, say the shared verdict once with no component
        // name; only diverge to naming a specific component when that's the information
        // (this one, not the other, needs attention) or when the worst reading is neither
        // CPU nor GPU (storage/battery have no dial duplicating the headline).
        bool cpuGpuAgree = cpu is { Scored: true } && gpu is { Scored: true } && cpu.Verdict == gpu.Verdict;
        VerdictTitle = cpuGpuAgree && worst.Kind is ComponentKind.Cpu or ComponentKind.GpuDiscrete
            ? worst.Verdict.Label()
            : $"{worst.Kind.Label()}: {worst.Verdict.Label()}";
        VerdictColor = new SolidColorBrush(ThermalPalette.VerdictColor(worst.Value));
        // The verdict's numbers now live in the stat readouts (tooltips carry the
        // sentences), so a locked verdict needs no paragraph under it. A sibling that is
        // STILL learning does need one: components lock independently, the calibration
        // branch above stops running the moment ANY component scores, and that component's
        // dial then reads CALIBRATING with nothing anywhere saying what it is waiting for.
        // So a GPU that locked first silently took the CPU's "what's next" off the
        // dashboard until the CPU locked too.
        VerdictDetail = CalibrationHint(all);
        UpdateDiagnosis(worst);
    }

    /// <summary>"What's next" for the least confident component still learning, to sit
    /// under a sibling's already-locked verdict. Empty once everything has locked, which
    /// collapses the line. Mirrors the wording of the all-calibrating headline, since it
    /// answers the same question for whichever component is still behind.</summary>
    private static string CalibrationHint(List<ComponentScore> all)
    {
        ComponentScore? lead = all
            .Where(s => s.Calibrating)
            .OrderBy(s => s.CalibrationProgress)
            .FirstOrDefault();
        if (lead is null)
            return "";
        // Caps at 99 like the dials: "100% confident" while still calibrating reads broken.
        string pct = $"{Math.Min(lead.CalibrationProgress * 100, 99):0}%";
        return string.IsNullOrWhiteSpace(lead.CalibrationConstraint)
            ? $"{lead.Kind.Label()} is still calibrating ({pct}). Use the machine normally; games and heavy work teach DeltaT fastest."
            : $"{lead.Kind.Label()} is still calibrating ({pct}). What's next: {lead.CalibrationConstraint}.";
    }

    /// <summary>The instrument readouts under the verdict title, built from the same
    /// component the verdict describes. Every value keeps its full sentence as tooltip.</summary>
    private void BuildHeroStats(ComponentScore score, bool calibrating)
    {
        HeroStats.Clear();

        // Δ vs baseline: the headline number, colored by severity.
        string deltaTip = score.Reasons.FirstOrDefault(r => r.Code.StartsWith("delta"))?.Text
                          ?? "Not enough recent comparable load to compare against baseline.";
        HeroStats.Add(new HeroStatViewModel(
            score.ExcessC is { } e ? $"{e:+0.0;-0.0;0.0}°" : "--",
            "Δ VS BASELINE", new SolidColorBrush(ExcessColor(score.ExcessC)), deltaTip));

        // Fans: the correction applied, or the honest reason there isn't one.
        if (score.Fan is { } fan)
        {
            HeroStats.Add(new HeroStatViewModel(
                $"{fan.CorrectionC:+0.#;-0.#}°", "FAN CORRECTION",
                new SolidColorBrush(ThermalPalette.TextDim), ScoreViewModel.DescribeFan(score).Note));
        }
        else if (_hasFanSensor)
        {
            HeroStats.Add(new HeroStatViewModel(
                "MATCHED", "FANS", new SolidColorBrush(ThermalPalette.TextDim),
                "Fans are running at this machine's baseline speed for the load, so no airflow correction was needed."));
        }
        else
        {
            HeroStats.Add(new HeroStatViewModel(
                "--", "FANS", new SolidColorBrush(ThermalPalette.TextFaint),
                "Fan speed isn't exposed on this machine (often locked behind vendor software), so airflow can't be corrected for; the verdict rests on weather-corrected rise."));
        }

        // Power: only when a correction was actually applied (stock rigs stay quiet).
        if (score.Power is { } pw)
        {
            string tip = score.Reasons.FirstOrDefault(r => r.Code == "power-normalized")?.Text
                         ?? $"Corrected {pw.CorrectionC:+0.#;-0.#}° for drawing {pw.RecentW:0} W against a {pw.BaselineW:0} W baseline.";
            HeroStats.Add(new HeroStatViewModel(
                $"{pw.CorrectionC:+0.#;-0.#}°", "POWER CORRECTION",
                new SolidColorBrush(ThermalPalette.TextDim), tip));
        }

        if (calibrating)
        {
            HeroStats.Add(new HeroStatViewModel(
                $"{Math.Min(score.CalibrationProgress * 100, 99):0}%", "CONFIDENCE",
                new SolidColorBrush(ThermalPalette.Accent),
                string.IsNullOrWhiteSpace(score.CalibrationConstraint)
                    ? "How sure DeltaT is of this machine's baseline so far."
                    : $"How sure DeltaT is of this machine's baseline so far. What's next: {score.CalibrationConstraint}."));
        }
    }

    /// <summary>Severity color for the Δ-vs-baseline readout: green when cooler than
    /// baseline, steel on it, then the thermal ramp as the excess grows.</summary>
    private static Color ExcessColor(double? excess) => excess switch
    {
        null => ThermalPalette.TextFaint,
        < -1.5 => ThermalPalette.Good,
        <= 1.5 => ThermalPalette.Cool,
        <= 4 => ThermalPalette.Warm,
        <= 8 => ThermalPalette.HotWarn,
        _ => ThermalPalette.Hot,
    };

    /// <summary>Feed the health matrix. Aspects the engine can't judge yet (or that this
    /// hardware can't measure) stay as faint dashes with an honest tooltip.</summary>
    private void UpdateAspects(ComponentScore? cpu, ComponentScore? gpu)
    {
        foreach (AspectColumnViewModel col in AspectColumns)
        {
            col.Cpu.Update(cpu?.Aspects.FirstOrDefault(a => a.Aspect == col.Aspect), cpu?.Calibrating ?? true);
            col.Gpu.Update(gpu?.Aspects.FirstOrDefault(a => a.Aspect == col.Aspect), gpu?.Calibrating ?? true);
        }
    }

    /// <summary>Surface the leading likely cause, so the dashboard reads as a diagnosis
    /// ("looks like airflow", "a fan is slowing", "the paste") instead of a bare number.
    /// A healthy machine, or one still calibrating, shows no cause chip.</summary>
    private void UpdateDiagnosis(ComponentScore? score)
    {
        if (score?.Diagnosis is not { } dx || dx.IsHealthy || score.Value >= 90)
        {
            HasDiagnosis = false;
            DiagnosisHeadline = "";
            DiagnosisText = "";
            return;
        }
        CauseFinding primary = dx.Primary;
        HasDiagnosis = true;
        DiagnosisHeadline = $"LIKELY CAUSE  {dx.Headline.ToUpperInvariant()}";
        DiagnosisText = primary.Evidence;
        DiagnosisColor = new SolidColorBrush(primary.Cause switch
        {
            ThermalCause.PowerConfig or ThermalCause.HighAmbient => ThermalPalette.Cool,
            ThermalCause.Paste or ThermalCause.Mount => ThermalPalette.Hot,
            _ => ThermalPalette.Accent,
        });
    }

    public void OnRemark(Remark remark)
    {
        LatestRemark = remark.Text;
        LatestRemarkTime = remark.TimestampUtc.ToLocalTime().ToString(TimeFormat.TimeOnly);
        LatestRemarkTag = remark.Severity switch
        {
            RemarkSeverity.Alert => "ALERT",
            RemarkSeverity.Warning => "WARNING",
            RemarkSeverity.Notice => "NOTICE",
            _ => "LOG",
        };
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

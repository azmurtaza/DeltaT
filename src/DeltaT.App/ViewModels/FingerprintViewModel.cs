using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.Core.Diagnostics;
using DeltaT.Core.Storage;

namespace DeltaT.App.ViewModels;

/// <summary>The month-over-month delta as an instrument readout: the signed change
/// ("+2.4°"), a direction key the view colors by ("hotter" | "cooler" | "steady"),
/// the verdict word ("RUNNING HOTTER"), and the reference caption
/// ("VS JUN 12 · WEATHER-CORRECTED"). Null when there is no earlier fingerprint of
/// the same component to compare against.</summary>
public sealed record FingerprintComparison(string DeltaText, string Direction, string Word, string Caption);

/// <summary>One component's result on the done screen: a titled block of stat cells,
/// its month-over-month comparison readout (when one exists), and the advisory verdict
/// sentence. A single test shows one section; a workup shows one per component that ran.</summary>
public sealed record FingerprintSection(
    string Title, IReadOnlyList<StatCell> Cells, string Verdict, FingerprintComparison? Comparison = null);

public partial class FingerprintViewModel : ObservableObject
{
    private readonly FingerprintTest _test;
    private readonly FingerprintSequence _sequence;
    private readonly TelemetryRepository _repo;
    private readonly Func<double?> _cpuLoad;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    /// <summary>Result blocks on the done screen, one per component that ran.</summary>
    public ObservableCollection<FingerprintSection> Sections { get; } = new();

    public bool HasGpu { get; }

    [ObservableProperty] private string _state = "intro"; // intro | running | done
    // Which operation the running/done screens are showing: the fingerprint measurement, or
    // the calibration workout. They share the running chrome (a held load + a ring gauge),
    // so this switches the few parts that differ (the protocol strip, the labels).
    [ObservableProperty] private string _mode = "fingerprint"; // fingerprint | workout
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string _targetLabel = "CPU";
    [ObservableProperty] private double _secondsLeft;
    [ObservableProperty] private bool _onBattery;
    // The multi-test badge ("TEST 1 OF 2 · CPU"), shown only while a workup runs.
    [ObservableProperty] private string _stepBadge = "";
    [ObservableProperty] private bool _inSequence;
    // Which leg of the protocol strip to light: fingerprint 0 settle/1 load/2 cooldown;
    // workout 0 medium/1 heavy.
    [ObservableProperty] private int _phaseIndex;

    // Running-screen labels, so the shared chrome reads correctly for each operation.
    [ObservableProperty] private string _runningOverline = "Fingerprint in progress";
    [ObservableProperty] private string _phaseHint = "";
    [ObservableProperty] private string _stopLabel = "STOP THE TEST";

    // The ring gauge is generalized: the fingerprint drives it with temperature (°C), the
    // workout with CPU load (%). Value/HasValue below; unit/max/sub let the same control read
    // either without the view knowing which operation is running.
    [ObservableProperty] private double _gaugeValue;
    [ObservableProperty] private bool _hasGaugeValue;
    [ObservableProperty] private string _gaugeUnit = "°C";
    [ObservableProperty] private double _gaugeMax = 100;
    [ObservableProperty] private string _gaugeSub = "CPU";

    // Multi-session workout progress: one workout is one independent loaded bout, and the
    // baseline locks on several spaced-out bouts (not one long burn), so the calibration strip
    // tracks how many DeltaT has banked this epoch against the target. Sourced from the real
    // loaded-session count so it reflects organic gaming bouts too, not just workouts.
    [ObservableProperty] private bool _showBoutProgress;
    [ObservableProperty] private string _boutProgressText = "";
    [ObservableProperty] private string _boutProgressHint = "";
    [ObservableProperty] private double _boutProgressValue; // 0..1 for the segment meter
    [ObservableProperty] private int _boutTarget;

    private readonly Func<(int Bouts, int Target)>? _boutProgress;

    public FingerprintViewModel(FingerprintTest test, FingerprintSequence sequence,
        TelemetryRepository repo, Func<double?> cpuLoad, bool onBattery, bool hasGpu,
        Func<(int Bouts, int Target)>? boutProgress = null)
    {
        _test = test;
        _sequence = sequence;
        _repo = repo;
        _cpuLoad = cpuLoad;
        _onBattery = onBattery;
        HasGpu = hasGpu;
        _boutProgress = boutProgress;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        RefreshBoutProgress();
    }

    /// <summary>Recompute the banked-bout tracker from the live loaded-session count. Called when
    /// the window opens and after a workout finishes, so the user sees the count tick up.</summary>
    public void RefreshBoutProgress()
    {
        if (_boutProgress is null)
            return;
        (int bouts, int target) = _boutProgress();
        if (target <= 0)
        {
            ShowBoutProgress = false;
            return;
        }
        BoutTarget = target;
        BoutProgressValue = Math.Clamp(bouts / (double)target, 0, 1);
        ShowBoutProgress = true;
        if (bouts >= target)
        {
            BoutProgressText = $"{bouts} independent bouts banked";
            BoutProgressHint = "Enough spaced bouts to lock the loaded buckets once the readings are consistent. Extra bouts keep sharpening it.";
        }
        else
        {
            BoutProgressText = $"{bouts} of about {target} independent bouts banked";
            BoutProgressHint = "The baseline locks on several bouts spread over time, not one long run. Come back and run another later (a game counts too).";
        }
    }

    [RelayCommand]
    private Task StartCpuAsync() => RunSequenceAsync(new[] { FingerprintTarget.Cpu });

    [RelayCommand]
    private Task StartGpuAsync() => RunSequenceAsync(new[] { FingerprintTarget.Gpu });

    /// <summary>The full workup: CPU, cool down, then GPU, back-to-back in one run.</summary>
    [RelayCommand]
    private Task StartWorkupAsync() => RunSequenceAsync(new[] { FingerprintTarget.Cpu, FingerprintTarget.Gpu });

    /// <summary>The calibration workout: hold Medium then Heavy CPU load so those buckets fill
    /// without waiting for them to happen by chance (Heavy especially is slow to reach in normal
    /// use). Unlike the fingerprint it records no result of its own; it just manufactures real
    /// loaded minutes through the normal monitoring pipeline, so the unchanged confidence gate
    /// locks sooner. It never touches the score or fakes a lock. Max is left to the fingerprint
    /// (a full-load reading needs the machine's real boost state), which is exactly why the two
    /// sit together here: the fingerprint fills the full-load bucket, the workout the ones below.</summary>
    [RelayCommand]
    private async Task StartWorkoutAsync()
    {
        Mode = "workout";
        State = "running";
        InSequence = false;
        StepBadge = "";
        RunningOverline = "Calibration workout in progress";
        StopLabel = "STOP WORKOUT";
        Phase = "Warming up";
        PhaseHint = "";
        GaugeUnit = "%";
        GaugeMax = 100;
        GaugeSub = "TARGET";
        HasGaugeValue = false;
        PhaseIndex = 0;
        _cts = new CancellationTokenSource();

        var workout = new CalibrationWorkout();
        var progress = new Progress<WorkoutProgress>(p =>
        {
            if (p.Phase == WorkoutPhase.Done) return;
            PhaseIndex = p.Phase == WorkoutPhase.Heavy ? 1 : 0;
            Phase = p.Phase == WorkoutPhase.Heavy ? "Holding a heavy load" : "Holding a medium load";
            GaugeSub = $"TARGET {p.TargetLoadPct}%";
            PhaseHint = $"{p.Remaining.Minutes}:{p.Remaining.Seconds:00} left in this workout";
            if (p.CurrentLoadPct is { } l)
            {
                GaugeValue = l;
                HasGaugeValue = true;
            }
        });

        try
        {
            await Task.Run(() => workout.RunAsync(u => new CpuBurner(u), _cpuLoad, progress, _cts.Token));
            RefreshBoutProgress();
            Sections.Clear();
            Sections.Add(new FingerprintSection("CALIBRATION WORKOUT", Array.Empty<StatCell>(),
                "Done. Those medium and heavy minutes are recorded. Run it again in a little while (spaced out, "
                + "so it counts as a fresh reading), and pair it with a fingerprint for the full-load number: "
                + "together they let the baseline lock without waiting for the loads to happen on their own."));
            State = "done";
        }
        catch (OperationCanceledException)
        {
            State = "intro";
        }
        catch (Exception ex)
        {
            Sections.Clear();
            Sections.Add(new FingerprintSection("CALIBRATION WORKOUT", Array.Empty<StatCell>(),
                $"The workout couldn't run: {ex.Message}"));
            State = "done";
        }
    }

    private async Task RunSequenceAsync(IReadOnlyList<FingerprintTarget> steps)
    {
        Mode = "fingerprint";
        State = "running";
        InSequence = steps.Count > 1;
        StepBadge = "";
        RunningOverline = "Fingerprint in progress";
        StopLabel = "STOP THE TEST";
        GaugeUnit = "°C";
        GaugeMax = 100;
        TargetLabel = steps[0] == FingerprintTarget.Gpu ? "GPU" : "CPU";
        GaugeSub = TargetLabel;
        HasGaugeValue = false;
        PhaseIndex = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<SequenceProgress>(p =>
        {
            Phase = p.Phase;
            PhaseIndex = (int)p.Stage;
            SecondsLeft = p.SecondsLeft;
            PhaseHint = $"{p.SecondsLeft:0} s remaining in this phase";
            TargetLabel = p.Target == FingerprintTarget.Gpu ? "GPU" : "CPU";
            GaugeSub = TargetLabel;
            StepBadge = p.StepCount > 1 ? $"TEST {p.StepIndex + 1} OF {p.StepCount} · {TargetLabel}" : "";
            if (p.TempC is { } t)
            {
                GaugeValue = t;
                HasGaugeValue = true;
            }
        });

        try
        {
            FingerprintSequenceResult result = await Task.Run(() => _sequence.RunAsync(steps, progress, _cts.Token));
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            State = "intro";
        }
        catch (Exception ex)
        {
            Sections.Clear();
            Sections.Add(new FingerprintSection("FINGERPRINT", Array.Empty<StatCell>(), $"Test failed: {ex.Message}"));
            State = "done";
        }
    }

    private void ShowResult(FingerprintSequenceResult sequence)
    {
        Sections.Clear();
        foreach (SequenceStep step in sequence.Steps)
        {
            if (step.Result is { } result)
                Sections.Add(StoreAndBuildSection(result));
            else
                Sections.Add(new FingerprintSection(
                    (step.Target == FingerprintTarget.Gpu ? "GPU" : "CPU") + " FINGERPRINT",
                    Array.Empty<StatCell>(),
                    $"This run didn't complete: {step.Error}"));
        }
        if (Sections.Count == 0) // every step was cancelled before finishing
            State = "intro";
        else
            State = "done";
    }

    /// <summary>Stores one finished fingerprint as an event (so it lands on the Trends
    /// markers and feeds future month-over-month comparisons) and builds its done-screen
    /// section, including the verdict against the previous fingerprint of the same
    /// component.</summary>
    private FingerprintSection StoreAndBuildSection(FingerprintResult result)
    {
        long now = result.AtUtc.ToUnixTimeSeconds();
        string label = result.Target == "Gpu" ? "GPU" : "CPU";

        // Fetch the previous fingerprint OF THE SAME TARGET before storing this one — a
        // GPU run only ever compares against earlier GPU runs. Legacy events carry kind
        // "Cpu", so old history keeps feeding CPU comparisons. Sequence steps are stored
        // in order, so an earlier step of this same workup counts as a valid "previous".
        // The load duration sets the temperature: a run held at full load for 90 s settles
        // cooler than one held for 150 s. So a fingerprint may only be compared against one
        // taken under the SAME timing protocol; otherwise a change to the test itself would
        // masquerade as the machine running cooler. Runs from an older protocol stay in the
        // history and on the graph, they just don't get a verdict against this one.
        FingerprintResult? previous = null;
        bool olderProtocolExists = false;
        foreach (StoredEvent e in _repo.GetEvents("fingerprint", 0, now - 1, 24))
        {
            if (e.Kind != result.Target || e.Data is not { } json)
                continue;
            FingerprintResult? candidate = null;
            try { candidate = JsonSerializer.Deserialize<FingerprintResult>(json); }
            catch { }
            if (candidate is null)
                continue;
            // Same protocol, and both runs must have actually settled. A run that hit the
            // ceiling still climbing measured a floor, not a plateau, so pairing it with a
            // settled run would compare two different quantities.
            if (candidate.Protocol == result.Protocol && candidate.Settled && result.Settled)
            {
                previous = candidate;
                break;
            }
            if (candidate.Protocol != result.Protocol)
                olderProtocolExists = true;
        }

        _repo.InsertEvent(now, "fingerprint", result.Target, null, 1,
            $"Fingerprint: {label} sustained {result.SustainedC:0.#}° (peak {result.PeakC:0.#}°), soak {result.SoakRatePerMin:0.#}°/min"
            + (result.SustainedDeltaC is { } d ? $", Δ+{d:0.#}° vs outside" : "")
            + (result.ThrottleSamples > 0 ? $", throttled {result.ThrottleSamples} samples" : ", no throttling"),
            JsonSerializer.Serialize(result));

        var cells = new List<StatCell>
        {
            new("SUSTAINED", $"{result.SustainedC:0.#}°"),
            new("PEAK", $"{result.PeakC:0.#}°"),
            new("SOAK RATE", $"{result.SoakRatePerMin:0.#}°/min"),
            new("Δ OUTSIDE", result.SustainedDeltaC is { } dd ? $"+{dd:0.#}°" : "-"),
            new("THROTTLING", result.ThrottleSamples > 0 ? $"{result.ThrottleSamples} samples" : "none"),
            // The load ran until the component stopped climbing, so how long that took is
            // itself a reading: a machine that takes longer to settle than it used to is
            // shedding heat more slowly.
            new("LOAD HELD", result.Settled ? $"{result.LoadSeconds:0}s" : $"{result.LoadSeconds:0}s (still rising)"),
        };
        if (result.Target == "Cpu" && result.GpuWasLoaded && result.GpuPeakC is { } gp)
            cells.Add(new StatCell("GPU PEAK", $"{gp:0.#}°"));

        string verdict;
        FingerprintComparison? comparison = null;
        if (previous is { SustainedDeltaC: { } prevDelta } && result.SustainedDeltaC is { } curDelta)
        {
            double diff = curDelta - prevDelta;
            string when = previous.AtUtc.ToLocalTime().ToString("MMM d").ToUpperInvariant();
            string direction = Math.Abs(diff) < 1.5 ? "steady" : diff > 0 ? "hotter" : "cooler";
            comparison = new FingerprintComparison(
                $"{diff:+0.#;-0.#;0}°",
                direction,
                direction switch { "steady" => "UNCHANGED", "hotter" => "RUNNING HOTTER", _ => "RUNNING COOLER" },
                $"VS {when} · WEATHER-CORRECTED");
            verdict = direction switch
            {
                "steady" => "Cooling is holding steady.",
                "hotter" => "Rerun monthly. A steady climb points to cooling wearing down.",
                _ => "Whatever you did, it worked.",
            };
        }
        else if (!result.OnAcPower)
        {
            verdict = "Recorded, but this run was on battery, so power limits softened the load. Prefer plugged-in runs for comparable numbers.";
        }
        else if (!result.Settled)
        {
            verdict = $"The {label} was still heating up when the test hit its time limit, so this is a floor, not a settled reading. "
                + "That usually means a large cooler that takes its time. It's recorded, but DeltaT won't compare it against settled runs.";
        }
        else if (olderProtocolExists)
        {
            // Honest about why there's no verdict: the earlier runs are still in the history,
            // they were just measured on a longer load and would read hotter for that reason
            // alone.
            verdict = $"The fingerprint test is now shorter, so this run isn't comparable to your older {label} ones. "
                + "This becomes the new reference. Rerun it monthly (plugged in, similar room) and DeltaT will chart the drift.";
        }
        else
        {
            verdict = $"First {label} fingerprint recorded. Rerun it monthly (plugged in, similar room) and DeltaT will chart the drift.";
        }

        return new FingerprintSection($"{label} FINGERPRINT", cells, verdict, comparison);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    public void CancelIfRunning() => _cts?.Cancel();
}

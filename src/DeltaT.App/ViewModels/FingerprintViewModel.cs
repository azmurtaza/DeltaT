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
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    /// <summary>Result blocks on the done screen, one per component that ran.</summary>
    public ObservableCollection<FingerprintSection> Sections { get; } = new();

    public bool HasGpu { get; }

    [ObservableProperty] private string _state = "intro"; // intro | running | done
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string _targetLabel = "CPU";
    [ObservableProperty] private double _secondsLeft;
    [ObservableProperty] private double _currentTemp;
    [ObservableProperty] private bool _hasCurrentTemp;
    [ObservableProperty] private bool _onBattery;
    // The multi-test badge ("TEST 1 OF 2 · CPU"), shown only while a workup runs.
    [ObservableProperty] private string _stepBadge = "";
    [ObservableProperty] private bool _inSequence;
    // Which leg of the protocol strip to light: 0 settle, 1 load, 2 cooldown.
    [ObservableProperty] private int _phaseIndex;

    public FingerprintViewModel(FingerprintTest test, FingerprintSequence sequence,
        TelemetryRepository repo, bool onBattery, bool hasGpu)
    {
        _test = test;
        _sequence = sequence;
        _repo = repo;
        _onBattery = onBattery;
        HasGpu = hasGpu;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    [RelayCommand]
    private Task StartCpuAsync() => RunSequenceAsync(new[] { FingerprintTarget.Cpu });

    [RelayCommand]
    private Task StartGpuAsync() => RunSequenceAsync(new[] { FingerprintTarget.Gpu });

    /// <summary>The full workup: CPU, cool down, then GPU, back-to-back in one run.</summary>
    [RelayCommand]
    private Task StartWorkupAsync() => RunSequenceAsync(new[] { FingerprintTarget.Cpu, FingerprintTarget.Gpu });

    private async Task RunSequenceAsync(IReadOnlyList<FingerprintTarget> steps)
    {
        State = "running";
        InSequence = steps.Count > 1;
        StepBadge = "";
        TargetLabel = steps[0] == FingerprintTarget.Gpu ? "GPU" : "CPU";
        HasCurrentTemp = false;
        PhaseIndex = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<SequenceProgress>(p =>
        {
            Phase = p.Phase;
            PhaseIndex = (int)p.Stage;
            SecondsLeft = p.SecondsLeft;
            TargetLabel = p.Target == FingerprintTarget.Gpu ? "GPU" : "CPU";
            StepBadge = p.StepCount > 1 ? $"TEST {p.StepIndex + 1} OF {p.StepCount} · {TargetLabel}" : "";
            if (p.TempC is { } t)
            {
                CurrentTemp = t;
                HasCurrentTemp = true;
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
        FingerprintResult? previous = null;
        StoredEvent? prevEvent = _repo.GetEvents("fingerprint", 0, now - 1, 24)
            .FirstOrDefault(e => e.Kind == result.Target);
        if (prevEvent?.Data is { } json)
        {
            try { previous = JsonSerializer.Deserialize<FingerprintResult>(json); }
            catch { }
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
                "steady" => "The paste is holding steady.",
                "hotter" => "Rerun monthly. A steady climb is the paste drying out.",
                _ => "Whatever you did, it worked.",
            };
        }
        else
        {
            verdict = result.OnAcPower
                ? $"First {label} fingerprint recorded. Rerun it monthly (plugged in, similar room) and DeltaT will chart the drift."
                : "Recorded, but this run was on battery, so power limits softened the load. Prefer plugged-in runs for comparable numbers.";
        }

        return new FingerprintSection($"{label} FINGERPRINT", cells, verdict, comparison);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    public void CancelIfRunning() => _cts?.Cancel();
}

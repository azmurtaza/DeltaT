using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.Core.Diagnostics;
using DeltaT.Core.Storage;

namespace DeltaT.App.ViewModels;

public partial class FingerprintViewModel : ObservableObject
{
    private readonly FingerprintTest _test;
    private readonly TelemetryRepository _repo;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    public ObservableCollection<StatCell> ResultCells { get; } = new();

    public bool HasGpu { get; }

    [ObservableProperty] private string _state = "intro"; // intro | running | done
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string _targetLabel = "CPU";
    [ObservableProperty] private double _secondsLeft;
    [ObservableProperty] private double _currentTemp;
    [ObservableProperty] private bool _hasCurrentTemp;
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private bool _onBattery;

    public FingerprintViewModel(FingerprintTest test, TelemetryRepository repo, bool onBattery, bool hasGpu)
    {
        _test = test;
        _repo = repo;
        _onBattery = onBattery;
        HasGpu = hasGpu;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    [RelayCommand]
    private Task StartCpuAsync() => StartAsync(FingerprintTarget.Cpu);

    [RelayCommand]
    private Task StartGpuAsync() => StartAsync(FingerprintTarget.Gpu);

    private async Task StartAsync(FingerprintTarget target)
    {
        State = "running";
        TargetLabel = target == FingerprintTarget.Gpu ? "GPU" : "CPU";
        HasCurrentTemp = false;
        _cts = new CancellationTokenSource();
        var progress = new Progress<FingerprintProgress>(p =>
        {
            Phase = p.Phase;
            SecondsLeft = p.SecondsLeft;
            if (p.TempC is { } t)
            {
                CurrentTemp = t;
                HasCurrentTemp = true;
            }
        });

        try
        {
            FingerprintResult result = await Task.Run(() => _test.RunAsync(target, progress, _cts.Token));
            await _dispatcher.BeginInvoke(() => ShowResult(result));
        }
        catch (OperationCanceledException)
        {
            State = "intro";
        }
        catch (Exception ex)
        {
            VerdictText = $"Test failed: {ex.Message}";
            State = "done";
        }
    }

    private void ShowResult(FingerprintResult result)
    {
        long now = result.AtUtc.ToUnixTimeSeconds();
        string label = result.Target == "Gpu" ? "GPU" : "CPU";

        // Fetch the previous fingerprint OF THE SAME TARGET before storing this one —
        // a GPU run only ever compares against earlier GPU runs. Legacy events carry
        // kind "Cpu", so old history keeps feeding CPU comparisons.
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

        ResultCells.Clear();
        ResultCells.Add(new StatCell("SUSTAINED", $"{result.SustainedC:0.#}°"));
        ResultCells.Add(new StatCell("PEAK", $"{result.PeakC:0.#}°"));
        ResultCells.Add(new StatCell("SOAK RATE", $"{result.SoakRatePerMin:0.#}°/min"));
        ResultCells.Add(new StatCell("Δ OUTSIDE", result.SustainedDeltaC is { } dd ? $"+{dd:0.#}°" : "-"));
        ResultCells.Add(new StatCell("THROTTLING", result.ThrottleSamples > 0 ? $"{result.ThrottleSamples} samples" : "none"));
        if (result.Target == "Cpu" && result.GpuWasLoaded && result.GpuPeakC is { } gp)
            ResultCells.Add(new StatCell("GPU PEAK", $"{gp:0.#}°"));

        if (previous is { SustainedDeltaC: { } prevDelta } && result.SustainedDeltaC is { } curDelta)
        {
            double diff = curDelta - prevDelta;
            string when = previous.AtUtc.ToLocalTime().ToString("MMM d");
            VerdictText = Math.Abs(diff) < 1.5
                ? $"Versus the {when} {label} fingerprint: unchanged ({diff:+0.#;-0.#}° weather-corrected). The paste is holding steady."
                : diff > 0
                    ? $"Versus the {when} {label} fingerprint: running {diff:0.#}° hotter, weather-corrected. Rerun monthly - a steady climb is the paste drying out."
                    : $"Versus the {when} {label} fingerprint: {-diff:0.#}° cooler, weather-corrected. Whatever you did - it worked.";
        }
        else
        {
            VerdictText = result.OnAcPower
                ? $"First {label} fingerprint recorded. Rerun it monthly (plugged in, similar room) and DeltaT will chart the drift."
                : "Recorded - but this run was on battery, so power limits softened the load. Prefer plugged-in runs for comparable numbers.";
        }

        State = "done";
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    public void CancelIfRunning() => _cts?.Cancel();
}

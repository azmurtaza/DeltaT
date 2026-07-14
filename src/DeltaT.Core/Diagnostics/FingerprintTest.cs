using System.Text.Json.Serialization;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Weather;

namespace DeltaT.Core.Diagnostics;

public enum FingerprintTarget { Cpu, Gpu }

/// <summary>Which leg of the test protocol is running — the machine-readable twin of
/// the prose <c>Phase</c>, so a UI can light the matching segment of a protocol strip
/// without parsing the phrase.</summary>
public enum FingerprintStage { Settle, Load, Cooldown }

public sealed record FingerprintProgress(
    string Phase, FingerprintStage Stage, double SecondsLeft, double? TempC, double? Load);

/// <summary>One fingerprint run's numbers. The primary Start/Peak/Sustained fields
/// describe whichever component was loaded (<see cref="Target"/>); their JSON names
/// keep the historical "Cpu*" spelling so every fingerprint stored before GPU runs
/// existed still deserializes for month-over-month comparison. The Gpu* fields are
/// the ride-along GPU readings of a CPU run (a game happening to run alongside).</summary>
public sealed record FingerprintResult(
    DateTimeOffset AtUtc,
    double? AmbientC,
    [property: JsonPropertyName("CpuStartC")] double StartC,
    [property: JsonPropertyName("CpuPeakC")] double PeakC,
    [property: JsonPropertyName("CpuSustainedC")] double SustainedC,
    [property: JsonPropertyName("CpuSustainedDeltaC")] double? SustainedDeltaC,
    double SoakRatePerMin,
    int ThrottleSamples,
    double? GpuPeakC,
    double? GpuSustainedDeltaC,
    bool GpuWasLoaded,
    bool OnAcPower,
    string Target = "Cpu");

/// <summary>The on-demand thermal fingerprint: ~25 s of calm, then ~2.5 min of full
/// load on the chosen component while we watch how the machine soaks and where it
/// settles. Repeatable, so month-over-month fingerprints are directly comparable —
/// the active counterpart to the passive baseline. CPU runs spin every core
/// (CpuBurner); GPU runs saturate the compute engine through OpenCL (GpuBurner).
/// During a CPU run the GPU isn't loaded by us, but if a game/benchmark happens to
/// be running its numbers ride along.</summary>
public sealed class FingerprintTest
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan Load = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private readonly MonitoringService _monitor;
    private readonly IAmbientProvider _ambient;

    public FingerprintTest(MonitoringService monitor, IAmbientProvider ambient)
    {
        _monitor = monitor;
        _ambient = ambient;
    }

    public async Task<FingerprintResult> RunAsync(FingerprintTarget target,
        IProgress<FingerprintProgress>? progress, CancellationToken ct)
    {
        ComponentKind kind = target == FingerprintTarget.Gpu ? ComponentKind.GpuDiscrete : ComponentKind.Cpu;
        string label = target == FingerprintTarget.Gpu ? "GPU" : "CPU";

        var samples = new List<(DateTimeOffset Ts, double Temp, bool Throttling)>();
        var gpuSamples = new List<(double Temp, double Load)>();
        bool onAc = true;

        // The active phase and when it ends, so a snapshot arriving mid-phase can report
        // an accurate countdown alongside its reading. Written on the run thread at each
        // phase change, read on the monitor thread in Collect — stable within a phase, so
        // a torn read at worst mislabels a single frame's countdown by a tick.
        string phase = "";
        FingerprintStage stage = FingerprintStage.Settle;
        DateTimeOffset phaseEnd = DateTimeOffset.UtcNow;

        void Collect(SensorSnapshot snap)
        {
            onAc = snap.OnAcPower;
            if (snap.Find(kind) is { TemperatureC: { } t } reading)
            {
                samples.Add((snap.TimestampUtc, t, reading.IsThrottling));
                // Push the reading the instant it lands — the same snapshot the main
                // window renders from — so the fingerprint gauge can't trail it by a tick.
                if (phase.Length > 0)
                    progress?.Report(new FingerprintProgress(
                        phase, stage, Math.Max(0, (phaseEnd - DateTimeOffset.UtcNow).TotalSeconds), t, null));
            }
            if (target == FingerprintTarget.Cpu
                && snap.Find(ComponentKind.GpuDiscrete) is { TemperatureC: { } gt, LoadPercent: { } gl })
                gpuSamples.Add((gt, gl));
        }

        _monitor.SnapshotCaptured += Collect;
        try
        {
            await TickPhase("Settling. Hands off the machine", FingerprintStage.Settle, Settle, progress, ct).ConfigureAwait(false);

            double start = TailAverage(samples, TimeSpan.FromSeconds(10)) ?? samples.LastOrDefault().Temp;
            int loadStartIndex = samples.Count;
            int gpuLoadStartIndex = gpuSamples.Count;
            DateTimeOffset loadStartTs = DateTimeOffset.UtcNow;

            using (IDisposable burner = CreateBurner(target))
            {
                await TickPhase($"Full {label} load, let it cook", FingerprintStage.Load, Load, progress, ct).ConfigureAwait(false);
            }

            var loadSamples = samples.Skip(loadStartIndex).ToList();
            if (loadSamples.Count < 10)
                throw new InvalidOperationException("Not enough sensor samples during the load phase.");

            double peak = loadSamples.Max(s => s.Temp);
            double sustained = TailAverage(samples, TimeSpan.FromSeconds(45)) ?? peak;
            int throttles = loadSamples.Count(s => s.Throttling);

            // Soak rate: rise over the first 60 s of load.
            var minuteIn = loadSamples.Where(s => s.Ts <= loadStartTs + TimeSpan.FromSeconds(60)).ToList();
            double soakRate = minuteIn.Count >= 2
                ? (minuteIn[^1].Temp - start) / Math.Max(10, (minuteIn[^1].Ts - loadStartTs).TotalSeconds) * 60
                : 0;

            double? ambient = _ambient.CurrentAmbientC;
            var gpuLoaded = gpuSamples.Skip(gpuLoadStartIndex).Where(g => g.Load >= 60).ToList();

            var result = new FingerprintResult(
                AtUtc: DateTimeOffset.UtcNow,
                AmbientC: ambient,
                StartC: Math.Round(start, 1),
                PeakC: Math.Round(peak, 1),
                SustainedC: Math.Round(sustained, 1),
                SustainedDeltaC: ambient is { } a ? Math.Round(sustained - a, 1) : null,
                SoakRatePerMin: Math.Round(soakRate, 1),
                ThrottleSamples: throttles,
                GpuPeakC: gpuLoaded.Count > 0 ? Math.Round(gpuLoaded.Max(g => g.Temp), 1) : null,
                GpuSustainedDeltaC: gpuLoaded.Count > 0 && ambient is { } a2
                    ? Math.Round(gpuLoaded.TakeLast(20).Average(g => g.Temp) - a2, 1) : null,
                GpuWasLoaded: gpuLoaded.Count > 0,
                OnAcPower: onAc,
                Target: target.ToString());

            // Cooldown ticks like any other phase — the countdown counts down and the
            // gauge keeps tracking the component as it falls — but a Stop here just ends
            // the wait and still returns the finished result: the numbers are already in hand.
            await TickPhase("Cooling down", FingerprintStage.Cooldown, Cooldown, progress, ct, cancelable: false).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _monitor.SnapshotCaptured -= Collect;
        }

        async Task TickPhase(string phaseName, FingerprintStage phaseStage, TimeSpan duration,
            IProgress<FingerprintProgress>? prog, CancellationToken token, bool cancelable = true)
        {
            phase = phaseName;
            stage = phaseStage;
            DateTimeOffset end = DateTimeOffset.UtcNow + duration;
            phaseEnd = end;
            while (DateTimeOffset.UtcNow < end)
            {
                if (token.IsCancellationRequested)
                {
                    if (cancelable) token.ThrowIfCancellationRequested();
                    return;
                }
                // Countdown tick every half second so the timer never stalls. The
                // temperature rides in live from Collect; null here means "unchanged",
                // so the gauge holds the last reading between snapshots.
                prog?.Report(new FingerprintProgress(phaseName, phaseStage, (end - DateTimeOffset.UtcNow).TotalSeconds, null, null));
                try { await Task.Delay(500, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (cancelable) throw; return; }
            }
        }

        double? TailAverage(List<(DateTimeOffset Ts, double Temp, bool Throttling)> list, TimeSpan tail)
        {
            if (list.Count == 0) return null;
            DateTimeOffset cut = list[^1].Ts - tail;
            var window = list.Where(s => s.Ts >= cut).ToList();
            return window.Count > 0 ? window.Average(s => s.Temp) : null;
        }
    }

    private IDisposable CreateBurner(FingerprintTarget target)
    {
        if (target == FingerprintTarget.Cpu)
            return new CpuBurner();
        // Aim the compute load at the same GPU the sensors watch (the discrete card
        // on a hybrid laptop, not the display iGPU).
        string? gpuName = _monitor.Latest?.Find(ComponentKind.GpuDiscrete)?.Name;
        return new GpuBurner(gpuName);
    }

    /// <summary>Spins every core with float math at BelowNormal priority — full
    /// thermal load, but the UI stays responsive. Dispose to stop instantly.</summary>
    private sealed class CpuBurner : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Thread> _threads = new();

        public CpuBurner()
        {
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                var thread = new Thread(Burn) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                thread.Start();
                _threads.Add(thread);
            }
        }

        private void Burn()
        {
            double x = 1.000173;
            long iterations = 0;
            while (!_cts.IsCancellationRequested)
            {
                x = Math.Sqrt(x * 1.7305 + 0.31) * 1.0001;
                if (x > 1e9) x = 1.000173;
                if (++iterations % 5_000_000 == 0)
                    Volatile.Write(ref _sink, x); // keep the JIT honest
            }
        }

        private static double _sink;

        public void Dispose()
        {
            _cts.Cancel();
            foreach (Thread t in _threads)
                t.Join(500);
            _cts.Dispose();
        }
    }
}

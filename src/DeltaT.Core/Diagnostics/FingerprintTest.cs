using DeltaT.Core.Monitoring;
using DeltaT.Core.Weather;

namespace DeltaT.Core.Diagnostics;

public sealed record FingerprintProgress(string Phase, double SecondsLeft, double? CpuTempC, double? CpuLoad);

public sealed record FingerprintResult(
    DateTimeOffset AtUtc,
    double? AmbientC,
    double CpuStartC,
    double CpuPeakC,
    double CpuSustainedC,
    double? CpuSustainedDeltaC,
    double SoakRatePerMin,
    int ThrottleSamples,
    double? GpuPeakC,
    double? GpuSustainedDeltaC,
    bool GpuWasLoaded,
    bool OnAcPower);

/// <summary>The on-demand thermal fingerprint: ~25 s of calm, then ~2.5 min of
/// full CPU load while we watch how the machine soaks and where it settles.
/// Repeatable, so month-over-month fingerprints are directly comparable — the
/// active counterpart to the passive baseline. The GPU isn't loaded by us
/// (that needs a real 3D workload); if a game/benchmark happens to be running,
/// its numbers ride along.</summary>
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

    public async Task<FingerprintResult> RunAsync(IProgress<FingerprintProgress>? progress, CancellationToken ct)
    {
        var cpuSamples = new List<(DateTimeOffset Ts, double Temp, bool Throttling)>();
        var gpuSamples = new List<(double Temp, double Load)>();
        bool onAc = true;

        void Collect(SensorSnapshot snap)
        {
            onAc = snap.OnAcPower;
            if (snap.Find(ComponentKind.Cpu) is { TemperatureC: { } ct_ } cpu)
                cpuSamples.Add((snap.TimestampUtc, ct_, cpu.IsThrottling));
            if (snap.Find(ComponentKind.GpuDiscrete) is { TemperatureC: { } gt, LoadPercent: { } gl })
                gpuSamples.Add((gt, gl));
        }

        _monitor.SnapshotCaptured += Collect;
        try
        {
            await TickPhase("Settling — hands off the machine", Settle, progress, ct).ConfigureAwait(false);

            double cpuStart = TailAverage(cpuSamples, TimeSpan.FromSeconds(10)) ?? cpuSamples.LastOrDefault().Temp;
            int loadStartIndex = cpuSamples.Count;
            DateTimeOffset loadStartTs = DateTimeOffset.UtcNow;

            using (var burner = new CpuBurner())
            {
                await TickPhase("Full CPU load — let it cook", Load, progress, ct).ConfigureAwait(false);
            }

            var loadSamples = cpuSamples.Skip(loadStartIndex).ToList();
            if (loadSamples.Count < 10)
                throw new InvalidOperationException("Not enough sensor samples during the load phase.");

            double peak = loadSamples.Max(s => s.Temp);
            double sustained = TailAverage(cpuSamples, TimeSpan.FromSeconds(45)) ?? peak;
            int throttles = loadSamples.Count(s => s.Throttling);

            // Soak rate: rise over the first 60 s of load.
            var minuteIn = loadSamples.Where(s => s.Ts <= loadStartTs + TimeSpan.FromSeconds(60)).ToList();
            double soakRate = minuteIn.Count >= 2
                ? (minuteIn[^1].Temp - cpuStart) / Math.Max(10, (minuteIn[^1].Ts - loadStartTs).TotalSeconds) * 60
                : 0;

            double? ambient = _ambient.CurrentAmbientC;
            var gpuLoaded = gpuSamples.Skip(Math.Min(gpuSamples.Count, loadStartIndex)).Where(g => g.Load >= 60).ToList();

            var result = new FingerprintResult(
                AtUtc: DateTimeOffset.UtcNow,
                AmbientC: ambient,
                CpuStartC: Math.Round(cpuStart, 1),
                CpuPeakC: Math.Round(peak, 1),
                CpuSustainedC: Math.Round(sustained, 1),
                CpuSustainedDeltaC: ambient is { } a ? Math.Round(sustained - a, 1) : null,
                SoakRatePerMin: Math.Round(soakRate, 1),
                ThrottleSamples: throttles,
                GpuPeakC: gpuLoaded.Count > 0 ? Math.Round(gpuLoaded.Max(g => g.Temp), 1) : null,
                GpuSustainedDeltaC: gpuLoaded.Count > 0 && ambient is { } a2
                    ? Math.Round(gpuLoaded.TakeLast(20).Average(g => g.Temp) - a2, 1) : null,
                GpuWasLoaded: gpuLoaded.Count > 0,
                OnAcPower: onAc);

            progress?.Report(new FingerprintProgress("Cooling down", Cooldown.TotalSeconds, null, null));
            try { await Task.Delay(Cooldown, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            return result;
        }
        finally
        {
            _monitor.SnapshotCaptured -= Collect;
        }

        async Task TickPhase(string phase, TimeSpan duration, IProgress<FingerprintProgress>? prog, CancellationToken token)
        {
            DateTimeOffset end = DateTimeOffset.UtcNow + duration;
            while (DateTimeOffset.UtcNow < end)
            {
                token.ThrowIfCancellationRequested();
                var last = cpuSamples.Count > 0 ? cpuSamples[^1] : default;
                prog?.Report(new FingerprintProgress(
                    phase,
                    (end - DateTimeOffset.UtcNow).TotalSeconds,
                    cpuSamples.Count > 0 ? last.Temp : null,
                    null));
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }

        double? TailAverage(List<(DateTimeOffset Ts, double Temp, bool Throttling)> samples, TimeSpan tail)
        {
            if (samples.Count == 0) return null;
            DateTimeOffset cut = samples[^1].Ts - tail;
            var window = samples.Where(s => s.Ts >= cut).ToList();
            return window.Count > 0 ? window.Average(s => s.Temp) : null;
        }
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

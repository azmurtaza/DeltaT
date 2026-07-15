using System.Diagnostics;

namespace DeltaT.Core.Diagnostics;

/// <summary>Spins every core with float math at BelowNormal priority. At full tilt
/// (the fingerprint's load engine) it produces maximum thermal load while the UI stays
/// responsive. Below full tilt it duty-cycles each worker (burn a slice, sleep the rest)
/// so the time-averaged CPU load lands at a target fraction, which the calibration workout
/// uses to fill the Medium/Heavy buckets, not just Max. Dispose stops the load instantly.
///
/// Duty-cycling gives a bursty load, not a smooth partial one, so the instantaneous temp
/// oscillates; DeltaT records the minute AVERAGE of load, power and temp, which is the
/// quantity scoring compares, so the average operating point is what matters. Whether that
/// average lands where the user's real workloads sit (the Phase 0 accuracy constraint) is
/// exactly what the <c>--workout</c> spike measures.</summary>
public sealed class CpuBurner : ILoadEngine
{
    // A full duty period. Long enough that Thread.Sleep granularity (~15 ms on Windows)
    // doesn't dominate the off-slice, short enough that a bout looks like steady load to a
    // 1 s sensor tick.
    private const double PeriodMs = 200;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = new();
    private volatile int _permille;   // duty target, 50..1000, read each period
    private static double _sink;

    /// <summary>The load-vs-duty curve is steeply nonlinear on real silicon (a little burn
    /// already pins the load counter), so a fixed duty can't be trusted to hit a target load.
    /// This is settable at runtime so a controller can steer the achieved load into a bucket:
    /// read the real load each tick, nudge this toward the target. 0.05..1.0; 1.0 is full tilt.</summary>
    public double Utilization
    {
        get => _permille / 1000.0;
        set => _permille = (int)(Math.Clamp(value, 0.05, 1.0) * 1000);
    }

    /// <param name="targetUtilization">Initial duty fraction, 0..1. 1.0 (default) is full
    /// tilt with no duty-cycling, identical to the original fingerprint burner.</param>
    public CpuBurner(double targetUtilization = 1.0)
    {
        Utilization = targetUtilization;
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
        var sw = new Stopwatch();
        while (!_cts.IsCancellationRequested)
        {
            int pm = _permille;
            double onMs = PeriodMs * (pm / 1000.0);
            sw.Restart();
            while (sw.Elapsed.TotalMilliseconds < onMs)
            {
                for (int k = 0; k < 4000; k++)
                {
                    x = Math.Sqrt(x * 1.7305 + 0.31) * 1.0001;
                    if (x > 1e9) x = 1.000173;
                }
                if (_cts.IsCancellationRequested) { Volatile.Write(ref _sink, x); return; }
            }
            Volatile.Write(ref _sink, x); // keep the JIT honest
            if (pm < 999) // full tilt burns the whole period with no idle slice
            {
                int offMs = (int)(PeriodMs - onMs);
                if (offMs > 0) Thread.Sleep(offMs);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (Thread t in _threads)
            t.Join(500);
        _cts.Dispose();
    }
}

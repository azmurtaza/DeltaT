using DeltaT.Core.Diagnostics;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>The workout's job is to hold the CPU in the Medium and Heavy buckets. The load
/// engine can't be trusted to a fixed duty (open-loop overshoots badly on real silicon), so
/// the controller must steer from the measured load. These prove it converges into the target
/// bucket against a realistic steep, saturating load curve, and that the pure step is sane.</summary>
public class CalibrationWorkoutTests
{
    // A fake engine whose "load" saturates with duty the way real hardware does: on the dev
    // machine 24% duty already produced 55% load and 57% produced 80%. load = 100·u/(u+0.2)
    // reproduces that shape (u=0.24 -> 55%, u=0.57 -> 74%), so a fixed duty can't hit a target
    // and only closed-loop control can.
    private sealed class FakeEngine : ILoadEngine
    {
        public double Utilization { get; set; }
        public bool Disposed { get; private set; }
        public double Load => 100.0 * Utilization / (Utilization + 0.2);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void SteerUtilization_MovesTowardTarget_AndClamps()
    {
        // Below target -> push duty up; above -> pull down.
        Assert.True(CalibrationWorkout.SteerUtilization(0.3, 80, 40) > 0.3);
        Assert.True(CalibrationWorkout.SteerUtilization(0.6, 55, 95) < 0.6);
        // Never leaves the safe burn range, and a missing reading holds.
        Assert.InRange(CalibrationWorkout.SteerUtilization(0.05, 55, 0), 0.05, 1.0);
        Assert.InRange(CalibrationWorkout.SteerUtilization(1.0, 80, 0), 0.05, 1.0);
        Assert.Equal(0.42, CalibrationWorkout.SteerUtilization(0.42, 80, null));
    }

    [Fact]
    public async Task Run_ConvergesIntoMediumThenHeavyBuckets()
    {
        FakeEngine? engine = null;
        var byPhase = new Dictionary<WorkoutPhase, List<double>>();

        // 90 s per phase, no real waiting (delay is a no-op), so the controller gets plenty of
        // ticks to settle. readCpuLoad reads the live fake engine, so control is genuinely closed.
        var workout = new CalibrationWorkout(TimeSpan.FromSeconds(90), (_, _) => Task.CompletedTask);
        var progress = new Progress<WorkoutProgress>(p =>
        {
            if (p.CurrentLoadPct is { } l)
            {
                if (!byPhase.TryGetValue(p.Phase, out var list)) byPhase[p.Phase] = list = new();
                list.Add(l);
            }
        });

        await workout.RunAsync(
            u => engine = new FakeEngine { Utilization = u },
            () => engine?.Load,
            progress, CancellationToken.None);

        // The tail of each phase (once settled) must sit in the right bucket.
        double mediumSettled = Tail(byPhase[WorkoutPhase.Medium]);
        double heavySettled = Tail(byPhase[WorkoutPhase.Heavy]);
        Assert.InRange(mediumSettled, 40, 70);   // Medium bucket
        Assert.InRange(heavySettled, 70, 90);    // Heavy bucket
        Assert.True(engine!.Disposed);           // load stops when the workout ends

        static double Tail(List<double> xs) => xs.Skip(Math.Max(0, xs.Count - 10)).Average();
    }

    [Fact]
    public async Task Run_StopsAndDisposesTheEngine_OnCancel()
    {
        FakeEngine? engine = null;
        using var cts = new CancellationTokenSource();
        var workout = new CalibrationWorkout(TimeSpan.FromSeconds(30), (_, _) =>
        {
            cts.Cancel(); // trip cancellation on the first delay
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workout.RunAsync(
            u => engine = new FakeEngine { Utilization = u },
            () => engine?.Load, null, cts.Token));

        Assert.True(engine!.Disposed);
    }
}

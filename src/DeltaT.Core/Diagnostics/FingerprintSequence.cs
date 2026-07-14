using DeltaT.Core.Monitoring;

namespace DeltaT.Core.Diagnostics;

/// <summary>Progress across a multi-test run: which step, its stage, and the live
/// reading. Wraps the single-test <see cref="FingerprintProgress"/> so the window shows
/// "Test 1 of 2, CPU" over the very same ring gauge and countdown, plus a distinct
/// "cooling between tests" stage where the machine settles back toward idle.</summary>
public sealed record SequenceProgress(
    int StepIndex, int StepCount, FingerprintTarget Target,
    string Phase, FingerprintStage Stage, double SecondsLeft, double? TempC, bool BetweenTests);

/// <summary>One completed (or attempted) step of a sequence. <see cref="Result"/> is
/// null only when the step failed, in which case <see cref="Error"/> explains why.</summary>
public sealed record SequenceStep(FingerprintTarget Target, FingerprintResult? Result, string? Error);

public sealed record FingerprintSequenceResult(DateTimeOffset AtUtc, IReadOnlyList<SequenceStep> Steps);

/// <summary>Runs several fingerprints back-to-back (a CPU + GPU workup, or a repeated
/// run for repeatability) as one guided sequence, letting the component about to be
/// tested cool back toward idle between steps so each measures like-for-like. Every step
/// is an ordinary <see cref="FingerprintTest"/> run, so the stored events and
/// month-over-month comparisons are identical to a single test; the sequence only chains
/// them and reports one combined progress and result.
///
/// Cancellation keeps whatever finished: cancel during the GPU step and the completed
/// CPU fingerprint still comes back to be stored and shown. A step that throws (no GPU
/// compute device, say) is recorded as a failed step and the sequence carries on.</summary>
public sealed class FingerprintSequence
{
    /// <summary>Longest we wait for the next component to cool before its test. Capped so
    /// a machine that idles warm still proceeds; the settle phase of each run does the
    /// rest.</summary>
    public static readonly TimeSpan MaxCooldownBetween = TimeSpan.FromSeconds(90);

    /// <summary>Cooled "enough" once within this many °C of the idle reading taken before
    /// the sequence began, close enough that the next soak measures like-for-like.</summary>
    private const double CooledMarginC = 6.0;

    private readonly FingerprintTest _test;
    private readonly MonitoringService _monitor;

    public FingerprintSequence(FingerprintTest test, MonitoringService monitor)
    {
        _test = test;
        _monitor = monitor;
    }

    public async Task<FingerprintSequenceResult> RunAsync(
        IReadOnlyList<FingerprintTarget> steps, IProgress<SequenceProgress>? progress, CancellationToken ct)
    {
        // The machine is idle when the user kicks the sequence off; snapshot each
        // component's idle temperature now so the between-test cooldowns have a real
        // target to wait for rather than a blind timer.
        var idle = new Dictionary<ComponentKind, double>();
        foreach (FingerprintTarget t in steps.Distinct())
        {
            ComponentKind k = KindOf(t);
            if (!idle.ContainsKey(k) && _monitor.Latest?.Find(k) is { TemperatureC: { } tc })
                idle[k] = tc;
        }

        var results = new List<SequenceStep>();
        for (int i = 0; i < steps.Count; i++)
        {
            FingerprintTarget target = steps[i];
            ComponentKind kind = KindOf(target);
            try
            {
                if (i > 0)
                    await CoolBetweenAsync(kind, target, i, steps.Count,
                        idle.TryGetValue(kind, out double id) ? id : null, progress, ct).ConfigureAwait(false);

                int step = i;
                var inner = new Progress<FingerprintProgress>(p => progress?.Report(new SequenceProgress(
                    step, steps.Count, target, p.Phase, p.Stage, p.SecondsLeft, p.TempC, BetweenTests: false)));
                FingerprintResult result = await _test.RunAsync(target, inner, ct).ConfigureAwait(false);
                results.Add(new SequenceStep(target, result, null));
            }
            catch (OperationCanceledException)
            {
                // Keep any finished steps; only a cancel with nothing done reads as a
                // plain "never mind" back to the intro screen.
                if (results.Count > 0)
                    return new FingerprintSequenceResult(DateTimeOffset.UtcNow, results);
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new SequenceStep(target, null, ex.Message));
            }
        }
        return new FingerprintSequenceResult(DateTimeOffset.UtcNow, results);
    }

    private async Task CoolBetweenAsync(ComponentKind kind, FingerprintTarget target,
        int stepIndex, int stepCount, double? idleTemp, IProgress<SequenceProgress>? progress, CancellationToken ct)
    {
        DateTimeOffset end = DateTimeOffset.UtcNow + MaxCooldownBetween;
        double coolTo = idleTemp is { } id ? id + CooledMarginC : 50.0;
        while (DateTimeOffset.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            double? t = _monitor.Latest?.Find(kind)?.TemperatureC;
            double left = Math.Max(0, (end - DateTimeOffset.UtcNow).TotalSeconds);
            progress?.Report(new SequenceProgress(stepIndex, stepCount, target,
                "Cooling between tests", FingerprintStage.Cooldown, left, t, BetweenTests: true));
            if (t is { } tc && tc <= coolTo)
                return; // already back to idle; no point burning the rest of the timer
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static ComponentKind KindOf(FingerprintTarget target) =>
        target == FingerprintTarget.Gpu ? ComponentKind.GpuDiscrete : ComponentKind.Cpu;
}

using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using Xunit;
using Xunit.Abstractions;

namespace DeltaT.Core.Tests;

/// <summary>Reproduction of a real reading off the dev laptop (2026-07-31): a CPU whose
/// paste is fine reads PASTE 0/100 and CPU 55. Rows are copied verbatim out of that
/// machine's deltat.db (epoch 11 baseline, last 72 h of agg_minute, AC, weather mode).</summary>
public class SharedCoolingRepro
{
    private readonly ITestOutputHelper _out;
    public SharedCoolingRepro(ITestOutputHelper output) => _out = output;

    private static BaselineBucket B(LoadBucket b, int band, double d, double fan, double t, double pw, int m) =>
        new(b, band, d, d + 3, fan, m, TempAvg: t, GapAvg: null, PowerAvg: pw);

    private static RecentBucketObs R(LoadBucket b, int band, int mins, double d, double fan, double pw, double t) =>
        new(b, band, mins, d, t, t + 8, fan, 0, GapAvg: null, PowerAvg: pw);

    private static List<BaselineBucket> Baseline() => new()
    {
        B(LoadBucket.Idle,   1, 25.89, 3323, 50.3,  8.26,  8),
        B(LoadBucket.Light,  1, 25.69, 3300, 50.1, 10.72,  8),
        B(LoadBucket.Medium, 1, 26.55, 3318, 51.0, 11.79,  8),
        B(LoadBucket.Heavy,  1, 29.17, 3470, 53.6, 13.86,  8),
        B(LoadBucket.Idle,   2, 21.44, 3150, 48.8,  7.66, 79),
        B(LoadBucket.Light,  2, 22.97, 3257, 50.3,  9.68, 76),
        B(LoadBucket.Medium, 2, 27.26, 3434, 54.0, 13.53, 10),
        B(LoadBucket.Heavy,  2, 34.22, 4151, 60.8, 21.24,  8),
        B(LoadBucket.Max,    2, 34.82, 3972, 61.4, 26.22,  8),
    };

    private static List<RecentBucketObs> Recent() => new()
    {
        R(LoadBucket.Idle,   1,  14, 25.56, 3320,  8.02, 50.0),
        R(LoadBucket.Idle,   2, 517, 23.55, 3272,  8.41, 50.7),
        R(LoadBucket.Light,  1,  59, 27.31, 3324, 10.50, 51.7),
        R(LoadBucket.Light,  2, 784, 24.47, 3338, 10.40, 52.0),
        R(LoadBucket.Medium, 1,  46, 28.01, 3332, 12.09, 52.4),
        R(LoadBucket.Medium, 2, 356, 29.79, 3753, 13.66, 57.2),
        R(LoadBucket.Heavy,  1,  11, 29.18, 3456, 13.79, 53.6),
        R(LoadBucket.Heavy,  2,  97, 35.80, 4283, 16.18, 62.9),
        R(LoadBucket.Max,    1,   2, 30.10, 3509, 12.55, 54.5),
        R(LoadBucket.Max,    2,  18, 29.30, 3598, 13.44, 56.7),
    };

    private static ScoreInput Input(List<RecentBucketObs> recent, List<BaselineBucket> baseline) =>
        new(ComponentKind.Cpu, "13th Gen Intel Core i5-13420H", recent, baseline,
            RecentWindowHours: 72, ThrottleEvents: 0,
            SoakRateRecent: null, SoakRateBaseline: null,
            CooldownRateRecent: null, CooldownRateBaseline: null,
            LimitC: 100, Profile: null, BaselineReady: true, CalibrationProgress: 1.0);

    private void Dump(string label, ScoreInput input)
    {
        ComponentScore s = ScoringEngine.Score(input, c => $"{c:0.#}°C");
        string paste = s.Aspects.FirstOrDefault(a => a.Aspect == HealthAspect.Paste)?.Score?.ToString() ?? "--";
        _out.WriteLine($"{label,-34} score {s.Value,3} {s.Verdict,-8} excess {s.ExcessC,6:+0.0;-0.0}°  " +
                       $"raw {s.Basis?.MeasuredRiseC,4:0.0}° vs {s.Basis?.BaselineRiseC,4:0.0}°  " +
                       $"pwr {s.Basis?.PowerCorrectionC,5:+0.0;-0.0}° fan {s.Basis?.FanCorrectionC,5:+0.0;-0.0}°  PASTE {paste}");
    }

    /// <summary>Per-cell replica of ComputeExcess's corrections, so the contribution of each
    /// (bucket, band) cell is visible. Mirrors ScoringEngine exactly; constants come from it.</summary>
    private static double Strength(double ratio) =>
        Math.Clamp((Math.Abs(ratio - 1) - ScoringEngine.PowerRatioDeadbandStart)
                   / (ScoringEngine.PowerRatioDeadband - ScoringEngine.PowerRatioDeadbandStart), 0, 1);

    [Fact]
    public void PerCell_ShowsWhereTheExcessComesFrom()
    {
        List<BaselineBucket> baseline = Baseline();
        _out.WriteLine("cell           mins  rise   base   rawΔ   W now/base  ratio  pwrCorr  fanCorr  EXCESS  weight");
        foreach (RecentBucketObs r in Recent())
        {
            if (r.Minutes < ScoringEngine.MinMinutes(r.Bucket)) { _out.WriteLine($"{r.Bucket,-7} band{r.Band}  {r.Minutes,4}  (below MinMinutes, dropped)"); continue; }
            BaselineBucket? bb = baseline.FirstOrDefault(b => b.Bucket == r.Bucket && b.Band == r.Band);
            if (bb is null) { _out.WriteLine($"{r.Bucket,-7} band{r.Band}  {r.Minutes,4}  (no same-band baseline)"); continue; }

            double delta = r.DeltaAvg!.Value, rawRatio = bb.PowerAvg!.Value / r.PowerAvg!.Value;
            bool excluded = rawRatio < ScoringEngine.PowerRatioClampLo || rawRatio > ScoringEngine.PowerRatioClampHi;
            double pratio = Math.Clamp(rawRatio, ScoringEngine.PowerRatioClampLo, ScoringEngine.PowerRatioClampHi);
            double pc = excluded ? 0 : Math.Clamp((delta * pratio - delta) * Strength(pratio), -ScoringEngine.MaxPowerCorrectionC, ScoringEngine.MaxPowerCorrectionC);
            double fanBase = delta + pc, fc = 0;
            double fanRatio = r.FanAvg!.Value / bb.FanAvg!.Value / ScoringEngine.PlausibleFanFactor(r.PowerAvg.Value / bb.PowerAvg.Value);
            if (!excluded && Math.Abs(fanRatio - 1) >= ScoringEngine.FanRatioDeadband)
                fc = Math.Clamp(fanBase * Math.Pow(fanRatio, ScoringEngine.FanNormalizationExponent) - fanBase,
                                -ScoringEngine.MaxFanCorrectionC, ScoringEngine.MaxFanCorrectionC);
            double w = ScoringEngine.Weight(r.Bucket) * Math.Min(1.0, r.Minutes / 60.0 + 0.5);

            _out.WriteLine($"{r.Bucket,-7} band{r.Band}  {r.Minutes,4}  {delta,5:0.0}  {bb.DeltaAvg,5:0.0}  {delta - bb.DeltaAvg,5:+0.0;-0.0}  " +
                           $"{r.PowerAvg,5:0.0}/{bb.PowerAvg,-5:0.0} {rawRatio,5:0.00}  {pc,7:+0.0;-0.0}  {fc,7:+0.0;-0.0}  " +
                           $"{(excluded ? "EXCLUDED" : $"{delta + pc + fc - bb.DeltaAvg:+0.0;-0.0}"),7}  {w,5:0.00}");
        }
    }

    /// <summary>The regression this whole change exists for. These are the machine's real rows:
    /// its measured rise averages 30.0 C against a baseline of 30.0 C, i.e. it is running exactly
    /// where it always has, and the old engine still scored it 57 with the PASTE cell at 0 because
    /// the multiplicative power correction manufactured +10.4 C of excess out of a watt ratio.</summary>
    [Fact]
    public void TheMachineIsRunningAtItsBaseline_AndMustReadThatWay()
    {
        ComponentScore s = ScoringEngine.Score(Input(Recent(), Baseline()), c => $"{c:0.#}°C");

        // The premise: nothing about the raw measurement is off.
        Assert.NotNull(s.Basis);
        Assert.True(Math.Abs(s.Basis!.MeasuredRiseC - s.Basis.BaselineRiseC) < 1.0,
            $"premise: measured {s.Basis.MeasuredRiseC:0.0} vs baseline {s.Basis.BaselineRiseC:0.0}");

        // The old engine reported +10.4 C here and lost 43 points to delta-excess.
        Assert.True(s.ExcessC < 5.0, $"fabricated excess {s.ExcessC:+0.0} (was +10.4)");
        Assert.True(s.Value >= 80, $"score {s.Value} (was 57)");

        int paste = s.Aspects.Single(a => a.Aspect == HealthAspect.Paste).Score ?? 100;
        Assert.True(paste >= 15, $"PASTE cell {paste} (was 0)");
    }

    [Fact]
    public void Variants_IsolateTheCause()
    {
        Dump("as measured (the screenshot)", Input(Recent(), Baseline()));

        // Same rows with the power sensor blanked: what DeltaT would have said before power
        // normalization existed, i.e. judging the raw weather-corrected rise.
        List<RecentBucketObs> noPwR = Recent().Select(r => r with { PowerAvg = null }).ToList();
        List<BaselineBucket> noPwB = Baseline().Select(b => b with { PowerAvg = null }).ToList();
        Dump("power normalization off", Input(noPwR, noPwB));

        // Power normalization on, fan normalization off.
        List<RecentBucketObs> noFanR = Recent().Select(r => r with { FanAvg = null }).ToList();
        List<BaselineBucket> noFanB = Baseline().Select(b => b with { FanAvg = null }).ToList();
        Dump("fan normalization off", Input(noFanR, noFanB));

        // Both off: the pure like-for-like rise comparison.
        Dump("both off (raw rise only)", Input(
            Recent().Select(r => r with { PowerAvg = null, FanAvg = null }).ToList(),
            Baseline().Select(b => b with { PowerAvg = null, FanAvg = null }).ToList()));

        // The full-load cell alone is the one whose watts nearly halved (13.4 vs 26.2 W,
        // ratio 1.95 against a 2.0 exclusion clamp). Drop it and see what remains.
        Dump("without the Max/band2 cell", Input(
            Recent().Where(r => !(r.Bucket == LoadBucket.Max && r.Band == 2)).ToList(), Baseline()));

        // How close the clamp came to catching it: sweep the exclusion threshold.
        foreach (double clamp in new[] { 2.0, 1.9, 1.8, 1.6, 1.5, 1.4, 1.3 })
        {
            List<RecentBucketObs> kept = Recent()
                .Where(r => Baseline().FirstOrDefault(b => b.Bucket == r.Bucket && b.Band == r.Band) is not { } bb
                            || bb.PowerAvg!.Value / r.PowerAvg!.Value <= clamp).ToList();
            Dump($"if the exclusion clamp were {clamp:0.0}", Input(kept, Baseline()));
        }
    }
}

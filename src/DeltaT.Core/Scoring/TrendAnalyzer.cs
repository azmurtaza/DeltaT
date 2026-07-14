using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

/// <summary>One week's weather-corrected loaded excess over the frozen baseline (°C),
/// with the total loaded minutes behind it (its weight).</summary>
public sealed record WeeklyExcess(long WeekStartTs, double ExcessC, double WeightMinutes);

/// <summary>A detected sustained level shift: the weeks before <see cref="AtWeekStartTs"/>
/// sat around <see cref="BeforeC"/>, the weeks from it on around <see cref="AfterC"/>.</summary>
public sealed record StepChange(long AtWeekStartTs, double BeforeC, double AfterC)
{
    public double JumpC => AfterC - BeforeC;
}

/// <summary>The verdict of a long-term drift analysis.</summary>
public sealed record TrendResult(
    bool HasTrend,             // a statistically meaningful sustained slope (up or down)
    double SlopePerMonthC,     // °C per 30 days; positive = getting hotter over time
    int Weeks,                 // weeks of data behind the fit
    double CurrentExcessC,     // fitted excess at the most recent week
    double? MonthsToConcern,   // months until the fitted excess reaches ConcernExcessC, when rising
    StepChange? Step)          // a discrete level shift, if one dominates the noise
{
    public static readonly TrendResult None = new(false, 0, 0, 0, null, null);
}

/// <summary>Long-horizon paste-drift detection, kept strictly pure so it is unit-testable
/// and never touches clocks or I/O (same discipline as <see cref="ScoringEngine"/>).
///
/// The per-load-bucket score answers "is the machine hotter than baseline right now"; this
/// answers the slower question the score can't: "is the machine <i>trending</i> hotter, week
/// over week, and if so since when". Two independent readings come out of it:
///
/// 1. A weighted least-squares <b>slope</b> over the weekly weather-corrected excess. A slope
///    that is both large enough to matter (°C/month) and large relative to its own standard
///    error is real drift, not noise — and its rate projects a rough "months until it reaches
///    a level worth acting on".
/// 2. A <b>step change</b>: the single week split that best separates a low plateau from a
///    higher one, reported only when the jump dominates the within-plateau scatter. That is
///    the fingerprint of a discrete event (a knocked cooler, a fan that started failing, a
///    case moved into the sun) rather than gradual paste aging — a "something changed around
///    this date" flag, which is exactly the prompt-to-investigate a drift number can't give.
///
/// Everything is computed on the SAME weather band as the baseline (built upstream), so a
/// seasonal swing never masquerades as drift.</summary>
public static class TrendAnalyzer
{
    /// <summary>Weeks of weather-corrected data required before a trend is even considered.</summary>
    public const int MinWeeks = 4;

    /// <summary>A slope must reach this magnitude (°C over 30 days) to be worth reporting —
    /// below it, even a "real" slope is too slow to act on.</summary>
    public const double MinSlopePerMonthC = 0.5;

    /// <summary>...and it must exceed its own standard error by this factor (a t-like gate),
    /// so a slope drawn through scattered weeks isn't mistaken for a trend.</summary>
    public const double MinSlopeTStat = 2.0;

    /// <summary>Excess level (°C over baseline) the projection counts down to — roughly where
    /// a paste-health score would slip out of "Good".</summary>
    public const double ConcernExcessC = 5.0;

    /// <summary>Smallest level shift (°C) that counts as a step change.</summary>
    public const double MinStepC = 3.0;

    private const double WeeksPerMonth = 30.0 / 7.0;

    /// <summary>Turn per-(week, bucket, band) loaded cells into one weather-corrected excess
    /// per week: each cell is compared only against the baseline cell of the SAME load bucket
    /// and ambient band (so summer heat is judged against summer baselines, never winter), and
    /// the surviving cells are averaged weighted by load-bucket importance and minutes. Weeks
    /// with no band-matched baseline cell drop out rather than guess.</summary>
    public static IReadOnlyList<WeeklyExcess> BuildWeekly(
        IReadOnlyList<WeeklyLoadedCell> cells, IReadOnlyList<BaselineRow> baseline)
    {
        var baseByCell = new Dictionary<(LoadBucket, int), double>();
        foreach (BaselineRow b in baseline)
            baseByCell[(b.Bucket, b.Band)] = b.DeltaAvg; // one row per (epoch,kind,name,band,bucket)

        var byWeek = new Dictionary<long, (double WeightedExcess, double Weight, double Minutes)>();
        foreach (WeeklyLoadedCell c in cells)
        {
            if (!baseByCell.TryGetValue((c.Bucket, c.Band), out double baseDelta))
                continue; // no like-for-like baseline for this weather — skip, don't guess
            double w = ScoringEngine.Weight(c.Bucket) * c.Minutes;
            if (w <= 0)
                continue;
            (double we, double wt, double mins) = byWeek.TryGetValue(c.WeekStartTs, out var cur) ? cur : (0, 0, 0);
            byWeek[c.WeekStartTs] = (we + (c.DeltaAvg - baseDelta) * w, wt + w, mins + c.Minutes);
        }

        return byWeek
            .Where(kv => kv.Value.Weight > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new WeeklyExcess(kv.Key, kv.Value.WeightedExcess / kv.Value.Weight, kv.Value.Minutes))
            .ToList();
    }

    /// <summary>Fit a slope and look for a step over the weekly excess series.</summary>
    public static TrendResult Analyze(IReadOnlyList<WeeklyExcess> weeks)
    {
        if (weeks.Count < MinWeeks)
            return TrendResult.None;

        // Order and index weeks by their real spacing (a skipped week is a gap, not a
        // step of one), so the slope is genuinely per-unit-time.
        var ordered = weeks.OrderBy(w => w.WeekStartTs).ToList();
        long t0 = ordered[0].WeekStartTs;
        var xs = ordered.Select(w => (w.WeekStartTs - t0) / 604800.0).ToList(); // weeks since first
        var ys = ordered.Select(w => w.ExcessC).ToList();
        // Cap weight so one very long week can't dominate the fit; minutes/(min per useful hour).
        var ws = ordered.Select(w => Math.Min(3.0, Math.Max(0.2, w.WeightMinutes / 120.0))).ToList();

        (double slopePerWeek, double intercept, double slopeSe) = WeightedLinearFit(xs, ys, ws);
        double slopePerMonth = slopePerWeek * WeeksPerMonth;
        // A degenerate fit (no x-spread) is returned as a huge SE and can't be judged; a
        // near-zero SE is the opposite — an essentially noise-free straight line, which is
        // the STRONGEST evidence of a trend, so it reads as fully significant, not zero.
        double tStat = slopeSe >= double.MaxValue / 2 ? 0
            : slopeSe <= 1e-6 ? double.PositiveInfinity
            : Math.Abs(slopePerWeek) / slopeSe;

        bool hasTrend = Math.Abs(slopePerMonth) >= MinSlopePerMonthC && tStat >= MinSlopeTStat;

        double currentExcess = intercept + slopePerWeek * xs[^1];
        double? monthsToConcern = null;
        if (hasTrend && slopePerMonth > 0 && currentExcess < ConcernExcessC)
            monthsToConcern = (ConcernExcessC - currentExcess) / slopePerMonth;

        StepChange? step = DetectStep(ordered);

        return new TrendResult(hasTrend, slopePerMonth, ordered.Count, currentExcess, monthsToConcern, step);
    }

    /// <summary>Weighted least-squares fit y = a + b·x. Returns (slope, intercept, slopeSE).
    /// slopeSE is the standard error of the slope from the weighted residuals.</summary>
    private static (double Slope, double Intercept, double SlopeSe) WeightedLinearFit(
        IReadOnlyList<double> xs, IReadOnlyList<double> ys, IReadOnlyList<double> ws)
    {
        int n = xs.Count;
        double sw = 0, swx = 0, swy = 0, swxx = 0, swxy = 0;
        for (int i = 0; i < n; i++)
        {
            double w = ws[i];
            sw += w; swx += w * xs[i]; swy += w * ys[i];
            swxx += w * xs[i] * xs[i]; swxy += w * xs[i] * ys[i];
        }
        double denom = sw * swxx - swx * swx;
        if (Math.Abs(denom) < 1e-9)
            return (0, swy / sw, double.MaxValue);

        double slope = (sw * swxy - swx * swy) / denom;
        double intercept = (swy - slope * swx) / sw;

        // Weighted residual variance → standard error of the slope.
        double sse = 0;
        for (int i = 0; i < n; i++)
        {
            double resid = ys[i] - (intercept + slope * xs[i]);
            sse += ws[i] * resid * resid;
        }
        double xMean = swx / sw;
        double sxx = 0;
        for (int i = 0; i < n; i++)
            sxx += ws[i] * (xs[i] - xMean) * (xs[i] - xMean);

        double slopeSe = n > 2 && sxx > 1e-9 ? Math.Sqrt(sse / (n - 2) / sxx) : double.MaxValue;
        return (slope, intercept, slopeSe);
    }

    /// <summary>Find the split point that best separates a low plateau from a higher one.
    /// Reported only when the jump both clears <see cref="MinStepC"/> and is large relative
    /// to the scatter within each side — i.e. a genuine level shift, not a slow ramp or noise.</summary>
    private static StepChange? DetectStep(IReadOnlyList<WeeklyExcess> ordered)
    {
        int n = ordered.Count;
        if (n < MinWeeks)
            return null;

        StepChange? best = null;
        double bestScore = 0;
        // Keep at least two weeks on each side so a plateau is a plateau, not one reading.
        for (int k = 2; k <= n - 2; k++)
        {
            double beforeMean = ordered.Take(k).Average(w => w.ExcessC);
            double afterMean = ordered.Skip(k).Average(w => w.ExcessC);
            double jump = afterMean - beforeMean;
            if (Math.Abs(jump) < MinStepC)
                continue;

            double withinSd = Math.Sqrt(
                (SumSq(ordered.Take(k).Select(w => w.ExcessC), beforeMean)
                 + SumSq(ordered.Skip(k).Select(w => w.ExcessC), afterMean)) / Math.Max(1, n - 2));
            // Signal-to-noise of the split: the jump measured in within-plateau sigmas.
            double snr = Math.Abs(jump) / Math.Max(0.5, withinSd);
            if (snr >= 2.0 && snr > bestScore)
            {
                bestScore = snr;
                best = new StepChange(ordered[k].WeekStartTs, beforeMean, afterMean);
            }
        }
        return best;
    }

    private static double SumSq(IEnumerable<double> values, double mean) =>
        values.Sum(v => (v - mean) * (v - mean));
}

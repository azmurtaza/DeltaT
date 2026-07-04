using Kelvin.Core.Monitoring;
using Kelvin.Core.Storage;

namespace Kelvin.Core.Scoring;

/// <summary>Builds the "what healthy looks like" reference from the learning
/// window. The window is the first 7 days after an epoch starts (first run or a
/// repaste), extended automatically if the machine hasn't seen enough load yet.</summary>
public static class BaselineBuilder
{
    public static readonly TimeSpan LearningWindow = TimeSpan.FromDays(7);
    public const int MinLearningDays = 5;

    /// <summary>Combined medium+heavy minutes needed before deltas mean anything.</summary>
    public const int MinLoadedMinutes = 90;

    /// <summary>Minutes a single (bucket, band) cell needs to earn a baseline row.</summary>
    public const int MinBucketMinutes = 8;

    public static bool IsReady(DateTimeOffset epochStart, DateTimeOffset now, IReadOnlyList<BucketStat> statsInWindow)
    {
        double days = (now - epochStart).TotalDays;
        return days >= MinLearningDays && LoadedMinutes(statsInWindow) >= MinLoadedMinutes;
    }

    public static double Progress(DateTimeOffset epochStart, DateTimeOffset now, IReadOnlyList<BucketStat> statsInWindow)
    {
        double dayPart = Math.Min(1.0, (now - epochStart).TotalDays / MinLearningDays);
        double loadPart = Math.Min(1.0, LoadedMinutes(statsInWindow) / (double)MinLoadedMinutes);
        return Math.Round(0.6 * dayPart + 0.4 * loadPart, 3);
    }

    private static int LoadedMinutes(IReadOnlyList<BucketStat> stats) =>
        stats.Where(s => s.Bucket is LoadBucket.Medium or LoadBucket.Heavy).Sum(s => s.Minutes);

    /// <summary>Distill bucket statistics into baseline rows (one per bucket+band
    /// with enough data). p95 comes from per-minute delta distributions.</summary>
    public static List<BaselineRow> Build(
        int epoch,
        ComponentKind kind,
        string name,
        IReadOnlyList<BucketStat> statsInWindow,
        Func<LoadBucket, int, IReadOnlyList<double>> minuteDeltasFor,
        double? soakRateAvg,
        DateTimeOffset now)
    {
        var rows = new List<BaselineRow>();
        foreach (BucketStat s in statsInWindow)
        {
            if (s.DeltaAvg is not { } deltaAvg || s.Minutes < MinBucketMinutes || s.Band < 0)
                continue;

            IReadOnlyList<double> deltas = minuteDeltasFor(s.Bucket, s.Band);
            double? p95 = Percentile(deltas, 0.95);

            rows.Add(new BaselineRow(
                epoch, kind, name, s.Band, s.Bucket,
                deltaAvg, p95, soakRateAvg, s.FanAvg, s.Minutes,
                now.ToUnixTimeSeconds()));
        }
        return rows;
    }

    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return null;
        var sorted = values.OrderBy(v => v).ToList();
        int index = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }
}

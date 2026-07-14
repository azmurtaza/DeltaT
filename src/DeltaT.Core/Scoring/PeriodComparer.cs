using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

/// <summary>One load bucket's weather-corrected rise (ΔT over ambient) in two periods.
/// Either side is null when that period had no comparable minutes in the chosen band.</summary>
public sealed record LoadResponsePoint(
    LoadBucket Bucket, double? EarlierDeltaC, double? RecentDeltaC, int EarlierMinutes, int RecentMinutes);

/// <summary>A like-for-like comparison of two time periods' load-response curves: for
/// each load bucket, the mean rise over ambient, in a single shared weather band so
/// summer is never weighed against winter. <see cref="WeightedChangeC"/> is positive when
/// the recent period runs hotter under load; null when the two periods share too little
/// loaded data to judge.</summary>
public sealed record PeriodComparison(
    int Band,
    IReadOnlyList<LoadResponsePoint> Points,
    double? WeightedChangeC,
    int MatchedLoadedMinutes,
    bool FanCorrected);

/// <summary>Compares two arbitrary time windows the same honest way the repaste verdict
/// compares two epochs: cell-by-cell at matching load bucket, inside one ambient band so
/// the weather is held constant, and fan-normalized so an airflow change (silent mode,
/// max-fan override) between the windows can't masquerade as paste drift. Pure — no
/// clocks, no I/O — so it is deterministic and unit-tested. The caller supplies the two
/// windows' <see cref="BucketStat"/>s (already one component) and turns the numbers into
/// the season-on-season overlay and its plain-English verdict.</summary>
public static class PeriodComparer
{
    private static readonly LoadBucket[] Order =
        { LoadBucket.Idle, LoadBucket.Light, LoadBucket.Medium, LoadBucket.Heavy, LoadBucket.Max };

    private static readonly LoadBucket[] Loaded =
        { LoadBucket.Medium, LoadBucket.Heavy, LoadBucket.Max };

    /// <summary>Matched loaded minutes needed before the weighted change is trusted as a
    /// verdict rather than shown as "not enough overlap yet".</summary>
    public const int MinMatchedLoadedMinutes = 20;

    public static PeriodComparison Compare(IReadOnlyList<BucketStat> earlier, IReadOnlyList<BucketStat> recent)
    {
        int band = ChooseBand(earlier, recent);
        if (band < 0)
            return new PeriodComparison(-1, Array.Empty<LoadResponsePoint>(), null, 0, false);

        var points = new List<LoadResponsePoint>(Order.Length);
        double sumWeighted = 0, sumWeights = 0;
        int matchedLoaded = 0;
        bool fanCorrected = false;

        foreach (LoadBucket bucket in Order)
        {
            BucketStat? e = Pick(earlier, bucket, band);
            BucketStat? r = Pick(recent, bucket, band);
            points.Add(new LoadResponsePoint(
                bucket, e?.DeltaAvg, r?.DeltaAvg, e?.Minutes ?? 0, r?.Minutes ?? 0));

            if (!Loaded.Contains(bucket) || e?.DeltaAvg is not { } earlierDelta || r?.DeltaAvg is not { } recentDelta)
                continue;

            // Express the recent rise at the earlier period's airflow before diffing, so
            // more (or fewer) fan rpm this period doesn't read as a paste change — the
            // same correction the repaste verdict uses.
            double adjusted = recentDelta;
            if (r.FanAvg is { } rFan && e.FanAvg is { } eFan
                && rFan >= ScoringEngine.MinMeaningfulFanRpm && eFan >= ScoringEngine.MinMeaningfulFanRpm)
            {
                double ratio = rFan / eFan;
                if (Math.Abs(ratio - 1) >= ScoringEngine.FanRatioDeadband && recentDelta > 0)
                {
                    double normalized = recentDelta * Math.Pow(ratio, ScoringEngine.FanNormalizationExponent);
                    double correction = Math.Clamp(normalized - recentDelta,
                        -ScoringEngine.MaxFanCorrectionC, ScoringEngine.MaxFanCorrectionC);
                    adjusted = recentDelta + correction;
                    if (Math.Abs(correction) >= 1.0)
                        fanCorrected = true;
                }
            }

            int cellMinutes = Math.Min(e.Minutes, r.Minutes);
            double w = ScoringEngine.Weight(bucket) * Math.Min(1.0, cellMinutes / 60.0 + 0.5);
            sumWeighted += (adjusted - earlierDelta) * w;
            sumWeights += w;
            matchedLoaded += cellMinutes;
        }

        double? change = sumWeights > 0 && matchedLoaded >= MinMatchedLoadedMinutes
            ? Math.Round(sumWeighted / sumWeights, 1)
            : null;
        return new PeriodComparison(band, points, change, matchedLoaded, fanCorrected);
    }

    /// <summary>The ambient band both periods share the most loaded, plugged-in minutes
    /// in — the only fair place to compare. Falls back to the band with the most combined
    /// loaded data when the two periods never overlapped a band (so at least one curve
    /// still draws), and to −1 when neither period saw real load.</summary>
    private static int ChooseBand(IReadOnlyList<BucketStat> earlier, IReadOnlyList<BucketStat> recent)
    {
        int bestShared = -1, bestSharedScore = 0;
        int bestAny = -1, bestAnyScore = 0;
        for (int band = 0; band <= 3; band++)
        {
            int e = LoadedMinutes(earlier, band);
            int r = LoadedMinutes(recent, band);
            int shared = Math.Min(e, r);
            if (shared > bestSharedScore) { bestSharedScore = shared; bestShared = band; }
            int any = e + r;
            if (any > bestAnyScore) { bestAnyScore = any; bestAny = band; }
        }
        return bestShared >= 0 ? bestShared : bestAny;
    }

    private static int LoadedMinutes(IReadOnlyList<BucketStat> stats, int band) =>
        stats.Where(s => s.OnAc && s.Band == band && Loaded.Contains(s.Bucket) && s.DeltaAvg is not null)
             .Sum(s => s.Minutes);

    /// <summary>The plugged-in cell for one (bucket, band) with usable rise data.</summary>
    private static BucketStat? Pick(IReadOnlyList<BucketStat> stats, LoadBucket bucket, int band) =>
        stats.FirstOrDefault(s => s.OnAc && s.Bucket == bucket && s.Band == band
                                  && s.DeltaAvg is not null && s.Minutes > 0);
}

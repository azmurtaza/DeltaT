using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

public enum RepasteVerdict
{
    Inconclusive, // not enough comparable load in both epochs to judge
    Improved,     // new paste runs measurably cooler
    Unchanged,    // within noise
    Worse,        // new paste runs measurably hotter — bad application
}

/// <summary>Result of comparing two epochs' learned baselines like-for-like.
/// <see cref="WeightedDeltaChangeC"/> is negative when the newer paste is cooler.</summary>
public sealed record BaselineComparison(
    RepasteVerdict Verdict,
    double WeightedDeltaChangeC,
    int MatchedMinutes,
    bool FanCorrected);

/// <summary>Compares an earlier baseline (before a repaste) against the newly
/// learned one, the honest way: cell-by-cell at matching load bucket and ambient
/// band, fan-normalized so a fan-curve change between the two learning weeks can't
/// be mistaken for a paste change. Pure — no clocks, no I/O — so a repaste that
/// made things *worse* is caught with the same rigor as one that helped.</summary>
public static class BaselineComparer
{
    /// <summary>°C of weighted change beyond which we call it a real move. A floor:
    /// even a statistically clean shift smaller than this is practically noise.</summary>
    public const double SignificantChangeC = 1.5;

    /// <summary>How many standard errors the weighted change must clear to count as
    /// real when both epochs carry per-cell standard errors. ~2σ ≈ 95% confidence,
    /// so two tight baselines can be separated by less than the °C floor, while two
    /// noisy ones must move more — an honest, self-scaling bar.</summary>
    public const double SignificanceSigma = 2.0;

    /// <summary>Matched heavy+medium minutes needed before the verdict is trusted.</summary>
    public const int MinConclusiveMinutes = 30;

    public static BaselineComparison Compare(
        IReadOnlyList<BaselineRow> before, IReadOnlyList<BaselineRow> after, ComponentKind kind)
    {
        double sumWeighted = 0, sumWeights = 0, sumSeSq = 0;
        int matchedMinutes = 0, loadedMatchedMinutes = 0;
        bool fanCorrected = false, haveSe = false;

        foreach (BaselineRow a in after.Where(r => r.Kind == kind))
        {
            BaselineRow? b = before.FirstOrDefault(r => r.Kind == kind && r.Bucket == a.Bucket && r.Band == a.Band);
            if (b is null)
                continue; // only cells both epochs learned are a fair comparison

            int cellMinutes = Math.Min(a.Minutes, b.Minutes);
            matchedMinutes += cellMinutes;
            if (a.Bucket is LoadBucket.Heavy or LoadBucket.Medium or LoadBucket.Max)
                loadedMatchedMinutes += cellMinutes;

            // Express the newer delta at the older baseline's airflow before diffing,
            // so more (or fewer) fan rpm this epoch doesn't masquerade as paste change.
            double afterDelta = a.DeltaAvg;
            double correction = 0;
            if (a.FanAvg is { } aFan && b.FanAvg is { } bFan
                && aFan >= ScoringEngine.MinMeaningfulFanRpm && bFan >= ScoringEngine.MinMeaningfulFanRpm)
            {
                double ratio = aFan / bFan;
                if (Math.Abs(ratio - 1) >= ScoringEngine.FanRatioDeadband && afterDelta > 0)
                {
                    double normalized = afterDelta * Math.Pow(ratio, ScoringEngine.FanNormalizationExponent);
                    correction = Math.Clamp(normalized - afterDelta, -ScoringEngine.MaxFanCorrectionC, ScoringEngine.MaxFanCorrectionC);
                    if (Math.Abs(correction) >= 1.0)
                        fanCorrected = true;
                }
            }

            double change = (afterDelta + correction) - b.DeltaAvg; // <0 = cooler = better
            double w = ScoringEngine.Weight(a.Bucket) * Math.Min(1.0, cellMinutes / 60.0 + 0.5);
            sumWeighted += change * w;
            sumWeights += w;

            // Variance of this cell's change = se_after² + se_before² (independent means).
            // Propagate it, weighted, so the aggregate carries its own standard error.
            if (a.DeltaSe is { } seA && b.DeltaSe is { } seB)
            {
                haveSe = true;
                double cellSe = Math.Sqrt(seA * seA + seB * seB);
                sumSeSq += (w * cellSe) * (w * cellSe);
            }
        }

        if (sumWeights <= 0 || loadedMatchedMinutes < MinConclusiveMinutes)
            return new BaselineComparison(RepasteVerdict.Inconclusive, 0, matchedMinutes, fanCorrected);

        double weightedChange = sumWeighted / sumWeights;

        // The move must clear the practical °C floor and, when we have the standard
        // errors to say so, be statistically distinguishable from no change at all.
        double threshold = SignificantChangeC;
        if (haveSe)
        {
            double weightedSe = Math.Sqrt(sumSeSq) / sumWeights;
            threshold = Math.Max(SignificantChangeC, SignificanceSigma * weightedSe);
        }

        RepasteVerdict verdict =
            weightedChange <= -threshold ? RepasteVerdict.Improved
            : weightedChange >= threshold ? RepasteVerdict.Worse
            : RepasteVerdict.Unchanged;

        return new BaselineComparison(verdict, Math.Round(weightedChange, 1), matchedMinutes, fanCorrected);
    }
}

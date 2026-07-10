using DeltaT.Core.Monitoring;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

/// <summary>How confident DeltaT is that the learned baseline describes this
/// machine's real "normal" — and, when it isn't yet, the one thing holding it
/// back. This is what the calibration meter shows.</summary>
public sealed record CalibrationState(
    bool Ready,
    double Confidence,       // 0..1 — the honest meter: how much you can trust the eventual score
    string Constraint,       // human phrasing of the binding limiter
    double DataConfidence,   // 0..1 — statistical precision × coverage × independence
    double CureMaturity,     // 0..1 — paste break-in progress (the only wall-clock term)
    int LoadedSessions,      // independent heavy/medium sessions observed so far
    double? BindingSeC);     // standard error of the weakest loaded cell, if known

/// <summary>Builds the "what healthy looks like" reference for an epoch, and — more
/// importantly — decides when that reference is trustworthy.
///
/// Calibration is not a countdown. The old design gated the score on a fixed
/// 7-day timer, which meant a machine that had already been stressed hard on day 1
/// still sat "calibrating" for a week, the meter creeping up on nothing but the
/// clock. This version tracks the thing that actually matters: how *precisely* we
/// know this machine's normal, per load bucket, and whether the fresh paste has
/// settled. Feed it varied load and it locks sooner; never stress it and it
/// honestly stays unlocked for the buckets it has never seen.
///
/// The statistical spine is the <b>standard error of session means</b>. Per-minute
/// deltas inside one gaming session are heavily autocorrelated — minute 40 tells
/// you almost nothing new over minute 1 — so counting minutes overstates how much
/// you've learned (that was the old "90 loaded minutes"). Instead each contiguous
/// loaded session collapses to a single mean-delta observation; those are ~independent,
/// so <c>SE = stdev(sessionMeans)/sqrt(n)</c> is a real estimate of how well the
/// baseline mean is pinned. It self-scales: a thermally stable machine converges in
/// a few sessions, a noisy one is correctly made to wait for more.</summary>
public static class BaselineBuilder
{
    // --- derived tunables (each traceable to a physical or statistical reason) ---

    /// <summary>Target standard error for a loaded baseline cell. Derived from the
    /// smallest drift worth flagging (~+3 °C of paste degradation): to separate that
    /// from noise cleanly we want the baseline mean pinned to roughly a quarter of it.</summary>
    public const double TargetBaselineSeC = 0.7;

    /// <summary>Independent loaded sessions a single cell needs before its variance —
    /// and therefore its confidence — can be estimated at all.</summary>
    public const int MinSessionsPerCell = 3;

    /// <summary>Independent heavy/medium sessions needed overall. Enforces sample
    /// independence and, for free, thermal cycling of the fresh paste — each session
    /// is a heat/cool cycle, which is exactly what a break-in wants.</summary>
    public const int MinLoadedSessions = 3;

    /// <summary>A gap larger than this between consecutive loaded minutes ends a
    /// session. Ten minutes comfortably separates distinct usage bouts without
    /// splitting a single game across a loading screen.</summary>
    public const int SessionGapSeconds = 10 * 60;

    /// <summary>Paste break-in floor: below this, a fresh mount hasn't settled at all,
    /// so confidence is capped hard however good the data looks.</summary>
    public static readonly TimeSpan CureFloor = TimeSpan.FromHours(48);

    /// <summary>By here the paste is treated as fully cured (typical TIM reaches
    /// steady conductivity within a few days of thermal cycling).</summary>
    public static readonly TimeSpan CureFull = TimeSpan.FromHours(96);

    /// <summary>Confidence at or above which the baseline locks and real scoring begins.</summary>
    public const double ReadyConfidence = 0.75;

    /// <summary>Confidence floor while the paste is still within the break-in window —
    /// low enough to keep the score honest, non-zero so the meter still moves.</summary>
    private const double CureFloorConfidence = 0.10;

    /// <summary>Minutes a single (bucket, band) cell needs to earn a baseline row.</summary>
    public const int MinBucketMinutes = 8;

    /// <summary>Buckets whose heat actually has to cross the paste — the ones that
    /// define a paste-health baseline. Idle/light are recorded but never gate readiness.</summary>
    private static readonly LoadBucket[] LoadedBuckets = { LoadBucket.Heavy, LoadBucket.Medium };

    /// <summary>Assess whether the epoch's baseline can be trusted yet, and by how much.
    /// Pure: the caller supplies the per-cell session means (segmentation lives in
    /// storage, which owns the timestamps) exactly as it supplies minute deltas to
    /// <see cref="Build"/>.
    ///
    /// <paramref name="independentLoadedSessions"/> is the number of distinct loaded
    /// usage bouts, deduplicated across buckets and bands — one gaming session that
    /// oscillates Heavy↔Medium (or straddles an ambient-band edge) is one observation,
    /// not four. Per-cell session means still drive each cell's standard error; this
    /// count only gates overall independence.
    ///
    /// <paramref name="pasteIsFresh"/>: the cure ramp models a physical process — fresh
    /// TIM settling over its first days of thermal cycling. That only exists when the
    /// epoch began with an actual repaste. A first install or a recalibration is watching
    /// paste that is already as cured as it will ever be, so gating those epochs on the
    /// clock would just delay an honest lock for no physical reason.</summary>
    public static CalibrationState Assess(
        DateTimeOffset epochStart,
        DateTimeOffset now,
        IReadOnlyList<BucketStat> statsInWindow,
        Func<LoadBucket, int, IReadOnlyList<double>> sessionMeansFor,
        int independentLoadedSessions,
        bool pasteIsFresh)
    {
        double cure = pasteIsFresh ? CureMaturity(epochStart, now) : 1.0;

        var cells = new List<CellConfidence>();
        foreach (BucketStat s in statsInWindow)
        {
            if (!LoadedBuckets.Contains(s.Bucket) || s.Band < 0 || s.Minutes < MinBucketMinutes || s.DeltaAvg is null)
                continue;

            IReadOnlyList<double> means = sessionMeansFor(s.Bucket, s.Band);
            double? se = StandardError(means);
            cells.Add(new CellConfidence(s.Bucket, s.Band, means.Count, se, CellScore(means.Count, se)));
        }
        int totalLoadedSessions = independentLoadedSessions;

        double dataConf;
        string dataConstraint;
        double? bindingSe = null;

        if (cells.Count == 0)
        {
            dataConf = 0;
            dataConstraint = "no heavy or medium load learned yet - run a game or a stress test so DeltaT can see the paste working";
        }
        else
        {
            double wSum = 0, cSum = 0;
            foreach (CellConfidence c in cells)
            {
                double w = ScoringEngine.Weight(c.Bucket);
                wSum += w;
                cSum += w * c.Score;
            }
            // Independence gate: too few distinct sessions and even tight-looking cells
            // aren't yet trustworthy (one session can't disprove a fluke).
            double sessionGate = Math.Min(1.0, totalLoadedSessions / (double)MinLoadedSessions);
            dataConf = (cSum / wSum) * sessionGate;

            CellConfidence binding = cells.OrderBy(c => c.Score).First();
            bindingSe = binding.SeC;
            dataConstraint = DescribeDataConstraint(binding, totalLoadedSessions);
        }

        double confidence = Math.Min(dataConf, cure);
        string constraint = cure < dataConf ? DescribeCure(epochStart, now) : dataConstraint;
        bool ready = confidence >= ReadyConfidence;

        return new CalibrationState(
            ready, Round3(confidence), constraint,
            Round3(dataConf), Round3(cure), totalLoadedSessions, bindingSe);
    }

    private readonly record struct CellConfidence(LoadBucket Bucket, int Band, int Sessions, double? SeC, double Score);

    /// <summary>Confidence contributed by one loaded cell. No confidence until we have
    /// enough independent sessions to estimate variance; after that it's how far the
    /// cell's standard error sits below target.</summary>
    private static double CellScore(int sessions, double? seC)
    {
        if (sessions <= 0)
            return 0;
        if (sessions < MinSessionsPerCell)
            return 0.5 * sessions / MinSessionsPerCell; // partial credit, capped — can't yet be sure
        if (seC is not { } se || se <= 0)
            return 1.0; // enough sessions and they agree perfectly
        return Math.Min(1.0, TargetBaselineSeC / se);
    }

    /// <summary>Standard error of the mean from independent session means. Null below
    /// two sessions (no spread to estimate).</summary>
    public static double? StandardError(IReadOnlyList<double> sessionMeans)
    {
        int n = sessionMeans.Count;
        if (n < 2)
            return null;
        double mean = sessionMeans.Average();
        double variance = sessionMeans.Sum(v => (v - mean) * (v - mean)) / (n - 1);
        return Math.Sqrt(variance) / Math.Sqrt(n);
    }

    /// <summary>Break-in progress: a linear ramp from a hard floor at
    /// <see cref="CureFloor"/> to fully cured at <see cref="CureFull"/>. This is the
    /// only place a clock touches the score, and it's justified by real TIM physics,
    /// not an arbitrary calibration length.</summary>
    public static double CureMaturity(DateTimeOffset epochStart, DateTimeOffset now)
    {
        double hours = (now - epochStart).TotalHours;
        double floorH = CureFloor.TotalHours, fullH = CureFull.TotalHours;
        if (hours <= floorH)
            return CureFloorConfidence;
        if (hours >= fullH)
            return 1.0;
        double t = (hours - floorH) / (fullH - floorH);
        return CureFloorConfidence + (1.0 - CureFloorConfidence) * t;
    }

    private static string DescribeCure(DateTimeOffset epochStart, DateTimeOffset now)
    {
        // Hours until cure alone would clear the ready threshold.
        double floorH = CureFloor.TotalHours, fullH = CureFull.TotalHours;
        double hoursToReady = floorH + (ReadyConfidence - CureFloorConfidence) / (1.0 - CureFloorConfidence) * (fullH - floorH);
        int left = Math.Max(1, (int)Math.Ceiling(hoursToReady - (now - epochStart).TotalHours));
        return $"the fresh paste is still settling - about {left} more hour{(left == 1 ? "" : "s")} of use and DeltaT can lock the baseline (thermal paste keeps improving over its first few days)";
    }

    private static string DescribeDataConstraint(CellConfidence binding, int totalSessions)
    {
        if (totalSessions < MinLoadedSessions)
        {
            int more = MinLoadedSessions - totalSessions;
            return $"need {more} more separate load session{(more == 1 ? "" : "s")} - each game or heavy task, split by a cool-down, sharpens the baseline";
        }
        if (binding.Sessions < MinSessionsPerCell)
        {
            int more = MinSessionsPerCell - binding.Sessions;
            return $"{binding.Bucket.Label()} in {BandLabel(binding.Band)} needs {more} more session{(more == 1 ? "" : "s")} to pin down";
        }
        string spread = binding.SeC is { } se ? $" (±{se:0.#} °C)" : "";
        return $"{binding.Bucket.Label()} readings still vary a little{spread} - a few more sessions will tighten it";
    }

    private static string BandLabel(int band) =>
        band is >= 0 and <= 3 ? ((AmbientBand)band).Label() : "this weather";

    /// <summary>Distill bucket statistics into baseline rows (one per bucket+band
    /// with enough data). p95 comes from per-minute delta distributions; the standard
    /// error comes from session means, so later comparisons can be significance-tested.</summary>
    public static List<BaselineRow> Build(
        int epoch,
        ComponentKind kind,
        string name,
        IReadOnlyList<BucketStat> statsInWindow,
        Func<LoadBucket, int, IReadOnlyList<double>> minuteDeltasFor,
        Func<LoadBucket, int, IReadOnlyList<double>> sessionMeansFor,
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
            double? se = StandardError(sessionMeansFor(s.Bucket, s.Band));

            rows.Add(new BaselineRow(
                epoch, kind, name, s.Band, s.Bucket,
                deltaAvg, p95, soakRateAvg, s.FanAvg, s.Minutes,
                now.ToUnixTimeSeconds(), se, s.TempAvg));
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

    private static double Round3(double v) => Math.Round(v, 3);
}

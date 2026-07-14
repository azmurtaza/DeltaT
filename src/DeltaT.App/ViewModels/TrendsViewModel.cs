using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Controls;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;

namespace DeltaT.App.ViewModels;

public sealed record StatCell(string Label, string Value);

public partial class TrendsViewModel : ObservableObject
{
    private static readonly (string Label, ComponentKind Kind)[] KindTabs =
    {
        ("CPU", ComponentKind.Cpu), ("GPU", ComponentKind.GpuDiscrete), ("SSD", ComponentKind.Storage),
    };

    private readonly TelemetryRepository _repo;
    private readonly Dispatcher _dispatcher;

    public string[] Kinds { get; } = KindTabs.Select(k => k.Label).ToArray();

    [ObservableProperty] private int _selectedKindIndex;
    [ObservableProperty] private string _range = "24h";
    [ObservableProperty] private IReadOnlyList<ChartPoint>? _points;
    [ObservableProperty] private IReadOnlyList<ChartPoint>? _ambientPoints;
    [ObservableProperty] private IReadOnlyList<ChartMarker>? _markers;
    [ObservableProperty] private bool _loading;

    // Compare mode: two periods' load-response curves overlaid, weather-corrected.
    [ObservableProperty] private bool _comparing;
    [ObservableProperty] private string _comparePreset = "30d";
    [ObservableProperty] private string _chartTitle = "Thermal history";
    [ObservableProperty] private IReadOnlyList<LoadResponseDatum>? _responseSeries;
    [ObservableProperty] private string _recentLabel = "RECENT";
    [ObservableProperty] private string _earlierLabel = "EARLIER";
    [ObservableProperty] private string _compareVerdict = "";

    public ObservableCollection<StatCell> Stats { get; } = new();

    public TrendsViewModel(TelemetryRepository repo)
    {
        _repo = repo;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    partial void OnSelectedKindIndexChanged(int value) => _ = RefreshAsync();

    /// <summary>A normal range button was picked: leave compare mode and show history.</summary>
    [RelayCommand]
    private void SetRange(string range)
    {
        Range = range;
        Comparing = false;
        _ = RefreshAsync();
    }

    /// <summary>Enter compare mode (season-on-season overlay), defaulting to the last
    /// 30 days versus the prior 30.</summary>
    [RelayCommand]
    private void EnterCompare()
    {
        Comparing = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void SetComparePreset(string preset)
    {
        ComparePreset = preset;
        Comparing = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Loading) return;
        Loading = true;
        try
        {
            if (Comparing)
                await RefreshComparisonAsync();
            else
                await RefreshHistoryAsync();
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        ChartTitle = "Thermal history";
        ComponentKind kind = KindTabs[Math.Clamp(SelectedKindIndex, 0, KindTabs.Length - 1)].Kind;
        string range = Range;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (long from, string resolution) = range switch
        {
            "24h" => (now - 86400, "minute"),
            "7d" => (now - 7 * 86400, "hour"),
            "30d" => (now - 30 * 86400, "hour"),
            _ => (0L, "hour"),
        };

        var result = await Task.Run(() =>
        {
            IReadOnlyList<SeriesPoint> series = _repo.GetSeries(kind, null, from, now, resolution);
            var pts = new List<ChartPoint>(series.Count);
            var amb = new List<ChartPoint>(series.Count);
            foreach (SeriesPoint p in series)
            {
                if (p.TempAvg is not { } avg) continue;
                pts.Add(new ChartPoint(p.Ts, avg, p.TempMin ?? avg, p.TempMax ?? avg));
                if (p.Ambient is { } a)
                    amb.Add(new ChartPoint(p.Ts, a, a, a));
            }

            var markers = new List<ChartMarker>();
            foreach (StoredEvent e in _repo.GetEvents("repaste", from, now, 20))
                markers.Add(new ChartMarker(e.Ts, "R", ThermalPalette.Good));
            foreach (StoredEvent e in _repo.GetEvents("fingerprint", from, now, 20))
                markers.Add(new ChartMarker(e.Ts, "F", ThermalPalette.Accent));
            var throttles = _repo.GetEvents("throttle", from, now, 31)
                .Where(e => e.Kind == kind.ToString()).ToList();
            if (throttles.Count <= 30)
                markers.AddRange(throttles.Select(e => new ChartMarker(e.Ts, "T", ThermalPalette.Hot)));

            int throttleCount = _repo.CountEvents("throttle", kind, from, now);
            var stats = new List<StatCell>();
            if (pts.Count > 0)
            {
                stats.Add(new StatCell("AVG", $"{pts.Average(p => p.Avg):0.#}°"));
                stats.Add(new StatCell("MIN", $"{pts.Min(p => p.Min):0.#}°"));
                stats.Add(new StatCell("MAX", $"{pts.Max(p => p.Max):0.#}°"));
                var deltas = series.Where(p => p.TempAvg is not null && p.Ambient is not null)
                                   .Select(p => p.TempAvg!.Value - p.Ambient!.Value).ToList();
                stats.Add(new StatCell("Δ OUTSIDE", deltas.Count > 0 ? $"+{deltas.Average():0.#}°" : "-"));
                stats.Add(new StatCell("THROTTLES", throttleCount.ToString()));
            }
            return (Points: (IReadOnlyList<ChartPoint>)pts, Ambient: (IReadOnlyList<ChartPoint>)amb,
                    Markers: (IReadOnlyList<ChartMarker>)markers, Stats: stats);
        });

        await _dispatcher.BeginInvoke(() =>
        {
            Points = result.Points;
            AmbientPoints = result.Ambient;
            Markers = result.Markers;
            Stats.Clear();
            foreach (StatCell s in result.Stats)
                Stats.Add(s);
        });
    }

    private async Task RefreshComparisonAsync()
    {
        ChartTitle = "Load-response comparison";
        ComponentKind kind = KindTabs[Math.Clamp(SelectedKindIndex, 0, KindTabs.Length - 1)].Kind;
        string kindLabel = KindTabs[Math.Clamp(SelectedKindIndex, 0, KindTabs.Length - 1)].Label;
        string preset = ComparePreset;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Recent window versus the equivalent earlier one. The season preset reaches back
        // a year (agg_hour, which is never pruned); the shorter presets read minute rows.
        const long day = 86400;
        (long rFrom, long rTo, long eFrom, long eTo, string recentLbl, string earlierLbl, string earlierPhrase, string resolution) = preset switch
        {
            "7d" => (now - 7 * day, now, now - 14 * day, now - 7 * day, "LAST 7 DAYS", "PRIOR 7 DAYS", "the prior week", "minute"),
            "season" => (now - 90 * day, now, now - 455 * day, now - 365 * day, "THIS SEASON", "A YEAR AGO", "a year ago", "hour"),
            _ => (now - 30 * day, now, now - 60 * day, now - 30 * day, "LAST 30 DAYS", "PRIOR 30 DAYS", "the prior month", "minute"),
        };

        var result = await Task.Run(() =>
        {
            IReadOnlyList<BucketStat> recent = _repo.GetBucketStats(kind, null, rFrom, rTo, resolution);
            IReadOnlyList<BucketStat> earlier = _repo.GetBucketStats(kind, null, eFrom, eTo, resolution);
            PeriodComparison cmp = PeriodComparer.Compare(earlier, recent);

            var datums = cmp.Points
                .Select(p => new LoadResponseDatum(Short(p.Bucket), p.EarlierDeltaC, p.RecentDeltaC))
                .ToList();

            var stats = new List<StatCell>();
            foreach (LoadResponsePoint p in cmp.Points)
            {
                if (p.Bucket is not (LoadBucket.Medium or LoadBucket.Heavy or LoadBucket.Max)) continue;
                if (p.EarlierDeltaC is { } e && p.RecentDeltaC is { } r)
                    stats.Add(new StatCell(Short(p.Bucket), $"{r - e:+0.#;-0.#}°"));
            }
            if (cmp.Band >= 0)
                stats.Add(new StatCell("WEATHER", BandShort(cmp.Band)));

            string verdict = BuildVerdict(cmp, kindLabel, earlierPhrase);
            return (Datums: (IReadOnlyList<LoadResponseDatum>)datums, Stats: stats,
                    RecentLbl: recentLbl, EarlierLbl: earlierLbl, Verdict: verdict);
        });

        await _dispatcher.BeginInvoke(() =>
        {
            ResponseSeries = result.Datums;
            RecentLabel = result.RecentLbl;
            EarlierLabel = result.EarlierLbl;
            CompareVerdict = result.Verdict;
            Stats.Clear();
            foreach (StatCell s in result.Stats)
                Stats.Add(s);
        });
    }

    private static string BuildVerdict(PeriodComparison cmp, string kindLabel, string earlierPhrase)
    {
        if (cmp.WeightedChangeC is not { } change)
            return $"Not enough matched load in both periods to compare yet. Give each stretch some real load in similar weather and the drift will show up here.";

        string weather = cmp.Band >= 0 ? $" in {BandLabel(cmp.Band)}" : "";
        string corrected = cmp.FanCorrected ? "weather-corrected and fan-corrected" : "weather-corrected";
        if (Math.Abs(change) < 1.5)
            return $"{kindLabel} runs essentially the same under load as {earlierPhrase} ({change:+0.#;-0.#}°, {corrected}{weather}). No meaningful drift.";
        return change > 0
            ? $"{kindLabel} runs {change:0.#}° hotter under load than {earlierPhrase} ({corrected}{weather}). A steady month-over-month climb is the paste drying out."
            : $"{kindLabel} runs {-change:0.#}° cooler under load than {earlierPhrase} ({corrected}{weather}). Cleaner airflow, fresh paste, or a cooler room.";
    }

    private static string Short(LoadBucket b) => b switch
    {
        LoadBucket.Idle => "IDLE",
        LoadBucket.Light => "LIGHT",
        LoadBucket.Medium => "MEDIUM",
        LoadBucket.Heavy => "HEAVY",
        _ => "FULL",
    };

    private static string BandShort(int band) => band switch
    {
        0 => "COLD", 1 => "MILD", 2 => "WARM", 3 => "HOT", _ => "-",
    };

    private static string BandLabel(int band) => band switch
    {
        0 => "cold weather", 1 => "mild weather", 2 => "warm weather", 3 => "hot weather", _ => "unknown weather",
    };
}

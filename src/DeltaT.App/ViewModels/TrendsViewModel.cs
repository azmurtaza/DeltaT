using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Controls;
using DeltaT.Core.Monitoring;
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

    public ObservableCollection<StatCell> Stats { get; } = new();

    public TrendsViewModel(TelemetryRepository repo)
    {
        _repo = repo;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    partial void OnSelectedKindIndexChanged(int value) => _ = RefreshAsync();

    [RelayCommand]
    private void SetRange(string range)
    {
        Range = range;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Loading) return;
        Loading = true;
        try
        {
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
        finally
        {
            Loading = false;
        }
    }
}

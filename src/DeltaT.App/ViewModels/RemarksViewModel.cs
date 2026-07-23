using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaT.App.Controls;
using DeltaT.App.Services;
using DeltaT.Core.Remarks;
using DeltaT.Core.Storage;

namespace DeltaT.App.ViewModels;

public sealed record RemarkItem(string TimeText, string Text, Brush Dot, string Badge);

public partial class RemarksViewModel : ObservableObject
{
    private readonly TelemetryRepository _repo;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<RemarkItem> Items { get; } = new();

    [ObservableProperty] private bool _importantOnly;
    [ObservableProperty] private string _emptyText = "Nothing yet. DeltaT speaks when there's something worth saying.";

    public RemarksViewModel(TelemetryRepository repo)
    {
        _repo = repo;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    partial void OnImportantOnlyChanged(bool value) => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        bool importantOnly = ImportantOnly;
        var items = await Task.Run(() =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var events = new List<StoredEvent>();
            events.AddRange(_repo.GetEvents("remark", 0, now, 250));
            events.AddRange(_repo.GetEvents("repaste", 0, now, 25));
            events.AddRange(_repo.GetEvents("fingerprint", 0, now, 25));
            events.AddRange(_repo.GetEvents("throttle", 0, now, 50));
            return events
                .Where(e => !importantOnly || e.Severity >= 2 || e.Type is "repaste" or "fingerprint")
                .OrderByDescending(e => e.Ts)
                .Take(250)
                .Select(ToItem)
                .ToList();
        });

        await _dispatcher.BeginInvoke(() =>
        {
            Items.Clear();
            foreach (RemarkItem item in items)
                Items.Add(item);
        });
    }

    public void Prepend(Remark remark) =>
        Items.Insert(0, new RemarkItem(
            remark.TimestampUtc.ToLocalTime().ToString(TimeFormat.DateDotTime),
            remark.Text,
            DotFor(remark.Severity switch
            {
                RemarkSeverity.Alert => 3, RemarkSeverity.Warning => 2, RemarkSeverity.Notice => 1, _ => 0,
            }, "remark"),
            "REMARK"));

    private static RemarkItem ToItem(StoredEvent e) => new(
        DateTimeOffset.FromUnixTimeSeconds(e.Ts).ToLocalTime().ToString(TimeFormat.DateDotTime),
        e.Message,
        DotFor(e.Severity, e.Type),
        e.Type.ToUpperInvariant());

    private static Brush DotFor(int severity, string type)
    {
        Color c = type switch
        {
            "repaste" => ThermalPalette.Good,
            "fingerprint" => ThermalPalette.Accent,
            "throttle" => ThermalPalette.Hot,
            _ => severity switch
            {
                >= 3 => ThermalPalette.Hot,
                2 => ThermalPalette.HotWarn,
                1 => ThermalPalette.Accent,
                _ => ThermalPalette.TextDim,
            },
        };
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}

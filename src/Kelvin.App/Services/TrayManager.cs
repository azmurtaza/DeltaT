using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Kelvin.App.Controls;
using Kelvin.Core.Monitoring;

namespace Kelvin.App.Services;

/// <summary>The always-there part of Kelvin: a tray icon that literally shows the
/// hottest paste-relevant temperature as its glyph, tinted by the thermal palette.
/// Icon redraws are throttled and skipped when nothing changed.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly MonitoringService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly MenuItem _pauseItem;
    private DateTimeOffset _lastIconUpdate = DateTimeOffset.MinValue;
    private int _lastShownTemp = int.MinValue;

    public TrayManager(MonitoringService monitor, Action showWindow, Action quit)
    {
        _monitor = monitor;
        _dispatcher = Application.Current.Dispatcher;

        _pauseItem = new MenuItem { Header = "Pause monitoring" };
        _pauseItem.Click += (_, _) =>
        {
            _monitor.IsPaused = !_monitor.IsPaused;
            _pauseItem.Header = _monitor.IsPaused ? "Resume monitoring" : "Pause monitoring";
        };

        var open = new MenuItem { Header = "Open Kelvin" };
        open.Click += (_, _) => showWindow();
        var quitItem = new MenuItem { Header = "Quit Kelvin" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Kelvin — warming up",
            ContextMenu = menu,
            IconSource = RenderIcon(null),
        };
        _tray.TrayLeftMouseUp += (_, _) => showWindow();

        _monitor.SnapshotCaptured += OnSnapshot;
    }

    public void ShowFirstTrayHint()
    {
        _tray.ShowBalloonTip("Kelvin is still watching",
            "Closing the window keeps monitoring alive here in the tray. Right-click the icon to quit for real.",
            BalloonIcon.Info);
    }

    public void ShowRemarkToast(Kelvin.Core.Remarks.Remark remark)
    {
        _tray.ShowBalloonTip(
            remark.Severity == Kelvin.Core.Remarks.RemarkSeverity.Alert ? "Kelvin — needs attention" : "Kelvin noticed",
            remark.Text,
            remark.Severity == Kelvin.Core.Remarks.RemarkSeverity.Alert ? BalloonIcon.Error : BalloonIcon.Warning);
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastIconUpdate < TimeSpan.FromSeconds(5))
            return;

        var temps = snap.Components
            .Where(c => c.Kind is ComponentKind.Cpu or ComponentKind.GpuDiscrete or ComponentKind.Storage)
            .Where(c => c.TemperatureC.HasValue)
            .Select(c => (c.Kind, Temp: c.TemperatureC!.Value))
            .ToList();
        if (temps.Count == 0)
            return;

        int hottest = (int)Math.Round(temps.Max(t => t.Temp));
        double limit = snap.Find(ComponentKind.Cpu)?.ThrottleLimitC ?? 100;
        string tooltip = "Kelvin  ·  " + string.Join("  ", temps.Select(t => $"{Short(t.Kind)} {t.Temp:0}°"));
        _lastIconUpdate = now;

        bool redraw = hottest != _lastShownTemp;
        _lastShownTemp = hottest;

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _tray.ToolTipText = tooltip;
            if (redraw)
                _tray.IconSource = RenderIcon(hottest, hottest / limit);
        });
    }

    private static string Short(ComponentKind kind) => kind switch
    {
        ComponentKind.Cpu => "CPU",
        ComponentKind.GpuDiscrete => "GPU",
        _ => "SSD",
    };

    /// <summary>Draws the tray glyph: the temperature itself on a dark rounded tile.</summary>
    private static BitmapSource RenderIcon(int? temp, double fraction = 0)
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x14));
            Color accent = temp is null ? ThermalPalette.Accent : ThermalPalette.ColorFromFraction(fraction);
            dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(accent), 2), new Rect(1, 1, size - 2, size - 2), 7, 7);

            string text = temp?.ToString(CultureInfo.InvariantCulture) ?? "K";
            double fontSize = text.Length >= 3 ? 15 : 18;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Cascadia Code, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize, new SolidColorBrush(accent), 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        _monitor.SnapshotCaptured -= OnSnapshot;
        _tray.Dispose();
    }
}

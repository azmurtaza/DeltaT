using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using DeltaT.App.Controls;
using DeltaT.Core.Monitoring;

namespace DeltaT.App.Services;

/// <summary>The always-there part of DeltaT: a tray icon that literally shows the
/// hottest paste-relevant temperature as its glyph, tinted by the thermal palette,
/// with a heat bar underneath. Icon redraws are throttled and skipped when
/// nothing changed.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly MonitoringService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly MenuItem _pauseItem;
    private DateTimeOffset _lastIconUpdate = DateTimeOffset.MinValue;
    private int _lastShownTemp = int.MinValue;
    private string _lastTooltip = "";
    private volatile string? _pendingTooltip;
    private volatile bool _pendingRedraw;
    private int _pendingTemp;
    private double _pendingFraction;
    private int _uiHopQueued;

    public TrayManager(MonitoringService monitor, Action showWindow, Action quit)
    {
        _monitor = monitor;
        _dispatcher = Application.Current.Dispatcher;

        _pauseItem = new MenuItem { Header = "Pause monitoring" };
        _pauseItem.Click += (_, _) =>
        {
            _monitor.IsPaused = !_monitor.IsPaused;
            _pauseItem.Header = _monitor.IsPaused ? "Resume monitoring" : "Pause monitoring";
            if (_monitor.IsPaused)
            {
                // The menu can only be clicked after the constructor finished.
                _tray!.ToolTipText = "DeltaT - monitoring paused";
                _lastTooltip = "";
            }
        };

        var open = new MenuItem { Header = "Open DeltaT" };
        open.Click += (_, _) => showWindow();
        var quitItem = new MenuItem { Header = "Quit DeltaT" };
        quitItem.Click += (_, _) => quit();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        _tray = new TaskbarIcon
        {
            ToolTipText = "DeltaT - warming up",
            ContextMenu = menu,
            IconSource = RenderIcon(null),
        };
        _tray.TrayLeftMouseUp += (_, _) => showWindow();

        _monitor.SnapshotCaptured += OnSnapshot;
    }

    public void ShowFirstTrayHint()
    {
        _tray.ShowBalloonTip("DeltaT is still watching",
            "Closing the window keeps monitoring alive here in the tray. Right-click the icon to quit for real.",
            BalloonIcon.Info);
    }

    public void ShowInfo(string title, string message)
    {
        _tray.ShowBalloonTip(title, message, BalloonIcon.Info);
    }

    public void ShowRemarkToast(DeltaT.Core.Remarks.Remark remark)
    {
        _tray.ShowBalloonTip(
            remark.Severity == DeltaT.Core.Remarks.RemarkSeverity.Alert ? "DeltaT - needs attention" : "DeltaT noticed",
            remark.Text,
            remark.Severity == DeltaT.Core.Remarks.RemarkSeverity.Alert ? BalloonIcon.Error : BalloonIcon.Warning);
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastIconUpdate < TimeSpan.FromSeconds(5))
            return;

        double hottestC = double.MinValue;
        var sb = new StringBuilder("DeltaT ");
        foreach (ComponentReading c in snap.Components)
        {
            if (c.Kind is not (ComponentKind.Cpu or ComponentKind.GpuDiscrete or ComponentKind.Storage)
                || c.TemperatureC is not { } temp)
                continue;
            sb.Append(' ').Append(Short(c.Kind)).Append(' ').Append(temp.ToString("0", CultureInfo.InvariantCulture)).Append('°');
            if (temp > hottestC)
                hottestC = temp;
        }
        if (hottestC == double.MinValue)
            return;

        int hottest = (int)Math.Round(hottestC);
        double limit = snap.Find(ComponentKind.Cpu)?.ThrottleLimitC ?? 100;
        string tooltip = sb.ToString();
        _lastIconUpdate = now;

        bool redraw = hottest != _lastShownTemp;
        _lastShownTemp = hottest;
        if (!redraw && tooltip == _lastTooltip)
            return; // nothing changed — skip the dispatcher hop entirely
        _lastTooltip = tooltip;

        // Latest-wins hop at Normal priority: a pegged CPU (stress test, game)
        // starves Background items, which used to freeze the tray at pre-load
        // temperatures; and at most one hop is ever queued.
        _pendingTooltip = tooltip;
        _pendingTemp = hottest;
        _pendingFraction = hottest / limit;
        if (redraw)
            _pendingRedraw = true;
        if (Interlocked.Exchange(ref _uiHopQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                Interlocked.Exchange(ref _uiHopQueued, 0);
                if (_pendingTooltip is not { } tip)
                    return;
                _tray.ToolTipText = tip;
                if (_pendingRedraw)
                {
                    _pendingRedraw = false;
                    _tray.IconSource = RenderIcon(_pendingTemp, _pendingFraction);
                }
            });
        }
    }

    private static string Short(ComponentKind kind) => kind switch
    {
        ComponentKind.Cpu => "CPU",
        ComponentKind.GpuDiscrete => "GPU",
        _ => "SSD",
    };

    private static readonly SolidColorBrush TileBg = Frozen(ThermalPalette.Bg);
    private static readonly Pen TileBorder = FrozenPen(Color.FromRgb(0x42, 0x2F, 0x1F), 1);
    private static readonly Typeface TileFace =
        new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(Frozen(c), w);
        p.Freeze();
        return p;
    }

    /// <summary>Tray glyph: the temperature itself on a soot tile, numeral in the
    /// thermal color, thin heat bar along the bottom. "Δ" until sensors report.</summary>
    private static BitmapSource RenderIcon(int? temp, double fraction = 0)
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Color accent = temp is null ? ThermalPalette.Accent : ThermalPalette.ColorFromFraction(fraction);
            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();

            dc.DrawRoundedRectangle(TileBg, TileBorder, new Rect(0.5, 0.5, size - 1, size - 1), 3, 3);

            string text = temp?.ToString(CultureInfo.InvariantCulture) ?? "Δ";
            double fontSize = text.Length >= 3 ? 14 : text.Length == 2 ? 17 : 18;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                TileFace, fontSize, accentBrush, 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2 - 1.5));

            if (temp is not null)
            {
                double barW = Math.Max(3, (size - 8) * Math.Clamp(fraction, 0, 1));
                dc.DrawRectangle(accentBrush, null, new Rect(4, size - 6, barW, 2.5));
            }
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

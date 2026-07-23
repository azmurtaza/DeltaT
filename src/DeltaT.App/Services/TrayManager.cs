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

/// <summary>The always-there part of DeltaT: a tray icon whose glyph is the CPU
/// temperature (the number people actually watch — an idle NVMe often runs hotter
/// than an idle CPU, so "hottest" used to headline the SSD), falling back to GPU
/// then SSD when the CPU can't be read. Tinted by the thermal palette, heat bar
/// underneath, full component list in the tooltip. Icon redraws are throttled and
/// skipped when nothing changed.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly TaskbarIcon _tray;
    // The DeltaT delta, shown as the large icon on every balloon so a notification carries
    // the brand instead of a generic system glyph. On Windows 10/11 the shell renders a
    // custom-icon balloon as a modern toast card. Null only if the icon can't be loaded,
    // in which case the balloons fall back to the built-in severity glyph.
    private readonly System.Drawing.Icon? _brandIcon;
    private readonly MonitoringService _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly MenuItem _pauseItem;
    private readonly Action _showWindow;
    private readonly Action _showRemarks;
    // Which surface a balloon click should open. A remark toast routes to the Remarks
    // feed (where its advice lives); an app notice (tray hint, "updating") just opens
    // the window. Latest-wins, since only one balloon shows at a time.
    private bool _lastBalloonWasRemark;
    private DateTimeOffset _lastIconUpdate = DateTimeOffset.MinValue;
    private int _lastShownTemp = int.MinValue;
    private string _lastTooltip = "";
    private volatile string? _pendingTooltip;
    private volatile bool _pendingRedraw;
    private int _pendingTemp;
    private double _pendingFraction;
    private int _uiHopQueued;

    public TrayManager(MonitoringService monitor, Action showWindow, Action quit, Action showRemarks)
    {
        _monitor = monitor;
        _dispatcher = Application.Current.Dispatcher;
        _showWindow = showWindow;
        _showRemarks = showRemarks;

        _pauseItem = new MenuItem { Header = "Pause monitoring" };
        _pauseItem.Click += (_, _) =>
        {
            _monitor.IsPaused = !_monitor.IsPaused;
            _pauseItem.Header = _monitor.IsPaused ? "Resume monitoring" : "Pause monitoring";
            if (_monitor.IsPaused)
            {
                // The menu can only be clicked after the constructor finished.
                _tray!.ToolTipText = "Monitoring paused";
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
            ToolTipText = "Warming up",
            ContextMenu = menu,
            IconSource = RenderIcon(null),
        };
        _tray.TrayLeftMouseUp += (_, _) => showWindow();

        // A clicked toast should take the user somewhere useful, not just sit there. A
        // remark toast opens the Remarks feed (its advice); any other notice opens the
        // window. Fires from the balloon itself and from the Action Center entry Windows
        // keeps for it, so the toast stays actionable after it fades.
        _tray.TrayBalloonTipClicked += (_, _) =>
        {
            if (_lastBalloonWasRemark)
                _showRemarks();
            else
                _showWindow();
        };

        try
        {
            System.Windows.Resources.StreamResourceInfo? sri =
                Application.GetResourceStream(new Uri("pack://application:,,,/Assets/deltat.ico"));
            if (sri?.Stream is { } s)
                using (s) _brandIcon = new System.Drawing.Icon(s, 64, 64);
        }
        catch { _brandIcon = null; }

        _monitor.SnapshotCaptured += OnSnapshot;
    }

    // Shows a balloon with the DeltaT logo as the large icon (a modern toast card on
    // Windows 10/11), falling back to the built-in severity glyph if the brand icon
    // couldn't be loaded.
    private void Balloon(string title, string message, BalloonIcon fallback)
    {
        if (_brandIcon is not null)
            _tray.ShowBalloonTip(title, message, _brandIcon, largeIcon: true);
        else
            _tray.ShowBalloonTip(title, message, fallback);
    }

    public void ShowFirstTrayHint()
    {
        _lastBalloonWasRemark = false;
        Balloon("DeltaT is still watching",
            "Closing the window keeps monitoring alive here in the tray. Right-click the icon to quit for real.",
            BalloonIcon.Info);
    }

    public void ShowInfo(string title, string message)
    {
        _lastBalloonWasRemark = false;
        Balloon(title, message, BalloonIcon.Info);
    }

    public void ShowRemarkToast(DeltaT.Core.Remarks.Remark remark)
    {
        _lastBalloonWasRemark = true;
        Balloon(
            remark.Severity == DeltaT.Core.Remarks.RemarkSeverity.Alert ? "Needs attention" : "DeltaT noticed",
            // The balloon is clickable, so invite the click. Windows truncates a long body
            // anyway; the full advice waits in the Remarks feed the click opens.
            remark.Text + "\n\nClick to see what DeltaT suggests.",
            remark.Severity == DeltaT.Core.Remarks.RemarkSeverity.Alert ? BalloonIcon.Error : BalloonIcon.Warning);
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastIconUpdate < TimeSpan.FromSeconds(5))
            return;

        // Glyph priority: CPU first, GPU then SSD only when the CPU is unreadable.
        ComponentReading? shown = null;
        var sb = new StringBuilder("DeltaT ");
        foreach (ComponentKind kind in new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete, ComponentKind.Storage })
        {
            foreach (ComponentReading c in snap.Components)
            {
                if (c.Kind != kind || c.TemperatureC is not { } temp)
                    continue;
                sb.Append(' ').Append(Short(c.Kind)).Append(' ').Append(temp.ToString("0", CultureInfo.InvariantCulture)).Append('°');
                shown ??= c;
            }
        }
        if (shown?.TemperatureC is not { } shownC)
            return;

        int hottest = (int)Math.Round(shownC);
        double limit = shown.ThrottleLimitC ?? (shown.Kind switch
        {
            ComponentKind.GpuDiscrete => 87,
            ComponentKind.Storage => 70,
            _ => 100,
        });
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

    // The real DeltaT delta, drawn as the tray glyph until a temperature is available (and
    // therefore the small attribution icon on a notification, which mirrors the tray icon).
    // Null if the packed icon can't be decoded, in which case the "Δ" tile is used instead.
    private static readonly ImageSource? BrandGlyph = LoadBrandGlyph();

    private static ImageSource? LoadBrandGlyph()
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/Assets/deltat.ico");
            img.DecodePixelWidth = 32;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

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
            // Before any temperature is read, show the real DeltaT logo rather than a "Δ"
            // tile, so the startup tray icon (and the notification's small attribution icon,
            // which mirrors it) carries the brand mark.
            if (temp is null && BrandGlyph is not null)
            {
                dc.DrawImage(BrandGlyph, new Rect(0, 0, size, size));
            }
            else
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
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        _monitor.SnapshotCaptured -= OnSnapshot;
        _brandIcon?.Dispose();
        _tray.Dispose();
    }
}

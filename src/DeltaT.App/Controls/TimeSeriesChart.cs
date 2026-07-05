using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DeltaT.App.Controls;

public readonly record struct ChartPoint(long Ts, double Avg, double Min, double Max);

public readonly record struct ChartMarker(long Ts, string Label, Color Color);

/// <summary>Hand-drawn time-series chart in the DeltaT dialect: min/max band,
/// ember average trace, dashed warm-slate ambient overlay, event markers,
/// hover crosshair with a readout. No charting library — every pixel matches
/// the theme.</summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AmbientPointsProperty = DependencyProperty.Register(
        nameof(AmbientPoints), typeof(IReadOnlyList<ChartPoint>), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<ChartMarker>), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points { get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<ChartPoint>? AmbientPoints { get => (IReadOnlyList<ChartPoint>?)GetValue(AmbientPointsProperty); set => SetValue(AmbientPointsProperty, value); }
    public IReadOnlyList<ChartMarker>? Markers { get => (IReadOnlyList<ChartMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    private const double MarginLeft = 42, MarginRight = 12, MarginTop = 16, MarginBottom = 26;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // Chart chrome never changes color — build it once, frozen, instead of per
    // render (hover redraws happen dozens of times a second while sweeping).
    private static readonly Typeface MonoFace = new(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoSemiFace = new(Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoBoldFace = new(Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly SolidColorBrush LabelBrush = Frozen(ThermalPalette.TextFaint);
    private static readonly SolidColorBrush TextBrush = Frozen(ThermalPalette.Text);
    private static readonly SolidColorBrush TextDimBrush = Frozen(ThermalPalette.TextDim);
    private static readonly SolidColorBrush AccentBrush = Frozen(ThermalPalette.Accent);
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x1C, 0x13, 0x0D), 1);
    private static readonly Pen AxisPen = FrozenPen(ThermalPalette.Stroke, 1);
    private static readonly Pen CrossPen = FrozenPen(Color.FromArgb(55, 0xF2, 0xE8, 0xDC), 1);
    private static readonly SolidColorBrush BandBrush = Frozen(Color.FromArgb(22,
        ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B));
    private static readonly SolidColorBrush BoxBrush = Frozen(Color.FromArgb(246, 0x21, 0x17, 0x11));
    private static readonly Pen BoxPen = FrozenPen(ThermalPalette.Stroke, 1);
    private static readonly Pen AmbientPen = MakeAmbientPen();
    private static readonly Pen LinePen = MakeLinePen();
    private static readonly Dictionary<Color, (Pen Pen, SolidColorBrush Brush)> MarkerCache = new();

    private Point? _hover;

    public TimeSeriesChart()
    {
        MouseMove += (_, e) =>
        {
            Point p = e.GetPosition(this);
            // Sub-pixel jitter doesn't move the crosshair — skip those redraws.
            if (_hover is { } old && Math.Abs(old.X - p.X) < 1 && Math.Abs(old.Y - p.Y) < 1)
                return;
            _hover = p;
            InvalidateVisual();
        };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        ClipToBounds = true;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(Frozen(c), width);
        p.Freeze();
        return p;
    }

    private static Pen MakeAmbientPen()
    {
        var p = new Pen(Frozen(Color.FromArgb(170, 0x6E, 0x5C, 0x4B)), 1.2)
        { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) };
        p.Freeze();
        return p;
    }

    private static Pen MakeLinePen()
    {
        var p = new Pen(Frozen(ThermalPalette.Accent), 1.6) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static (Pen Pen, SolidColorBrush Brush) MarkerStyle(Color c)
    {
        if (!MarkerCache.TryGetValue(c, out var style))
        {
            var pen = new Pen(Frozen(Color.FromArgb(120, c.R, c.G, c.B)), 1)
            { DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0) };
            pen.Freeze();
            style = (pen, Frozen(c));
            MarkerCache[c] = style;
        }
        return style;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        // Transparent hit-test surface so hover works over empty regions.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        IReadOnlyList<ChartPoint>? points = Points;
        if (points is null || points.Count < 2 || w < 120 || h < 80)
        {
            DrawEmptyState(dc, "NO SAMPLES IN THIS RANGE", w, h);
            return;
        }

        double plotW = w - MarginLeft - MarginRight;
        double plotH = h - MarginTop - MarginBottom;
        long t0 = points[0].Ts, t1 = points[^1].Ts;
        if (t1 <= t0) return;

        double yMin = points.Min(p => p.Min);
        double yMax = points.Max(p => p.Max);
        if (AmbientPoints is { Count: > 0 } amb0)
        {
            yMin = Math.Min(yMin, amb0.Min(p => p.Avg));
            yMax = Math.Max(yMax, amb0.Max(p => p.Avg));
        }
        yMin = Math.Floor((yMin - 3) / 5) * 5;
        yMax = Math.Ceiling((yMax + 3) / 5) * 5;
        double ySpan = Math.Max(5, yMax - yMin);

        double X(long ts) => MarginLeft + (ts - t0) / (double)(t1 - t0) * plotW;
        double Y(double v) => MarginTop + (1 - (v - yMin) / ySpan) * plotH;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Grid + y labels.
        double step = ySpan <= 25 ? 5 : ySpan <= 60 ? 10 : 20;
        for (double v = yMin; v <= yMax + 0.01; v += step)
        {
            double y = Y(v);
            dc.DrawLine(GridPen, new Point(MarginLeft, y), new Point(w - MarginRight, y));
            var txt = new FormattedText($"{v:0}°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 9.5, LabelBrush, dip);
            dc.DrawText(txt, new Point(MarginLeft - txt.Width - 7, y - txt.Height / 2));
        }

        // Bottom axis + x labels with small tick marks.
        double axisY = h - MarginBottom;
        dc.DrawLine(AxisPen, new Point(MarginLeft, axisY), new Point(w - MarginRight, axisY));
        TimeSpan span = TimeSpan.FromSeconds(t1 - t0);
        string fmt = span.TotalHours <= 26 ? "HH:mm" : span.TotalDays <= 8 ? "ddd HH:mm" : "MMM d";
        int xTicks = Math.Max(3, (int)(plotW / 130));
        for (int i = 0; i <= xTicks; i++)
        {
            long ts = t0 + (long)((t1 - t0) * (i / (double)xTicks));
            double tickX = X(ts);
            dc.DrawLine(AxisPen, new Point(tickX, axisY), new Point(tickX, axisY + 3));
            string label = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var txt = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 9.5, LabelBrush, dip);
            double x = Math.Clamp(tickX - txt.Width / 2, 0, w - txt.Width);
            dc.DrawText(txt, new Point(x, axisY + 7));
        }

        // Min/max band.
        var band = new StreamGeometry();
        using (StreamGeometryContext ctx = band.Open())
        {
            ctx.BeginFigure(new Point(X(points[0].Ts), Y(points[0].Max)), true, true);
            foreach (ChartPoint p in points.Skip(1))
                ctx.LineTo(new Point(X(p.Ts), Y(p.Max)), false, false);
            for (int i = points.Count - 1; i >= 0; i--)
                ctx.LineTo(new Point(X(points[i].Ts), Y(points[i].Min)), false, false);
        }
        band.Freeze();
        dc.DrawGeometry(BandBrush, null, band);

        // Ambient overlay (dashed slate).
        if (AmbientPoints is { Count: > 1 } amb)
            DrawLine(dc, AmbientPen, amb.Select(p => new Point(X(p.Ts), Y(p.Avg))));

        // Average trace — the one loud element on this screen.
        DrawLine(dc, LinePen, points.Select(p => new Point(X(p.Ts), Y(p.Avg))));

        // Event markers.
        if (Markers is { Count: > 0 } markers)
        {
            foreach (ChartMarker m in markers)
            {
                if (m.Ts < t0 || m.Ts > t1) continue;
                double x = X(m.Ts);
                (Pen pen, SolidColorBrush brush) = MarkerStyle(m.Color);
                dc.DrawLine(pen, new Point(x, MarginTop), new Point(x, axisY));
                var txt = new FormattedText(m.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    MonoBoldFace, 9, brush, dip);
                dc.DrawText(txt, new Point(Math.Clamp(x - txt.Width / 2, 0, w - txt.Width), MarginTop - 13));
            }
        }

        // Hover crosshair + readout.
        if (_hover is { } hover && hover.X >= MarginLeft && hover.X <= w - MarginRight)
        {
            long ts = t0 + (long)((hover.X - MarginLeft) / plotW * (t1 - t0));
            ChartPoint nearest = points.MinBy(p => Math.Abs(p.Ts - ts));
            double? ambientAt = AmbientPoints is { Count: > 0 } a2
                ? a2.MinBy(p => Math.Abs(p.Ts - ts)).Avg : null;

            double x = X(nearest.Ts);
            dc.DrawLine(CrossPen, new Point(x, MarginTop), new Point(x, axisY));
            dc.DrawEllipse(AccentBrush, null, new Point(x, Y(nearest.Avg)), 2.5, 2.5);

            string when = DateTimeOffset.FromUnixTimeSeconds(nearest.Ts).ToLocalTime()
                .ToString(span.TotalHours <= 26 ? "HH:mm" : "MMM d HH:mm", CultureInfo.InvariantCulture);
            string line1 = $"{when}   {nearest.Avg:0.#}°  ({nearest.Min:0}–{nearest.Max:0}°)";
            string line2 = ambientAt is { } av ? $"outside {av:0.#}°   Δ {nearest.Avg - av:+0.#;-0.#}°" : "";
            var t1f = new FormattedText(line1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoSemiFace, 11, TextBrush, dip);
            var t2f = new FormattedText(line2, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 10.5, TextDimBrush, dip);

            double boxW = Math.Max(t1f.Width, t2f.Width) + 20;
            double boxH = 14 + t1f.Height + (line2.Length > 0 ? t2f.Height + 3 : 0);
            double bx = Math.Clamp(x + 12, MarginLeft, w - boxW - 4);
            double by = MarginTop + 6;
            dc.DrawRoundedRectangle(BoxBrush, BoxPen, new Rect(bx, by, boxW, boxH), 2, 2);
            dc.DrawText(t1f, new Point(bx + 10, by + 7));
            if (line2.Length > 0)
                dc.DrawText(t2f, new Point(bx + 10, by + 10 + t1f.Height));
        }
    }

    private static void DrawLine(DrawingContext dc, Pen pen, IEnumerable<Point> pts)
    {
        var list = pts.ToList();
        if (list.Count < 2) return;
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(list[0], false, false);
            ctx.PolyLineTo(list.Skip(1).ToList(), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>Empty state as an instrument would print it: tracked caps on
    /// the centerline, flanked by short hairline dashes.</summary>
    private void DrawEmptyState(DrawingContext dc, string text, double w, double h)
    {
        string tracked = string.Join(((char)0x200A).ToString(), text.ToCharArray());
        var txt = new FormattedText(tracked, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, 10.5, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double tx = (w - txt.Width) / 2, ty = (h - txt.Height) / 2;
        dc.DrawText(txt, new Point(tx, ty));

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(110,
            ThermalPalette.TextFaint.R, ThermalPalette.TextFaint.G, ThermalPalette.TextFaint.B)), 1);
        pen.Freeze();
        double cy = ty + txt.Height / 2 + 0.5;
        dc.DrawLine(pen, new Point(tx - 40, cy), new Point(tx - 14, cy));
        dc.DrawLine(pen, new Point(tx + txt.Width + 14, cy), new Point(tx + txt.Width + 40, cy));
    }
}

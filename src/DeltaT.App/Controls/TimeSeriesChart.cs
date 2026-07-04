using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DeltaT.App.Controls;

public readonly record struct ChartPoint(long Ts, double Avg, double Min, double Max);

public readonly record struct ChartMarker(long Ts, string Label, Color Color);

/// <summary>Hand-drawn time-series chart in the DeltaT dialect: min/max band,
/// average line, dashed ambient overlay, event markers, hover crosshair with a
/// readout. No charting library — every pixel matches the theme.</summary>
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

    private const double MarginLeft = 42, MarginRight = 12, MarginTop = 14, MarginBottom = 26;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private Point? _hover;

    public TimeSeriesChart()
    {
        MouseMove += (_, e) => { _hover = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        // Transparent hit-test surface so hover works over empty regions.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        IReadOnlyList<ChartPoint>? points = Points;
        if (points is null || points.Count < 2 || w < 120 || h < 80)
        {
            DrawCentered(dc, "no data in this range yet", w, h);
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
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x1B, 0x17)), 1);
        gridPen.Freeze();
        var labelBrush = new SolidColorBrush(ThermalPalette.TextFaint);
        for (double v = yMin; v <= yMax + 0.01; v += step)
        {
            double y = Y(v);
            dc.DrawLine(gridPen, new Point(MarginLeft, y), new Point(w - MarginRight, y));
            var txt = new FormattedText($"{v:0}°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9.5, labelBrush, dip);
            dc.DrawText(txt, new Point(MarginLeft - txt.Width - 7, y - txt.Height / 2));
        }

        // X labels.
        TimeSpan span = TimeSpan.FromSeconds(t1 - t0);
        string fmt = span.TotalHours <= 26 ? "HH:mm" : span.TotalDays <= 8 ? "ddd HH:mm" : "MMM d";
        int xTicks = Math.Max(3, (int)(plotW / 130));
        for (int i = 0; i <= xTicks; i++)
        {
            long ts = t0 + (long)((t1 - t0) * (i / (double)xTicks));
            string label = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var txt = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9.5, labelBrush, dip);
            double x = Math.Clamp(X(ts) - txt.Width / 2, 0, w - txt.Width);
            dc.DrawText(txt, new Point(x, h - MarginBottom + 8));
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
        var bandBrush = new SolidColorBrush(Color.FromArgb(20,
            ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B));
        bandBrush.Freeze();
        dc.DrawGeometry(bandBrush, null, band);

        // Ambient overlay (dashed).
        if (AmbientPoints is { Count: > 1 } amb)
        {
            var ambPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 0x6A, 0x61, 0x52)), 1.2)
            { DashStyle = new DashStyle(new[] { 4.0, 4.0 }, 0) };
            ambPen.Freeze();
            DrawLine(dc, ambPen, amb.Select(p => new Point(X(p.Ts), Y(p.Avg))));
        }

        // Average line.
        var linePen = new Pen(new SolidColorBrush(ThermalPalette.Accent), 1.6) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        DrawLine(dc, linePen, points.Select(p => new Point(X(p.Ts), Y(p.Avg))));

        // Event markers.
        if (Markers is { Count: > 0 } markers)
        {
            foreach (ChartMarker m in markers)
            {
                if (m.Ts < t0 || m.Ts > t1) continue;
                double x = X(m.Ts);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(140, m.Color.R, m.Color.G, m.Color.B)), 1)
                { DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0) };
                pen.Freeze();
                dc.DrawLine(pen, new Point(x, MarginTop), new Point(x, h - MarginBottom));
                var txt = new FormattedText(m.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 9,
                    new SolidColorBrush(m.Color), dip);
                dc.DrawText(txt, new Point(Math.Clamp(x - txt.Width / 2, 0, w - txt.Width), MarginTop - 11));
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
            var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 0xE9, 0xE1, 0xD2)), 1);
            crossPen.Freeze();
            dc.DrawLine(crossPen, new Point(x, MarginTop), new Point(x, h - MarginBottom));
            dc.DrawEllipse(new SolidColorBrush(ThermalPalette.Accent), null, new Point(x, Y(nearest.Avg)), 2.5, 2.5);

            string when = DateTimeOffset.FromUnixTimeSeconds(nearest.Ts).ToLocalTime()
                .ToString(span.TotalHours <= 26 ? "HH:mm" : "MMM d HH:mm", CultureInfo.InvariantCulture);
            string line1 = $"{when}   {nearest.Avg:0.#}°  ({nearest.Min:0}–{nearest.Max:0}°)";
            string line2 = ambientAt is { } av ? $"outside {av:0.#}°   Δ {nearest.Avg - av:+0.#;-0.#}°" : "";
            var t1f = new FormattedText(line1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11,
                new SolidColorBrush(ThermalPalette.Text), dip);
            var t2f = new FormattedText(line2, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10.5,
                new SolidColorBrush(ThermalPalette.TextDim), dip);

            double boxW = Math.Max(t1f.Width, t2f.Width) + 20;
            double boxH = 14 + t1f.Height + (line2.Length > 0 ? t2f.Height + 3 : 0);
            double bx = Math.Clamp(x + 12, MarginLeft, w - boxW - 4);
            double by = MarginTop + 6;
            var boxBrush = new SolidColorBrush(Color.FromArgb(244, 0x21, 0x1D, 0x18));
            var boxPen = new Pen(new SolidColorBrush(ThermalPalette.Stroke), 1);
            dc.DrawRoundedRectangle(boxBrush, boxPen, new Rect(bx, by, boxW, boxH), 2, 2);
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

    private void DrawCentered(DrawingContext dc, string text, double w, double h)
    {
        var txt = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12,
            new SolidColorBrush(ThermalPalette.TextFaint), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(txt, new Point((w - txt.Width) / 2, (h - txt.Height) / 2));
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DeltaT.App.Controls;

public readonly record struct ChartPoint(long Ts, double Avg, double Min, double Max);

public readonly record struct ChartMarker(long Ts, string Label, Color Color);

/// <summary>Hand-drawn time-series chart in the DeltaT dialect: min/max band,
/// ember average trace (glow + smoothed), dashed warm-slate ambient overlay, event
/// markers, hover crosshair with a readout. No charting library — every pixel
/// matches the theme.
///
/// Interactive: mouse-wheel zooms about the cursor, left-drag pans, double-click
/// resets to the full range. The visible window and the Y axis both ease toward
/// their targets (a CompositionTarget frame loop that stops once settled), and the
/// Y axis auto-scales to whatever is on screen so a zoomed-in stretch fills the
/// plot. Long ranges are downsampled to keep the geometry cheap.</summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPointsChanged));

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

    // Interaction tunables.
    private const double ZoomStep = 1.22;          // one wheel notch out; in is 1/step
    private const double MinSpanSeconds = 120;      // never zoom tighter than two minutes
    private const double Ease = 0.28;               // view/axis glide per frame toward target

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // Chart chrome never changes color — build it once, frozen, instead of per
    // render (hover/animation redraws happen dozens of times a second).
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
    private static readonly SolidColorBrush BandBrush = Frozen(Color.FromArgb(15,
        ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B));
    private static readonly SolidColorBrush BoxBrush = Frozen(Color.FromArgb(246, 0x21, 0x17, 0x11));
    private static readonly Pen BoxPen = FrozenPen(ThermalPalette.Stroke, 1);
    private static readonly Pen AmbientPen = MakeAmbientPen();
    private static readonly Pen LinePen = MakeLinePen();
    private static readonly Pen GlowPen = MakeGlowPen();
    private static readonly Dictionary<Color, (Pen Pen, SolidColorBrush Brush)> MarkerCache = new();

    private Point? _hover;

    // Visible window in unix seconds; NaN until first laid out. The view eases
    // toward the target; a wheel/drag/reset just moves the target.
    private double _viewStart = double.NaN, _viewEnd = double.NaN;
    private double _targetStart = double.NaN, _targetEnd = double.NaN;
    private double _curYMin = double.NaN, _curYMax = double.NaN;
    private double _targetYMin = double.NaN, _targetYMax = double.NaN;

    private bool _dragging;
    private double _dragAnchorX, _dragStartView, _dragEndView;
    private bool _ticking;

    public TimeSeriesChart()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    private double PlotWidth => Math.Max(1, ActualWidth - MarginLeft - MarginRight);

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

    private static Pen MakeGlowPen()
    {
        Color a = ThermalPalette.Accent;
        var p = new Pen(Frozen(Color.FromArgb(40, a.R, a.G, a.B)), 5) { LineJoin = PenLineJoin.Round };
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

    // New data set: forget the old view/zoom so the fresh series fits the frame.
    private static void OnPointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TimeSeriesChart)d;
        chart._viewStart = chart._viewEnd = chart._targetStart = chart._targetEnd = double.NaN;
        chart._curYMin = chart._curYMax = chart._targetYMin = chart._targetYMax = double.NaN;
        chart._hover = null;
    }

    private bool TryDataRange(out double a, out double b)
    {
        IReadOnlyList<ChartPoint>? points = Points;
        if (points is { Count: > 1 })
        {
            a = points[0].Ts;
            b = points[^1].Ts;
            return b > a;
        }
        a = b = 0;
        return false;
    }

    /// <summary>Where the view is heading: the pending target, else the live view,
    /// else the full data range.</summary>
    private (double Start, double End) AimView()
    {
        if (!double.IsNaN(_targetStart) && _targetEnd > _targetStart)
            return (_targetStart, _targetEnd);
        if (!double.IsNaN(_viewStart) && _viewEnd > _viewStart)
            return (_viewStart, _viewEnd);
        TryDataRange(out double a, out double b);
        return (a, b);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!TryDataRange(out double a, out double b))
            return;

        (double start, double end) = AimView();
        double span = end - start;
        double fullSpan = b - a;

        // Zoom about the point under the cursor so it stays put.
        double frac = Math.Clamp((e.GetPosition(this).X - MarginLeft) / PlotWidth, 0, 1);
        double anchor = start + frac * span;
        double factor = e.Delta > 0 ? 1 / ZoomStep : ZoomStep;
        double newSpan = Math.Clamp(span * factor, Math.Max(MinSpanSeconds, fullSpan / 1000), fullSpan);

        double ns = anchor - frac * newSpan;
        double ne = ns + newSpan;
        if (ns < a) { ns = a; ne = a + newSpan; }
        if (ne > b) { ne = b; ns = b - newSpan; }

        SetTarget(ns, ne);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (TryDataRange(out double a, out double b))
                SetTarget(a, b); // double-click resets to the full range
            e.Handled = true;
            return;
        }

        if (!TryDataRange(out _, out _))
            return;

        (double start, double end) = AimView();
        _viewStart = _targetStart = start;
        _viewEnd = _targetEnd = end;
        _dragging = true;
        _dragAnchorX = e.GetPosition(this).X;
        _dragStartView = start;
        _dragEndView = end;
        _hover = null;
        CaptureMouse();
        Cursor = Cursors.ScrollWE;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        if (_dragging)
        {
            double span = _dragEndView - _dragStartView;
            double shift = -(p.X - _dragAnchorX) / PlotWidth * span;
            TryDataRange(out double a, out double b);
            double ns = _dragStartView + shift;
            double ne = _dragEndView + shift;
            if (ns < a) { ns = a; ne = a + span; }
            if (ne > b) { ne = b; ns = b - span; }
            _viewStart = _targetStart = ns;
            _viewEnd = _targetEnd = ne;
            StartTicking();
            return;
        }

        // Sub-pixel jitter doesn't move the crosshair — skip those redraws.
        if (_hover is { } old && Math.Abs(old.X - p.X) < 1 && Math.Abs(old.Y - p.Y) < 1)
            return;
        _hover = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_dragging)
            return;
        _hover = null;
        InvalidateVisual();
    }

    private void SetTarget(double start, double end)
    {
        _targetStart = start;
        _targetEnd = end;
        if (double.IsNaN(_viewStart))
            TryDataRange(out _viewStart, out _viewEnd);
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking)
        {
            InvalidateVisual();
            return;
        }
        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
        InvalidateVisual();
    }

    // Ease the live view/axis toward their targets; unhook the frame loop once
    // both have settled (and we're not mid-drag) so idle charts cost nothing.
    private void OnFrame(object? sender, EventArgs e)
    {
        bool moving = false;

        if (!double.IsNaN(_targetStart) && !double.IsNaN(_viewStart))
        {
            _viewStart += (_targetStart - _viewStart) * Ease;
            _viewEnd += (_targetEnd - _viewEnd) * Ease;
            double eps = Math.Max(0.5, (_targetEnd - _targetStart) * 0.0015);
            if (Math.Abs(_targetStart - _viewStart) > eps || Math.Abs(_targetEnd - _viewEnd) > eps)
                moving = true;
            else { _viewStart = _targetStart; _viewEnd = _targetEnd; }
        }

        if (!double.IsNaN(_targetYMin) && !double.IsNaN(_curYMin))
        {
            _curYMin += (_targetYMin - _curYMin) * Ease;
            _curYMax += (_targetYMax - _curYMax) * Ease;
            if (Math.Abs(_targetYMin - _curYMin) > 0.05 || Math.Abs(_targetYMax - _curYMax) > 0.05)
                moving = true;
            else { _curYMin = _targetYMin; _curYMax = _targetYMax; }
        }

        InvalidateVisual();
        if (!moving && !_dragging)
        {
            CompositionTarget.Rendering -= OnFrame;
            _ticking = false;
        }
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

        if (double.IsNaN(_viewStart) || _viewEnd <= _viewStart)
            TryDataRange(out _viewStart, out _viewEnd);
        double t0 = _viewStart, t1 = _viewEnd;
        if (t1 <= t0) return;

        // Visible index window (binary search), padded by one on each side.
        int lo = Math.Max(0, LowerBound(points, (long)Math.Floor(t0)) - 1);
        int hi = Math.Min(points.Count - 1, LowerBound(points, (long)Math.Ceiling(t1)));
        if (hi <= lo) { lo = 0; hi = points.Count - 1; }

        // Y axis auto-scales to what's visible, eased frame to frame.
        (double vyMin, double vyMax) = VisibleYRange(points, lo, hi, t0, t1);
        _targetYMin = vyMin;
        _targetYMax = vyMax;
        if (double.IsNaN(_curYMin)) { _curYMin = vyMin; _curYMax = vyMax; }
        double yMin = _curYMin, yMax = _curYMax;
        double ySpan = Math.Max(5, yMax - yMin);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double X(double ts) => MarginLeft + (ts - t0) / (t1 - t0) * plotW;
        double Y(double v) => MarginTop + (1 - (v - yMin) / ySpan) * plotH;

        // Grid + y labels (clipped to the plot area).
        double step = ySpan <= 25 ? 5 : ySpan <= 60 ? 10 : 20;
        for (double v = Math.Floor(yMin / step) * step; v <= yMax + 0.01; v += step)
        {
            double y = Y(v);
            if (y < MarginTop || y > h - MarginBottom + 1) continue;
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
            double ts = t0 + (t1 - t0) * (i / (double)xTicks);
            double tickX = X(ts);
            dc.DrawLine(AxisPen, new Point(tickX, axisY), new Point(tickX, axisY + 3));
            string label = DateTimeOffset.FromUnixTimeSeconds((long)ts).ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var txt = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 9.5, LabelBrush, dip);
            double x = Math.Clamp(tickX - txt.Width / 2, 0, w - txt.Width);
            dc.DrawText(txt, new Point(x, axisY + 7));
        }

        // Downsample the visible slice so wide ranges stay cheap to draw.
        int maxPoints = Math.Max(2, (int)(plotW / 2.5));
        IReadOnlyList<ChartPoint> vis = Downsample(points, lo, hi, maxPoints);

        // Min/max band.
        var band = new StreamGeometry();
        using (StreamGeometryContext ctx = band.Open())
        {
            ctx.BeginFigure(new Point(X(vis[0].Ts), Y(vis[0].Max)), true, true);
            foreach (ChartPoint p in vis.Skip(1))
                ctx.LineTo(new Point(X(p.Ts), Y(p.Max)), false, false);
            for (int i = vis.Count - 1; i >= 0; i--)
                ctx.LineTo(new Point(X(vis[i].Ts), Y(vis[i].Min)), false, false);
        }
        band.Freeze();
        dc.DrawGeometry(BandBrush, null, band);

        // Ambient overlay (dashed slate), same visible window.
        if (AmbientPoints is { Count: > 1 } amb)
        {
            int alo = Math.Max(0, LowerBound(amb, (long)Math.Floor(t0)) - 1);
            int ahi = Math.Min(amb.Count - 1, LowerBound(amb, (long)Math.Ceiling(t1)));
            if (ahi > alo)
                DrawLine(dc, AmbientPen, Downsample(amb, alo, ahi, maxPoints).Select(p => new Point(X(p.Ts), Y(p.Avg))));
        }

        // Average trace (3-point smoothed) with a soft glow beneath — the one loud element.
        double[] smooth = SmoothAvg(vis);
        var line = new Point[vis.Count];
        for (int i = 0; i < vis.Count; i++)
            line[i] = new Point(X(vis[i].Ts), Y(smooth[i]));
        DrawLine(dc, GlowPen, line);
        DrawLine(dc, LinePen, line);
        dc.DrawEllipse(AccentBrush, null, line[^1], 2.6, 2.6);

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

        // No crosshair while panning.
        if (_dragging || _hover is not { } hover || hover.X < MarginLeft || hover.X > w - MarginRight)
            return;

        double hts = t0 + (hover.X - MarginLeft) / plotW * (t1 - t0);
        ChartPoint nearest = NearestInRange(points, lo, hi, hts);
        double? ambientAt = AmbientPoints is { Count: > 0 } a2
            ? NearestInRange(a2, 0, a2.Count - 1, hts).Avg : null;

        double cx = X(nearest.Ts);
        dc.DrawLine(CrossPen, new Point(cx, MarginTop), new Point(cx, axisY));
        dc.DrawEllipse(AccentBrush, null, new Point(cx, Y(nearest.Avg)), 2.5, 2.5);

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
        double bx = Math.Clamp(cx + 12, MarginLeft, w - boxW - 4);
        double by = MarginTop + 6;
        dc.DrawRoundedRectangle(BoxBrush, BoxPen, new Rect(bx, by, boxW, boxH), 2, 2);
        dc.DrawText(t1f, new Point(bx + 10, by + 7));
        if (line2.Length > 0)
            dc.DrawText(t2f, new Point(bx + 10, by + 10 + t1f.Height));
    }

    /// <summary>First index whose timestamp is >= <paramref name="ts"/> (points are sorted).</summary>
    private static int LowerBound(IReadOnlyList<ChartPoint> pts, long ts)
    {
        int lo = 0, hi = pts.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (pts[mid].Ts < ts) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static ChartPoint NearestInRange(IReadOnlyList<ChartPoint> pts, int lo, int hi, double ts)
    {
        ChartPoint best = pts[lo];
        double bestD = Math.Abs(best.Ts - ts);
        for (int i = lo + 1; i <= hi; i++)
        {
            double d = Math.Abs(pts[i].Ts - ts);
            if (d < bestD) { bestD = d; best = pts[i]; }
        }
        return best;
    }

    /// <summary>Min/max across the visible slice (plus any ambient points in range),
    /// snapped out to 5° so the axis labels stay tidy.</summary>
    private (double Min, double Max) VisibleYRange(IReadOnlyList<ChartPoint> pts, int lo, int hi, double t0, double t1)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = lo; i <= hi; i++)
        {
            if (pts[i].Min < min) min = pts[i].Min;
            if (pts[i].Max > max) max = pts[i].Max;
        }
        if (AmbientPoints is { Count: > 0 } amb)
        {
            foreach (ChartPoint a in amb)
            {
                if (a.Ts < t0 || a.Ts > t1) continue;
                if (a.Avg < min) min = a.Avg;
                if (a.Avg > max) max = a.Avg;
            }
        }
        if (min > max) { min = 20; max = 60; }
        min = Math.Floor((min - 3) / 5) * 5;
        max = Math.Ceiling((max + 3) / 5) * 5;
        return (min, Math.Max(min + 5, max));
    }

    /// <summary>Bucket a long slice down to <paramref name="maxPoints"/> columns, keeping
    /// min/max envelope and mean per bucket so spikes survive downsampling.</summary>
    private static IReadOnlyList<ChartPoint> Downsample(IReadOnlyList<ChartPoint> src, int lo, int hi, int maxPoints)
    {
        int n = hi - lo + 1;
        if (maxPoints < 2 || n <= maxPoints)
        {
            var copy = new ChartPoint[n];
            for (int i = 0; i < n; i++) copy[i] = src[lo + i];
            return copy;
        }

        var outp = new List<ChartPoint>(maxPoints);
        double bucket = (double)n / maxPoints;
        for (int j = 0; j < maxPoints; j++)
        {
            int from = lo + (int)(j * bucket);
            int to = Math.Min(hi + 1, lo + Math.Max((int)(j * bucket) + 1, (int)((j + 1) * bucket)));
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            for (int k = from; k < to; k++)
            {
                if (src[k].Min < min) min = src[k].Min;
                if (src[k].Max > max) max = src[k].Max;
                sum += src[k].Avg;
            }
            outp.Add(new ChartPoint(src[from].Ts, sum / (to - from), min, max));
        }
        return outp;
    }

    private static double[] SmoothAvg(IReadOnlyList<ChartPoint> pts)
    {
        var outp = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            double sum = pts[i].Avg;
            int n = 1;
            if (i > 0) { sum += pts[i - 1].Avg; n++; }
            if (i < pts.Count - 1) { sum += pts[i + 1].Avg; n++; }
            outp[i] = sum / n;
        }
        return outp;
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

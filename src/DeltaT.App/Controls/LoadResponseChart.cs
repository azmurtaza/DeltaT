using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>One category on the load-response overlay: a load bucket and its
/// weather-corrected rise (ΔT over ambient) in two periods. Either side may be null when
/// that period lacked comparable data for the bucket.</summary>
public readonly record struct LoadResponseDatum(string Label, double? Earlier, double? Recent);

/// <summary>The season-on-season comparison graph: two load-response curves overlaid on a
/// shared axis (x = load bucket idle→full, y = rise over ambient). The recent period is
/// the ember subject line (solid, glowing, filled dots with value labels); the earlier
/// period is a neutral gray reference (dashed, hollow dots). Where the ember line pulls
/// above the reference under load, that gap is the drift. Hand-drawn in the DeltaT dialect to
/// match the time-series chart — no charting library.</summary>
public sealed class LoadResponseChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<LoadResponseDatum>), typeof(LoadResponseChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RecentLabelProperty = DependencyProperty.Register(
        nameof(RecentLabel), typeof(string), typeof(LoadResponseChart),
        new FrameworkPropertyMetadata("RECENT", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EarlierLabelProperty = DependencyProperty.Register(
        nameof(EarlierLabel), typeof(string), typeof(LoadResponseChart),
        new FrameworkPropertyMetadata("EARLIER", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<LoadResponseDatum>? Series
    {
        get => (IReadOnlyList<LoadResponseDatum>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string RecentLabel { get => (string)GetValue(RecentLabelProperty); set => SetValue(RecentLabelProperty, value); }
    public string EarlierLabel { get => (string)GetValue(EarlierLabelProperty); set => SetValue(EarlierLabelProperty, value); }

    private const double MarginLeft = 44, MarginRight = 16, MarginTop = 44, MarginBottom = 30;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Typeface MonoFace = new(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoSemiFace = new(Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly SolidColorBrush LabelBrush = Frozen(ThermalPalette.TextFaint);
    private static readonly SolidColorBrush TextDimBrush = Frozen(ThermalPalette.TextDim);
    private static readonly SolidColorBrush AccentBrush = Frozen(ThermalPalette.Accent);
    // The earlier period is a reference, not a reading: it gets the neutral warm gray,
    // not the thermal ramp's steel (which means "running cool" everywhere else).
    private static readonly SolidColorBrush RefBrush = Frozen(ThermalPalette.TextDim);
    // Matches the Module plate the chart sits on, so a hollow dot reads as a ring
    // punched in the line, not as a darker hole in the panel.
    private static readonly SolidColorBrush PanelBrush = Frozen(ThermalPalette.Panel);
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x1C, 0x13, 0x0D), 1);
    private static readonly Pen AxisPen = FrozenPen(ThermalPalette.Stroke, 1);
    private static readonly Pen RecentPen = FrozenPen(ThermalPalette.Accent, 1.8, PenLineJoin.Round);
    private static readonly Pen RefPen = MakeRefPen();
    // The reference dots are stroked solid: the dash pattern belongs to the line, and
    // wrapping it around a 3px circle just chews the ring into three ragged arcs.
    private static readonly Pen RefDotPen = FrozenPen(ThermalPalette.TextDim, 1.4, PenLineJoin.Round);
    private static readonly Pen GlowPen = MakeGlowPen();

    public LoadResponseChart()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        IReadOnlyList<LoadResponseDatum>? series = Series;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (series is null || series.Count < 2 || w < 120 || h < 90
            || !series.Any(d => d.Earlier is not null || d.Recent is not null))
        {
            DrawEmptyState(dc, "NOT ENOUGH DATA TO COMPARE", w, h, dip);
            return;
        }

        double plotW = w - MarginLeft - MarginRight;
        double plotH = h - MarginTop - MarginBottom;
        int n = series.Count;

        (double yMin, double yMax) = YRange(series);
        double ySpan = Math.Max(5, yMax - yMin);

        double X(int i) => MarginLeft + (n == 1 ? 0.5 : i / (double)(n - 1)) * plotW;
        double Y(double v) => MarginTop + (1 - (v - yMin) / ySpan) * plotH;

        // Horizontal grid + y labels.
        double step = ySpan <= 25 ? 5 : ySpan <= 60 ? 10 : 20;
        for (double v = Math.Ceiling(yMin / step) * step; v <= yMax + 0.01; v += step)
        {
            double y = Y(v);
            if (y < MarginTop - 1 || y > h - MarginBottom + 1) continue;
            dc.DrawLine(GridPen, new Point(MarginLeft, y), new Point(w - MarginRight, y));
            FormattedText txt = Label($"{v:0}°", MonoFace, 9.5, LabelBrush, dip);
            dc.DrawText(txt, new Point(MarginLeft - txt.Width - 8, y - txt.Height / 2));
        }

        // Bottom axis + category labels.
        double axisY = h - MarginBottom;
        dc.DrawLine(AxisPen, new Point(MarginLeft, axisY), new Point(w - MarginRight, axisY));
        for (int i = 0; i < n; i++)
        {
            double x = X(i);
            dc.DrawLine(AxisPen, new Point(x, axisY), new Point(x, axisY + 3));
            FormattedText txt = Label(series[i].Label.ToUpperInvariant(), MonoFace, 9, LabelBrush, dip);
            double tx = Math.Clamp(x - txt.Width / 2, 0, w - txt.Width);
            dc.DrawText(txt, new Point(tx, axisY + 7));
        }

        // Earlier reference curve first (gray, dashed, hollow dots), so the ember
        // subject line reads on top of it.
        DrawCurve(dc, series, i => series[i].Earlier, X, Y, RefPen, null, RefBrush, hollow: true, dip: dip, showValues: false, dotPen: RefDotPen);
        DrawCurve(dc, series, i => series[i].Recent, X, Y, RecentPen, GlowPen, AccentBrush, hollow: false, dip: dip, showValues: true);

        DrawLegend(dc, dip);
    }

    /// <summary>Draws one connected curve, breaking the line across any null gap so a
    /// bucket with no data leaves a hole rather than a false straight line through it.</summary>
    private void DrawCurve(DrawingContext dc, IReadOnlyList<LoadResponseDatum> series, Func<int, double?> value,
        Func<int, double> X, Func<double, double> Y, Pen pen, Pen? glow, SolidColorBrush dot,
        bool hollow, double dip, bool showValues, Pen? dotPen = null)
    {
        int n = series.Count;
        Point? prev = null;
        for (int i = 0; i < n; i++)
        {
            if (value(i) is not { } v)
            {
                prev = null; // gap: don't bridge across the missing bucket
                continue;
            }
            var p = new Point(X(i), Y(v));
            if (prev is { } q)
            {
                if (glow is not null) dc.DrawLine(glow, q, p);
                dc.DrawLine(pen, q, p);
            }
            prev = p;
        }

        // Dots (and value labels) on a second pass so they sit above every line segment.
        for (int i = 0; i < n; i++)
        {
            if (value(i) is not { } v) continue;
            var p = new Point(X(i), Y(v));
            if (hollow)
            {
                dc.DrawEllipse(PanelBrush, dotPen ?? pen, p, 3.2, 3.2);
            }
            else
            {
                dc.DrawEllipse(dot, null, p, 3.2, 3.2);
                if (showValues)
                {
                    FormattedText txt = Label($"{v:0.#}°", MonoSemiFace, 10, dot, dip);
                    double lx = Math.Clamp(p.X - txt.Width / 2, 0, ActualWidth - txt.Width);
                    dc.DrawText(txt, new Point(lx, p.Y - txt.Height - 6));
                }
            }
        }
    }

    private void DrawLegend(DrawingContext dc, double dip)
    {
        double x = MarginLeft, y = 12;
        // Recent (ember) swatch.
        dc.DrawRectangle(AccentBrush, null, new Rect(x, y + 4, 16, 2.5));
        dc.DrawEllipse(AccentBrush, null, new Point(x + 8, y + 5.2), 3, 3);
        FormattedText r = Label(RecentLabel.ToUpperInvariant(), MonoSemiFace, 9.5, TextDimBrush, dip);
        dc.DrawText(r, new Point(x + 24, y));
        // Earlier (steel, dashed) swatch, to the right of the recent label.
        double x2 = x + 24 + r.Width + 22;
        dc.DrawLine(RefPen, new Point(x2, y + 5.2), new Point(x2 + 16, y + 5.2));
        dc.DrawEllipse(PanelBrush, RefDotPen, new Point(x2 + 8, y + 5.2), 3.2, 3.2); // same marker as the plot
        FormattedText e = Label(EarlierLabel.ToUpperInvariant(), MonoSemiFace, 9.5, TextDimBrush, dip);
        dc.DrawText(e, new Point(x2 + 24, y));
    }

    private static (double Min, double Max) YRange(IReadOnlyList<LoadResponseDatum> series)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (LoadResponseDatum d in series)
        {
            foreach (double? v in new[] { d.Earlier, d.Recent })
                if (v is { } x) { if (x < min) min = x; if (x > max) max = x; }
        }
        if (min > max) return (0, 40);
        min = Math.Floor((min - 3) / 5) * 5;
        max = Math.Ceiling((max + 4) / 5) * 5;
        if (min < 0) min = 0;
        return (min, Math.Max(min + 10, max));
    }

    private void DrawEmptyState(DrawingContext dc, string text, double w, double h, double dip)
    {
        string tracked = string.Join(((char)0x200A).ToString(), text.ToCharArray());
        FormattedText txt = Label(tracked, MonoFace, 10.5, LabelBrush, dip);
        double tx = (w - txt.Width) / 2, ty = (h - txt.Height) / 2;
        dc.DrawText(txt, new Point(tx, ty));
        var pen = FrozenPen(Color.FromArgb(110, ThermalPalette.TextFaint.R, ThermalPalette.TextFaint.G, ThermalPalette.TextFaint.B), 1);
        double cy = ty + txt.Height / 2 + 0.5;
        dc.DrawLine(pen, new Point(tx - 40, cy), new Point(tx - 14, cy));
        dc.DrawLine(pen, new Point(tx + txt.Width + 14, cy), new Point(tx + txt.Width + 40, cy));
    }

    // ---- frozen chrome -----------------------------------------------------

    private readonly Dictionary<string, FormattedText> _textCache = new();

    private FormattedText Label(string text, Typeface face, double size, Brush brush, double dip)
    {
        string key = $"{text}|{size}|{face.GetHashCode()}|{brush.GetHashCode()}|{dip}";
        if (_textCache.TryGetValue(key, out FormattedText? cached))
            return cached;
        if (_textCache.Count > 200) _textCache.Clear();
        var txt = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dip);
        _textCache[key] = txt;
        return txt;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double width, PenLineJoin join = PenLineJoin.Miter)
    {
        var p = new Pen(Frozen(c), width) { LineJoin = join };
        p.Freeze();
        return p;
    }

    private static Pen MakeRefPen()
    {
        var p = new Pen(Frozen(ThermalPalette.TextDim), 1.4)
        { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0), LineJoin = PenLineJoin.Round };
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
}

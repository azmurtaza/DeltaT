using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>Minimal line trace for the last ~10 minutes of one metric: a single
/// warm-slate hairline with an ember dot on the newest point — the "live"
/// cursor. No fill — the shape is the information.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0x5C, 0x4A, 0x3A), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color LineColor { get => (Color)GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

    private static readonly SolidColorBrush DotBrush = MakeFrozen(ThermalPalette.Accent);

    // One pen per color across all sparklines (they redraw every sample tick).
    private static readonly Dictionary<Color, Pen> PenCache = new();

    private static SolidColorBrush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen LinePen(Color c)
    {
        if (!PenCache.TryGetValue(c, out Pen? pen))
        {
            pen = new Pen(MakeFrozen(c), 1.3) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            PenCache[c] = pen;
        }
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<double>? values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values is null || values.Count < 2 || w < 10 || h < 6)
            return;

        double min = values.Min(), max = values.Max();
        double span = Math.Max(max - min, 2.0); // never zoom into pure noise
        min -= span * 0.15;
        max += span * 0.15;
        span = max - min;

        var points = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            double x = i / (double)(values.Count - 1) * (w - 4);
            double y = h - (values[i] - min) / span * h;
            points[i] = new Point(x, y);
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points[1..], true, true);
        }
        line.Freeze();

        dc.DrawGeometry(null, LinePen(LineColor), line);

        dc.DrawEllipse(DotBrush, null, points[^1], 2.0, 2.0);
    }
}

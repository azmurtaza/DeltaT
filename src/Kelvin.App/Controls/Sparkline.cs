using System.Windows;
using System.Windows.Media;

namespace Kelvin.App.Controls;

/// <summary>Minimal line chart for the last ~10 minutes of one metric: gradient
/// fill under the line, dot on the newest point, auto-scaled with padding.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(ThermalPalette.Accent, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color LineColor { get => (Color)GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

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
            double x = i / (double)(values.Count - 1) * w;
            double y = h - (values[i] - min) / span * h;
            points[i] = new Point(x, y);
        }

        // Fill under the line.
        var fill = new StreamGeometry();
        using (StreamGeometryContext ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, h), true, true);
            ctx.LineTo(points[0], false, false);
            for (int i = 1; i < points.Length; i++)
                ctx.LineTo(points[i], false, false);
            ctx.LineTo(new Point(points[^1].X, h), false, false);
        }
        fill.Freeze();
        Color c = LineColor;
        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(64, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fill);

        // The line itself.
        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points[1..], true, true);
        }
        line.Freeze();
        var pen = new Pen(new SolidColorBrush(c), 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // Newest-point dot.
        var dotBrush = new SolidColorBrush(c);
        dotBrush.Freeze();
        dc.DrawEllipse(dotBrush, null, points[^1], 2.2, 2.2);
    }
}

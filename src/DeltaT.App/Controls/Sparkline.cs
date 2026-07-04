using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>Minimal line trace for the last ~10 minutes of one metric: a single
/// hairline with a dot on the newest point. No fill, no gradient — the shape is
/// the information.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineColorProperty = DependencyProperty.Register(
        nameof(LineColor), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8A, 0x80, 0x71), FrameworkPropertyMetadataOptions.AffectsRender));

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

        Color c = LineColor;
        var pen = new Pen(new SolidColorBrush(c), 1.3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        var dotBrush = new SolidColorBrush(c);
        dotBrush.Freeze();
        dc.DrawEllipse(dotBrush, null, points[^1], 2.0, 2.0);
    }
}

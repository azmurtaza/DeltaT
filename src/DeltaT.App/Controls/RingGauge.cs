using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeltaT.App.Controls;

/// <summary>Custom-drawn 270° temperature gauge: hairline track with scale
/// ticks, a flat-capped value arc in the thermal color, monospaced numeral in
/// the middle. Value changes ease in over 400 ms so readings breathe instead
/// of ticking.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("°C", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SubTextProperty = DependencyProperty.Register(
        nameof(SubText), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
        nameof(HasValue), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Optional color override, 0..1. Lets the thermal color stay tied to
    /// the °C ratio even when the displayed unit is °F (whose ratio would skew).</summary>
    public static readonly DependencyProperty ColorFractionProperty = DependencyProperty.Register(
        nameof(ColorFraction), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty RenderValueProperty = DependencyProperty.Register(
        nameof(RenderValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public bool HasValue { get => (bool)GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }
    public double ColorFraction { get => (double)GetValue(ColorFractionProperty); set => SetValue(ColorFractionProperty, value); }
    private double RenderValue => (double)GetValue(RenderValueProperty);

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static readonly Pen TrackPen = MakeFrozenPen(ThermalPalette.Track, 1.2);
    private static readonly Pen TickMarkPen = MakeFrozenPen(ThermalPalette.Stroke, 1.0);

    private static Pen MakeFrozenPen(Color c, double w)
    {
        var pen = new Pen(new SolidColorBrush(c), w)
        { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var anim = new DoubleAnimation((double)e.NewValue, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        gauge.BeginAnimation(RenderValueProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double thickness = Math.Max(2.5, size * 0.028);
        double radius = size / 2 - thickness - 3;

        const double startAngle = 135;
        const double sweep = 270;

        // Hairline track + scale ticks (every eighth of the sweep).
        DrawArc(dc, TrackPen, center, radius, startAngle, startAngle + sweep);
        for (int i = 0; i <= 8; i++)
        {
            double a = (startAngle + sweep * i / 8) * Math.PI / 180.0;
            var p1 = new Point(center.X + (radius - 4) * Math.Cos(a), center.Y + (radius - 4) * Math.Sin(a));
            var p2 = new Point(center.X + (radius + 4) * Math.Cos(a), center.Y + (radius + 4) * Math.Sin(a));
            dc.DrawLine(TickMarkPen, p1, p2);
        }

        double fraction = Maximum > 0 ? Math.Clamp(RenderValue / Maximum, 0, 1) : 0;

        if (HasValue && fraction > 0.005)
        {
            double colorFraction = double.IsNaN(ColorFraction) ? fraction : Math.Clamp(ColorFraction, 0, 1);
            var valuePen = new Pen(ThermalPalette.BrushFromFraction(colorFraction), thickness)
            { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            valuePen.Freeze();
            DrawArc(dc, valuePen, center, radius, startAngle, startAngle + sweep * fraction);
        }

        // Center numerals.
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string mainText = HasValue ? Math.Round(RenderValue).ToString(CultureInfo.InvariantCulture) : "—";
        var main = new FormattedText(mainText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Light, FontStretches.Normal),
            size * 0.27, new SolidColorBrush(ThermalPalette.Text), dip);
        dc.DrawText(main, new Point(center.X - main.Width / 2, center.Y - main.Height * 0.68));

        var unit = new FormattedText(HasValue ? Unit : "no sensor", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size * 0.085, new SolidColorBrush(ThermalPalette.TextDim), dip);
        dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + main.Height * 0.36));

        if (!string.IsNullOrEmpty(SubText))
        {
            var sub = new FormattedText(SubText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.075, new SolidColorBrush(ThermalPalette.TextFaint), dip);
            dc.DrawText(sub, new Point(center.X - sub.Width / 2, center.Y + radius - sub.Height * 0.4));
        }
    }

    internal static void DrawArc(DrawingContext dc, Pen pen, Point center, double radius, double startDeg, double endDeg)
    {
        if (endDeg - startDeg < 0.1) return;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOn(center, radius, startDeg), false, false);
            // Split into ≤ 90° segments for stable ArcTo rendering.
            double angle = startDeg;
            while (angle < endDeg)
            {
                double next = Math.Min(angle + 90, endDeg);
                ctx.ArcTo(PointOn(center, radius, next), new Size(radius, radius), 0,
                    next - angle > 180, SweepDirection.Clockwise, true, false);
                angle = next;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOn(Point center, double radius, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}

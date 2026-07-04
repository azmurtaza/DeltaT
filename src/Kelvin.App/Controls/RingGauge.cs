using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Kelvin.App.Controls;

/// <summary>Custom-drawn 270° temperature ring: track, thermal-colored value arc
/// with a glow, big monospaced numeral in the middle. Value changes ease in over
/// 400 ms so the dashboard breathes instead of ticking.</summary>
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

    private static readonly DependencyProperty RenderValueProperty = DependencyProperty.Register(
        nameof(RenderValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public bool HasValue { get => (bool)GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }
    private double RenderValue => (double)GetValue(RenderValueProperty);

    private static readonly FontFamily Mono = new("Cascadia Code, Cascadia Mono, Consolas");

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
        double thickness = Math.Max(5, size * 0.062);
        double radius = size / 2 - thickness;

        const double startAngle = 135;
        const double sweep = 270;

        // Track.
        var trackPen = new Pen(new SolidColorBrush(ThermalPalette.Track), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        trackPen.Freeze();
        DrawArc(dc, trackPen, center, radius, startAngle, startAngle + sweep);

        double fraction = Maximum > 0 ? Math.Clamp(RenderValue / Maximum, 0, 1) : 0;

        if (HasValue && fraction > 0.005)
        {
            Color color = ThermalPalette.ColorFromFraction(fraction);
            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(56, color.R, color.G, color.B)), thickness * 2.1)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            glowPen.Freeze();
            var valuePen = new Pen(new SolidColorBrush(color), thickness)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            valuePen.Freeze();

            double endAngle = startAngle + sweep * fraction;
            DrawArc(dc, glowPen, center, radius, startAngle, endAngle);
            DrawArc(dc, valuePen, center, radius, startAngle, endAngle);
        }

        // Center numerals.
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string mainText = HasValue ? Math.Round(RenderValue).ToString(CultureInfo.InvariantCulture) : "—";
        var main = new FormattedText(mainText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.26, new SolidColorBrush(ThermalPalette.Text), dip);
        dc.DrawText(main, new Point(center.X - main.Width / 2, center.Y - main.Height * 0.72));

        var unit = new FormattedText(HasValue ? Unit : "no sensor", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size * 0.088, new SolidColorBrush(ThermalPalette.TextDim), dip);
        dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + main.Height * 0.34));

        if (!string.IsNullOrEmpty(SubText))
        {
            var sub = new FormattedText(SubText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.078, new SolidColorBrush(ThermalPalette.TextFaint), dip);
            dc.DrawText(sub, new Point(center.X - sub.Width / 2, center.Y + radius - sub.Height * 0.2));
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

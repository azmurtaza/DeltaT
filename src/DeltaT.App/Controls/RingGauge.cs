using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeltaT.App.Controls;

/// <summary>Custom-drawn 270° temperature gauge in the ember-console dialect:
/// hairline track, outer scale ticks (the last 15% tinted red — the danger
/// zone), and a chunky value arc that sweeps as a thermal gradient — steel at
/// the start, the current heat color at the tip (gaming-gauge flair,
/// but the gradient is data). Bold condensed DIN numeral in the middle.
/// Value changes ease in over 400 ms so readings breathe instead of
/// ticking.</summary>
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

    private static readonly FontFamily Din = new("Bahnschrift, Segoe UI");
    private static readonly FontFamily DinCond = new("Bahnschrift Condensed, Bahnschrift, Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private const double StartAngle = 135;
    private const double SweepAngle = 270;
    private const double DangerFrom = 0.85;  // scale ticks past here tint red
    private const int ArcSegments = 56;      // gradient sweep resolution

    private static readonly Pen TrackPen = MakeFrozenPen(ThermalPalette.Track, 1.2);
    private static readonly Pen MinorTickPen = MakeFrozenPen(ThermalPalette.Track, 1.0);
    private static readonly Pen MajorTickPen = MakeFrozenPen(Color.FromRgb(0x3C, 0x2B, 0x1C), 1.2);
    private static readonly Pen DangerTickPen = MakeFrozenPen(Color.FromArgb(120,
        ThermalPalette.Hot.R, ThermalPalette.Hot.G, ThermalPalette.Hot.B), 1.0);

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
        double thickness = Math.Max(3.5, size * 0.05);
        double radius = size / 2 - thickness - size * 0.055;

        // Hairline track.
        DrawArc(dc, TrackPen, center, radius, StartAngle, StartAngle + SweepAngle);

        // Outer scale: minor tick each 1/40 of the sweep, major each 1/8;
        // ticks in the danger zone are tinted.
        for (int i = 0; i <= 40; i++)
        {
            double f = i / 40.0;
            bool major = i % 5 == 0;
            double a = (StartAngle + SweepAngle * f) * Math.PI / 180.0;
            double r1 = radius + thickness + 2;
            double r2 = r1 + (major ? size * 0.038 : size * 0.02);
            Pen pen = f >= DangerFrom ? DangerTickPen : major ? MajorTickPen : MinorTickPen;
            dc.DrawLine(pen,
                new Point(center.X + r1 * Math.Cos(a), center.Y + r1 * Math.Sin(a)),
                new Point(center.X + r2 * Math.Cos(a), center.Y + r2 * Math.Sin(a)));
        }

        double fraction = Maximum > 0 ? Math.Clamp(RenderValue / Maximum, 0, 1) : 0;

        if (HasValue && fraction > 0.005)
        {
            // The arc is a gradient sweep: steel at the start of the scale,
            // the current thermal color at the tip. Drawn as short flat-capped
            // segments with a hair of overlap so no seams show.
            double colorFraction = double.IsNaN(ColorFraction) ? fraction : Math.Clamp(ColorFraction, 0, 1);
            int segments = Math.Max(2, (int)(ArcSegments * fraction));
            double sweepEnd = StartAngle + SweepAngle * fraction;
            double segSweep = (sweepEnd - StartAngle) / segments;
            for (int i = 0; i < segments; i++)
            {
                double t = (i + 1) / (double)segments;
                var segPen = new Pen(ThermalPalette.BrushFromFraction(t * colorFraction), thickness)
                { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                segPen.Freeze();
                double a0 = StartAngle + segSweep * i;
                double a1 = Math.Min(sweepEnd, a0 + segSweep + 0.6);
                DrawArc(dc, segPen, center, radius, a0, a1);
            }
        }

        // Center numerals.
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string mainText = HasValue ? Math.Round(RenderValue).ToString(CultureInfo.InvariantCulture) : "—";
        var main = new FormattedText(mainText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(DinCond, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size * 0.30, new SolidColorBrush(ThermalPalette.Text), dip);
        dc.DrawText(main, new Point(center.X - main.Width / 2, center.Y - main.Height * 0.66));

        var unit = new FormattedText(HasValue ? Unit : "no sensor", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size * 0.082, new SolidColorBrush(ThermalPalette.TextDim), dip);
        dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + main.Height * 0.38));

        if (!string.IsNullOrEmpty(SubText))
        {
            var sub = new FormattedText(Track(SubText), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Din, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.07, new SolidColorBrush(ThermalPalette.TextFaint), dip);
            dc.DrawText(sub, new Point(center.X - sub.Width / 2, center.Y + radius - sub.Height * 0.55));
        }
    }

    private static string Track(string s) =>
        string.Join(((char)0x200A).ToString(), s.ToUpperInvariant().ToCharArray());

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

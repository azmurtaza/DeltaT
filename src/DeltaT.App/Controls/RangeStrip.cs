using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>Faceplate range scale for the Device screen: a horizontal track
/// with the healthy / watch / concern zones shaded, threshold ticks labelled,
/// and a live marker at the current reading. All values arrive in display
/// units; the marker color comes from ColorFraction so it stays °C-true.</summary>
public sealed class RangeStrip : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Dp(nameof(Value), 0.0);
    public static readonly DependencyProperty MinimumProperty = Dp(nameof(Minimum), 25.0);
    public static readonly DependencyProperty NormProperty = Dp(nameof(Norm), 80.0);
    public static readonly DependencyProperty ConcernProperty = Dp(nameof(Concern), 95.0);
    public static readonly DependencyProperty MaximumProperty = Dp(nameof(Maximum), 100.0);
    public static readonly DependencyProperty ColorFractionProperty = Dp(nameof(ColorFraction), 0.0);

    public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
        nameof(HasValue), typeof(bool), typeof(RangeStrip),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty Dp(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(RangeStrip),
        new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Norm { get => (double)GetValue(NormProperty); set => SetValue(NormProperty, value); }
    public double Concern { get => (double)GetValue(ConcernProperty); set => SetValue(ConcernProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double ColorFraction { get => (double)GetValue(ColorFractionProperty); set => SetValue(ColorFractionProperty, value); }
    public bool HasValue { get => (bool)GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static SolidColorBrush Zone(Color c, byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static readonly SolidColorBrush CoolZone = Zone(ThermalPalette.Cool, 32);
    private static readonly SolidColorBrush WarmZone = Zone(ThermalPalette.Warm, 34);
    private static readonly SolidColorBrush HotZone = Zone(ThermalPalette.Hot, 38);
    private static readonly Pen WarmTick = TickPen(ThermalPalette.Warm);
    private static readonly Pen HotTick = TickPen(ThermalPalette.Hot);

    private static Pen TickPen(Color c)
    {
        var p = new Pen(Zone(c, 200), 1);
        p.Freeze();
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 24) return;

        double min = Minimum, max = Math.Max(Maximum, min + 1);
        double stripY = 8, stripH = 5;
        double X(double v) => Math.Clamp((v - min) / (max - min), 0, 1) * w;

        double xNorm = X(Norm), xConcern = X(Concern);
        dc.DrawRectangle(CoolZone, null, new Rect(0, stripY, xNorm, stripH));
        dc.DrawRectangle(WarmZone, null, new Rect(xNorm, stripY, Math.Max(0, xConcern - xNorm), stripH));
        dc.DrawRectangle(HotZone, null, new Rect(xConcern, stripY, Math.Max(0, w - xConcern), stripH));

        // Threshold ticks + labels.
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawThreshold(dc, dip, xNorm, Norm, WarmTick, ThermalPalette.Warm, w);
        DrawThreshold(dc, dip, xConcern, Concern, HotTick, ThermalPalette.Hot, w);

        // Live marker.
        if (HasValue)
        {
            double x = X(Value);
            var brush = ThermalPalette.BrushFromFraction(Math.Clamp(ColorFraction, 0, 1));
            dc.DrawRoundedRectangle(brush, null, new Rect(x - 1.25, stripY - 4, 2.5, stripH + 8), 1, 1);
        }
    }

    private static void DrawThreshold(DrawingContext dc, double dip, double x, double value,
        Pen pen, Color labelColor, double w)
    {
        dc.DrawLine(pen, new Point(x, 15), new Point(x, 21));
        var txt = new FormattedText(value.ToString("0", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9,
            new SolidColorBrush(Color.FromArgb(210, labelColor.R, labelColor.G, labelColor.B)), dip);
        dc.DrawText(txt, new Point(Math.Clamp(x - txt.Width / 2, 0, w - txt.Width), 23));
    }
}

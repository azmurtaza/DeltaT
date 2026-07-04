using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>The paste-health dial, drawn as a ring of radial ticks like a bench
/// meter: lit ticks up to the score, unlit ticks for the rest. While calibrating
/// it shows learning progress in dim amber with a percentage instead of
/// pretending to know the answer.</summary>
public sealed class ScoreDial : FrameworkElement
{
    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
        nameof(Score), typeof(int), typeof(ScoreDial),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CalibratingProperty = DependencyProperty.Register(
        nameof(Calibrating), typeof(bool), typeof(ScoreDial),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ScoreDial),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ScoreDial),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public int Score { get => (int)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public bool Calibrating { get => (bool)GetValue(CalibratingProperty); set => SetValue(CalibratingProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private const int Ticks = 41;
    private const double StartAngle = 120;
    private const double Sweep = 300;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Dictionary<Color, Pen> PenCache = new();

    private static Pen TickPen(Color color)
    {
        if (!PenCache.TryGetValue(color, out Pen? pen))
        {
            pen = new Pen(new SolidColorBrush(color), 1.6)
            { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            PenCache[color] = pen;
        }
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 24) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double rOut = size / 2 - 2;
        double rIn = rOut - size * 0.085;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double frac = Calibrating ? Math.Clamp(Progress, 0, 1) : Math.Clamp(Score / 100.0, 0, 1);
        int lit = (int)Math.Round(frac * Ticks);
        Color litColor = Calibrating
            ? Color.FromArgb(150, ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B)
            : ThermalPalette.VerdictColor(Score);

        Pen litPen = TickPen(litColor);
        Pen dimPen = TickPen(ThermalPalette.Track);
        for (int i = 0; i < Ticks; i++)
        {
            double angle = (StartAngle + Sweep * i / (Ticks - 1)) * Math.PI / 180.0;
            var a = new Point(center.X + rIn * Math.Cos(angle), center.Y + rIn * Math.Sin(angle));
            var b = new Point(center.X + rOut * Math.Cos(angle), center.Y + rOut * Math.Sin(angle));
            dc.DrawLine(i < lit ? litPen : dimPen, a, b);
        }

        if (Calibrating)
            DrawCenter(dc, center, size, dip, $"{Progress * 100:0}%", "calibrating", ThermalPalette.Accent);
        else
            DrawCenter(dc, center, size, dip, Score.ToString(CultureInfo.InvariantCulture), "/100", litColor);

        if (!string.IsNullOrEmpty(Label))
        {
            var label = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.082, new SolidColorBrush(ThermalPalette.TextFaint), dip);
            // In the 60° gap at the bottom of the sweep.
            dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + rOut - label.Height * 0.9));
        }
    }

    private static void DrawCenter(DrawingContext dc, Point center, double size, double dip, string big, string small, Color accent)
    {
        var main = new FormattedText(big, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Light, FontStretches.Normal),
            size * 0.26, new SolidColorBrush(accent), dip);
        dc.DrawText(main, new Point(center.X - main.Width / 2, center.Y - main.Height * 0.62));

        var sub = new FormattedText(small, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size * 0.08, new SolidColorBrush(ThermalPalette.TextDim), dip);
        dc.DrawText(sub, new Point(center.X - sub.Width / 2, center.Y + main.Height * 0.42));
    }
}

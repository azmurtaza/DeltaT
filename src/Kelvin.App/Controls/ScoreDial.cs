using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Kelvin.App.Controls;

/// <summary>The paste-health dial: a 300° ring colored by verdict, score numeral
/// in the middle. While calibrating it shows learning progress as a dashed ring
/// with a percentage instead of pretending to know the answer.</summary>
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

    private static readonly FontFamily Mono = new("Cascadia Code, Cascadia Mono, Consolas");

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 24) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double thickness = Math.Max(4, size * 0.055);
        double radius = size / 2 - thickness;
        const double startAngle = 120;
        const double sweep = 300;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var trackPen = new Pen(new SolidColorBrush(ThermalPalette.Track), thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        trackPen.Freeze();
        RingGauge.DrawArc(dc, trackPen, center, radius, startAngle, startAngle + sweep);

        if (Calibrating)
        {
            double frac = Math.Clamp(Progress, 0, 1);
            if (frac > 0.01)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B)), thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    DashStyle = new DashStyle(new[] { 0.05, 1.6 }, 0),
                    DashCap = PenLineCap.Round,
                };
                pen.Freeze();
                RingGauge.DrawArc(dc, pen, center, radius, startAngle, startAngle + sweep * frac);
            }

            DrawCenter(dc, center, size, dip, $"{Progress * 100:0}%", "learning", ThermalPalette.Accent);
        }
        else
        {
            Color color = ThermalPalette.VerdictColor(Score);
            double frac = Math.Clamp(Score / 100.0, 0, 1);
            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(52, color.R, color.G, color.B)), thickness * 2.2)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            glowPen.Freeze();
            var pen = new Pen(new SolidColorBrush(color), thickness)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            RingGauge.DrawArc(dc, glowPen, center, radius, startAngle, startAngle + sweep * frac);
            RingGauge.DrawArc(dc, pen, center, radius, startAngle, startAngle + sweep * frac);

            DrawCenter(dc, center, size, dip, Score.ToString(CultureInfo.InvariantCulture), "/100", color);
        }

        if (!string.IsNullOrEmpty(Label))
        {
            var label = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.085, new SolidColorBrush(ThermalPalette.TextFaint), dip);
            dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + radius * 0.62));
        }
    }

    private static void DrawCenter(DrawingContext dc, Point center, double size, double dip, string big, string small, Color accent)
    {
        var main = new FormattedText(big, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size * 0.24, new SolidColorBrush(accent), dip);
        dc.DrawText(main, new Point(center.X - main.Width / 2, center.Y - main.Height * 0.66));

        var sub = new FormattedText(small, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size * 0.085, new SolidColorBrush(ThermalPalette.TextDim), dip);
        dc.DrawText(sub, new Point(center.X - sub.Width / 2, center.Y + main.Height * 0.38));
    }
}

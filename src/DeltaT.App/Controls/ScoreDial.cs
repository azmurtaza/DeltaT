using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeltaT.App.Controls;

/// <summary>The paste-health dial: a 270° tick scale like a faceplate meter —
/// minor tick every 2 points, major every 10 — lit up to the score in the
/// verdict color (fading in along the sweep, ember-console style), bold
/// condensed DIN numeral in the middle, verdict word under it. While
/// calibrating it shows learning progress in dim ember with a percentage
/// instead of pretending to know the answer. Value changes sweep in over
/// 600 ms.</summary>
public sealed class ScoreDial : FrameworkElement
{
    public static readonly DependencyProperty ScoreProperty = DependencyProperty.Register(
        nameof(Score), typeof(int), typeof(ScoreDial),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnFractionSourceChanged));

    public static readonly DependencyProperty CalibratingProperty = DependencyProperty.Register(
        nameof(Calibrating), typeof(bool), typeof(ScoreDial),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnFractionSourceChanged));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ScoreDial),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnFractionSourceChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ScoreDial),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VerdictProperty = DependencyProperty.Register(
        nameof(Verdict), typeof(string), typeof(ScoreDial),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty RenderFractionProperty = DependencyProperty.Register(
        nameof(RenderFraction), typeof(double), typeof(ScoreDial),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Score { get => (int)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public bool Calibrating { get => (bool)GetValue(CalibratingProperty); set => SetValue(CalibratingProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Verdict { get => (string)GetValue(VerdictProperty); set => SetValue(VerdictProperty, value); }
    private double RenderFraction => (double)GetValue(RenderFractionProperty);

    private const int MinorTicks = 51;      // every 2 points across the sweep
    private const int MajorEvery = 5;       // every 10 points
    private const double StartAngle = 135;
    private const double Sweep = 270;

    private static readonly FontFamily Din = new("Bahnschrift, Segoe UI");
    private static readonly FontFamily DinCond = new("Bahnschrift Condensed, Bahnschrift, Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Dictionary<(Color, double), Pen> PenCache = new();

    private static Pen TickPen(Color color, double width)
    {
        if (!PenCache.TryGetValue((color, width), out Pen? pen))
        {
            pen = new Pen(new SolidColorBrush(color), width)
            { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            PenCache[(color, width)] = pen;
        }
        return pen;
    }

    private static void OnFractionSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dial = (ScoreDial)d;
        double target = dial.Calibrating
            ? Math.Clamp(dial.Progress, 0, 1)
            : Math.Clamp(dial.Score / 100.0, 0, 1);
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        dial.BeginAnimation(RenderFractionProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 24) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double rOut = size / 2 - 2;
        double rMinor = rOut - size * 0.075;
        double rMajor = rOut - size * 0.115;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double frac = Math.Clamp(RenderFraction, 0, 1);
        int lit = (int)Math.Round(frac * (MinorTicks - 1));
        Color litColor = Calibrating
            ? Color.FromArgb(140, ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B)
            : ThermalPalette.VerdictColor(Score);

        Pen dimMinor = TickPen(ThermalPalette.Track, 1.4);
        Pen dimMajor = TickPen(Color.FromRgb(0x3C, 0x2B, 0x1C), 2.0);

        for (int i = 0; i < MinorTicks; i++)
        {
            bool major = i % MajorEvery == 0;
            double angle = (StartAngle + Sweep * i / (MinorTicks - 1)) * Math.PI / 180.0;
            double rIn = major ? rMajor : rMinor;
            var a = new Point(center.X + rIn * Math.Cos(angle), center.Y + rIn * Math.Sin(angle));
            var b = new Point(center.X + rOut * Math.Cos(angle), center.Y + rOut * Math.Sin(angle));
            bool on = i <= lit && frac > 0;
            Pen pen;
            if (on)
            {
                // Lit ticks brighten along the sweep — the tip carries the verdict.
                // Alpha quantized to 5s so the pen cache stays small.
                byte alpha = (byte)((150 + (int)(105.0 * (lit > 0 ? i / (double)lit : 1))) / 5 * 5);
                Color c = Color.FromArgb(Math.Min(alpha, litColor.A), litColor.R, litColor.G, litColor.B);
                pen = TickPen(c, major ? 2.0 : 1.4);
            }
            else
            {
                pen = major ? dimMajor : dimMinor;
            }
            dc.DrawLine(pen, a, b);
        }

        Color subColor = Calibrating
            ? Color.FromArgb(190, ThermalPalette.Accent.R, ThermalPalette.Accent.G, ThermalPalette.Accent.B)
            : litColor;
        if (Calibrating)
            // No score yet, so the meter says so: dashes for the reading, the
            // learning progress spelled out small (the ticks fill with it too).
            DrawCenter(dc, center, size, dip, "--", $"CAL {Progress * 100:0}%", ThermalPalette.TextFaint, subColor);
        else
            DrawCenter(dc, center, size, dip, Score.ToString(CultureInfo.InvariantCulture), Verdict, ThermalPalette.Text, subColor);

        if (!string.IsNullOrEmpty(Label))
        {
            var label = new FormattedText(Track(Label), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Din, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.078, new SolidColorBrush(ThermalPalette.TextFaint), dip);
            // In the 90° gap at the bottom of the sweep.
            dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + rOut - label.Height * 1.15));
        }
    }

    private static void DrawCenter(DrawingContext dc, Point center, double size, double dip,
        string big, string small, Color main, Color sub)
    {
        var numeral = new FormattedText(big, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(DinCond, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size * 0.31, new SolidColorBrush(main), dip);
        dc.DrawText(numeral, new Point(center.X - numeral.Width / 2, center.Y - numeral.Height * 0.60));

        if (string.IsNullOrEmpty(small)) return;
        var word = new FormattedText(Track(small), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Din, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size * 0.068, new SolidColorBrush(sub), dip);
        dc.DrawText(word, new Point(center.X - word.Width / 2, center.Y + numeral.Height * 0.44));
    }

    /// <summary>Hair-spaced caps, same trick as Ui.Tracking but for drawn text.</summary>
    private static string Track(string s) =>
        string.Join(((char)0x200A).ToString(), s.ToUpperInvariant().ToCharArray());
}

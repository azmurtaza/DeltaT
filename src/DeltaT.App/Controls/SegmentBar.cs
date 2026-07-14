using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeltaT.App.Controls;

/// <summary>The linear sibling of <see cref="ScoreDial"/>: a segmented LED tick meter
/// (console meter-bridge language) for the dashboard's health matrix. Ticks light up
/// to the value in the given tint, brightening toward the tip exactly like the dial's
/// sweep; unlit ticks stay as dark track marks so an empty meter still reads as a
/// meter, not a missing control. Value changes ease in over 500 ms.</summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    /// <summary>Lit-segment brush; only the color of a SolidColorBrush is used.</summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty RenderFractionProperty = DependencyProperty.Register(
        nameof(RenderFraction), typeof(double), typeof(SegmentBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–100.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    private double RenderFraction => (double)GetValue(RenderFractionProperty);

    private const double TickWidth = 2.6;
    private const double TickGap = 2.4;

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly SolidColorBrush TrackBrush = Frozen(ThermalPalette.Track);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SegmentBar)d;
        var anim = new DoubleAnimation(Math.Clamp(bar.Value / 100.0, 0, 1), TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        bar.BeginAnimation(RenderFractionProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < TickWidth || h < 2) return;

        int count = Math.Max(1, (int)((w + TickGap) / (TickWidth + TickGap)));
        double frac = Math.Clamp(RenderFraction, 0, 1);
        int lit = (int)Math.Round(frac * count);

        Color tint = Tint is SolidColorBrush scb ? scb.Color : ThermalPalette.Accent;

        for (int i = 0; i < count; i++)
        {
            Brush brush;
            if (i < lit)
            {
                // Brighten along the run — the tip carries the verdict, dial-style.
                // Alpha quantized to 5s so the brush cache stays small.
                byte alpha = (byte)((140 + (int)(115.0 * (lit > 1 ? i / (double)(lit - 1) : 1))) / 5 * 5);
                brush = Cached(Color.FromArgb(Math.Min(alpha, tint.A), tint.R, tint.G, tint.B));
            }
            else
            {
                brush = TrackBrush;
            }
            dc.DrawRectangle(brush, null, new Rect(i * (TickWidth + TickGap), 0, TickWidth, h));
        }
    }

    private static SolidColorBrush Cached(Color c)
    {
        if (!BrushCache.TryGetValue(c, out SolidColorBrush? b))
        {
            b = Frozen(c);
            BrushCache[c] = b;
        }
        return b;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

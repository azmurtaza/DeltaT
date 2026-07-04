using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>The one thermal gradient, shared by every control that colors by
/// temperature, so steel/amber/vermilion always mean the same thing everywhere.
/// Palette is the bench-instrument set: warm soot neutrals, one amber signal.</summary>
public static class ThermalPalette
{
    public static readonly Color Cool = Color.FromRgb(0x7F, 0xA9, 0xB8);    // steel — comfortably below limit
    public static readonly Color Warm = Color.FromRgb(0xE2, 0xA1, 0x44);    // amber — working hard
    public static readonly Color HotWarn = Color.FromRgb(0xD2, 0x69, 0x3B); // burnt orange — closing on the limit
    public static readonly Color Hot = Color.FromRgb(0xD6, 0x4B, 0x33);     // vermilion — at the limit
    public static readonly Color Good = Color.FromRgb(0xA4, 0xB3, 0x6A);    // sage — healthy verdict
    public static readonly Color Accent = Color.FromRgb(0xE2, 0xA1, 0x44);  // brand amber (== Warm, on purpose)
    public static readonly Color Track = Color.FromRgb(0x26, 0x21, 0x19);
    public static readonly Color Stroke = Color.FromRgb(0x2B, 0x26, 0x20);
    public static readonly Color Bg = Color.FromRgb(0x13, 0x11, 0x10);
    public static readonly Color Text = Color.FromRgb(0xE9, 0xE1, 0xD2);
    public static readonly Color TextDim = Color.FromRgb(0x9A, 0x90, 0x81);
    public static readonly Color TextFaint = Color.FromRgb(0x6A, 0x61, 0x52);

    private static readonly Dictionary<int, SolidColorBrush> Cache = new();

    /// <summary>fraction = temp / limit. Steel below 55 %, blends to vermilion at the limit.</summary>
    public static Color ColorFromFraction(double f)
    {
        f = Math.Clamp(f, 0, 1);
        return f switch
        {
            <= 0.55 => Cool,
            <= 0.72 => Lerp(Cool, Warm, (f - 0.55) / 0.17),
            <= 0.87 => Lerp(Warm, HotWarn, (f - 0.72) / 0.15),
            _ => Lerp(HotWarn, Hot, (f - 0.87) / 0.13),
        };
    }

    public static SolidColorBrush BrushFromFraction(double f)
    {
        int key = (int)(Math.Clamp(f, 0, 1) * 100);
        if (!Cache.TryGetValue(key, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ColorFromFraction(key / 100.0));
            brush.Freeze();
            Cache[key] = brush;
        }
        return brush;
    }

    /// <summary>Verdicts read like an instrument: sage/steel = fine, amber = watch it,
    /// orange/vermilion = act.</summary>
    public static Color VerdictColor(int score) => score switch
    {
        >= 85 => Good,
        >= 70 => Cool,
        >= 50 => Warm,
        >= 30 => HotWarn,
        _ => Hot,
    };

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}

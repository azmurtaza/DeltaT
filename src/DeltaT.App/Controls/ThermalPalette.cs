using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>The one thermal gradient, shared by every control that colors by
/// temperature, so steel/amber/red always mean the same thing everywhere.
/// Palette is the ember-console set (docs/DESIGN.md): warm soot surfaces, one
/// ember-orange signal, thermal color strictly functional.</summary>
public static class ThermalPalette
{
    public static readonly Color Cool = Color.FromRgb(0x7F, 0xA2, 0xB8);    // steel — comfortably below limit
    public static readonly Color Warm = Color.FromRgb(0xEC, 0xAF, 0x3A);    // amber — working hard
    public static readonly Color HotWarn = Color.FromRgb(0xF4, 0x74, 0x1F); // orange — closing on the limit
    public static readonly Color Hot = Color.FromRgb(0xE9, 0x3A, 0x2B);     // red — at the limit
    public static readonly Color Good = Color.FromRgb(0x57, 0xC4, 0x65);    // green — healthy verdict only
    public static readonly Color Accent = Color.FromRgb(0xF2, 0x6A, 0x1B);  // signal ember (interactive / live trace)
    public static readonly Color Track = Color.FromRgb(0x2B, 0x1D, 0x12);   // unlit ticks, gauge tracks
    public static readonly Color Stroke = Color.FromRgb(0x33, 0x24, 0x18);
    public static readonly Color Bg = Color.FromRgb(0x0E, 0x0A, 0x07);
    public static readonly Color Panel = Color.FromRgb(0x17, 0x10, 0x0A);
    public static readonly Color PanelHigh = Color.FromRgb(0x21, 0x17, 0x11);
    public static readonly Color Text = Color.FromRgb(0xF2, 0xE8, 0xDC);
    public static readonly Color TextDim = Color.FromRgb(0xAE, 0x98, 0x84);
    public static readonly Color TextFaint = Color.FromRgb(0x6E, 0x5C, 0x4B);

    private static readonly Dictionary<int, SolidColorBrush> Cache = new();

    /// <summary>fraction = temp / limit. Steel below 55 %, blends to red at the limit.</summary>
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

    /// <summary>Verdicts read like an instrument: green/steel = fine, amber = watch it,
    /// orange/red = act.</summary>
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

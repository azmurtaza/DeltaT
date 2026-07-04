using System.Windows.Media;

namespace Kelvin.App.Controls;

/// <summary>The one thermal gradient, shared by every control that colors by
/// temperature, so cyan/amber/red always mean the same thing everywhere.</summary>
public static class ThermalPalette
{
    public static readonly Color Cool = Color.FromRgb(0x22, 0xD3, 0xEE);
    public static readonly Color Warm = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color HotWarn = Color.FromRgb(0xF9, 0x73, 0x16);
    public static readonly Color Hot = Color.FromRgb(0xEF, 0x44, 0x44);
    public static readonly Color Good = Color.FromRgb(0x34, 0xD3, 0x99);
    public static readonly Color Accent = Color.FromRgb(0x38, 0xBD, 0xF8);
    public static readonly Color Track = Color.FromRgb(0x1B, 0x24, 0x2E);
    public static readonly Color TextDim = Color.FromRgb(0x8B, 0x98, 0xA5);
    public static readonly Color TextFaint = Color.FromRgb(0x55, 0x61, 0x6D);
    public static readonly Color Text = Color.FromRgb(0xE8, 0xEE, 0xF4);

    private static readonly Dictionary<int, SolidColorBrush> Cache = new();

    /// <summary>fraction = temp / limit. Cool below 55 %, blends to red at the limit.</summary>
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

    public static Color VerdictColor(int score) => score switch
    {
        >= 85 => Good,
        >= 70 => Accent,
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

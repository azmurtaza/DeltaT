using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>Silkscreen-style hardware glyphs for the telemetry ledger and
/// device panels: chip, graphics card, drive, battery. Stroke-drawn at
/// 1.3px in the quiet label color — they identify, they don't decorate.
/// Geometry lives on a 16×16 grid and scales with the element.</summary>
public sealed class ComponentGlyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ComponentGlyph),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Ledger badge string: CPU, iGPU, GPU, SSD, BAT — anything else
    /// gets the generic module square.</summary>
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private static readonly Pen Stroke = MakePen(ThermalPalette.TextDim, 1.3);
    private static readonly Pen StrokeFaint = MakePen(ThermalPalette.TextFaint, 1.1);
    private static readonly SolidColorBrush FillFaint = MakeBrush(
        Color.FromArgb(90, ThermalPalette.TextFaint.R, ThermalPalette.TextFaint.G, ThermalPalette.TextFaint.B));

    private static Pen MakePen(Color c, double w)
    {
        var p = new Pen(MakeBrush(c), w)
        { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Miter };
        p.Freeze();
        return p;
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;
        double s = size / 16.0;
        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;

        Point P(double x, double y) => new(ox + x * s, oy + y * s);
        Rect R(double x, double y, double w, double h) => new(P(x, y), new Size(w * s, h * s));
        void Line(Pen pen, double x1, double y1, double x2, double y2) => dc.DrawLine(pen, P(x1, y1), P(x2, y2));

        switch (Kind)
        {
            case "CPU" or "iGPU":
                // Package with die and pins.
                dc.DrawRectangle(null, Stroke, R(3.5, 3.5, 9, 9));
                dc.DrawRectangle(FillFaint, null, R(6.2, 6.2, 3.6, 3.6));
                for (int i = 0; i < 3; i++)
                {
                    double t = 5.4 + i * 2.6;
                    Line(StrokeFaint, t, 1.2, t, 3.0);      // top pins
                    Line(StrokeFaint, t, 13.0, t, 14.8);    // bottom pins
                    Line(StrokeFaint, 1.2, t, 3.0, t);      // left pins
                    Line(StrokeFaint, 13.0, t, 14.8, t);    // right pins
                }
                break;

            case "GPU":
                // Card with a fan and bracket slot.
                dc.DrawRectangle(null, Stroke, R(1.8, 3.8, 12.4, 8.2));
                dc.DrawEllipse(null, Stroke, P(6.4, 7.9), 2.5 * s, 2.5 * s);
                dc.DrawEllipse(FillFaint, null, P(6.4, 7.9), 0.8 * s, 0.8 * s);
                Line(StrokeFaint, 10.6, 6.0, 12.6, 6.0);
                Line(StrokeFaint, 10.6, 8.0, 12.6, 8.0);
                Line(StrokeFaint, 10.6, 10.0, 12.6, 10.0);
                Line(Stroke, 1.8, 13.6, 7.2, 13.6);          // bracket
                break;

            case "SSD":
                // M.2 stick: body, edge notch, contact fingers.
                dc.DrawRectangle(null, Stroke, R(2.2, 5.0, 11.6, 6.0));
                Line(Stroke, 2.2, 7.4, 3.6, 7.4);            // key notch
                for (int i = 0; i < 4; i++)
                {
                    double x = 5.4 + i * 2.2;
                    Line(StrokeFaint, x, 11.0, x, 12.6);     // fingers
                }
                dc.DrawRectangle(FillFaint, null, R(8.6, 6.6, 3.4, 2.8));
                break;

            case "BAT":
                // Cell with terminal and charge bar.
                dc.DrawRectangle(null, Stroke, R(2.0, 5.2, 10.6, 5.6));
                dc.DrawRectangle(FillFaint, null, R(13.2, 6.8, 1.6, 2.4));
                dc.DrawRectangle(FillFaint, null, R(3.6, 6.8, 4.2, 2.4));
                break;

            default:
                // Generic module.
                dc.DrawRectangle(null, Stroke, R(3.5, 3.5, 9, 9));
                Line(StrokeFaint, 5.5, 8.0, 10.5, 8.0);
                break;
        }
    }
}

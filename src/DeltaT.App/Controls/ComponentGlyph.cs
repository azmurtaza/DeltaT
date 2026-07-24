using System.Windows;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>Silkscreen-style hardware glyphs for the telemetry ledger, device
/// panels and the health matrix: chip, graphics card, drive, battery, plus the
/// judged subsystems (paste joint, airflow, fan, mount, headroom, power).
/// Stroke-drawn at 1.3px in the quiet label color — they identify, they don't
/// decorate. Geometry lives on a 16×16 grid and scales with the element.</summary>
public sealed class ComponentGlyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ComponentGlyph),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Badge string: CPU, iGPU, GPU, SSD, BAT for hardware; PASTE, AIRFLOW,
    /// FANS, MOUNT, HEADROOM, POWER for the health-matrix aspects — anything else
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
        void Poly(Pen pen, params (double X, double Y)[] pts)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(P(pts[0].X, pts[0].Y), isFilled: false, isClosed: true);
                for (int i = 1; i < pts.Length; i++)
                    ctx.LineTo(P(pts[i].X, pts[i].Y), isStroked: true, isSmoothJoin: false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        void Arrow(double x1, double y, double x2)
        {
            Line(Stroke, x1, y, x2, y);
            Line(Stroke, x2, y, x2 - 1.9, y - 1.6);
            Line(Stroke, x2, y, x2 - 1.9, y + 1.6);
        }

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

            case "PASTE":
                // The joint itself: heatsink fins, the paste bead, the die under it.
                Line(Stroke, 2.5, 6.2, 13.5, 6.2);                       // heatsink base
                for (int i = 0; i < 5; i++)
                {
                    double x = 3.5 + i * 2.25;
                    Line(StrokeFaint, x, 2.4, x, 6.2);                   // fins
                }
                dc.DrawRectangle(FillFaint, null, R(4.2, 7.2, 7.6, 1.8)); // paste layer
                dc.DrawRectangle(null, Stroke, R(5.2, 10.0, 5.6, 3.6));   // die
                break;

            case "AIRFLOW":
                // Air crossing the fin stack: three staggered flow arrows.
                Arrow(2.0, 4.2, 11.6);
                Arrow(3.8, 8.0, 13.8);
                Arrow(2.0, 11.8, 10.2);
                break;

            case "FANS":
                // Fan front-on: shroud ring, hub, swept blades.
                dc.DrawEllipse(null, Stroke, P(8, 8), 5.6 * s, 5.6 * s);
                dc.DrawEllipse(FillFaint, null, P(8, 8), 1.2 * s, 1.2 * s);
                for (int i = 0; i < 4; i++)
                {
                    double a = Math.PI / 2 * i + 0.5;
                    Line(Stroke,
                        8 + Math.Cos(a) * 2.0, 8 + Math.Sin(a) * 2.0,
                        8 + Math.Cos(a + 0.7) * 4.4, 8 + Math.Sin(a + 0.7) * 4.4);
                }
                break;

            case "MOUNT":
                // Cold plate over the die, screws at the corners: the clamp, not the chip.
                dc.DrawRectangle(null, Stroke, R(3.8, 3.8, 8.4, 8.4));
                dc.DrawRectangle(FillFaint, null, R(6.5, 6.5, 3.0, 3.0));
                foreach ((double cx, double cy) in new (double, double)[] { (2.2, 2.2), (13.8, 2.2), (2.2, 13.8), (13.8, 13.8) })
                    dc.DrawEllipse(FillFaint, StrokeFaint, P(cx, cy), 1.0 * s, 1.0 * s);
                break;

            case "HEADROOM":
                // The silicon ceiling, and the die pushing up toward it.
                Line(Stroke, 2.5, 3.0, 13.5, 3.0);                       // the limit
                Line(Stroke, 8, 13.6, 8, 6.2);                           // rising shaft
                Line(Stroke, 8, 6.2, 5.6, 8.8);                          // arrowhead
                Line(Stroke, 8, 6.2, 10.4, 8.8);
                break;

            case "POWER":
                // Lightning bolt: watts on the move.
                Poly(Stroke, (9.2, 1.6), (5.2, 8.8), (7.6, 8.8), (6.6, 14.4), (10.9, 7.2), (8.4, 7.2));
                break;

            case "RAM":
                // Memory module: the board, its chips, and keyed contact fingers.
                dc.DrawRectangle(null, Stroke, R(1.6, 4.6, 12.8, 6.4));
                for (int i = 0; i < 3; i++)
                    dc.DrawRectangle(FillFaint, null, R(3.0 + i * 3.4, 6.0, 2.4, 2.4)); // chips
                for (int i = 0; i < 7; i++)
                {
                    if (i == 3) continue;                    // key notch gap
                    double x = 2.6 + i * 1.6;
                    Line(StrokeFaint, x, 11.0, x, 13.2);     // contact fingers
                }
                break;

            default:
                // Generic module.
                dc.DrawRectangle(null, Stroke, R(3.5, 3.5, 9, 9));
                Line(StrokeFaint, 5.5, 8.0, 10.5, 8.0);
                break;
        }
    }
}

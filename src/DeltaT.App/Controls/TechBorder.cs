using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeltaT.App.Controls;

/// <summary>Panel chrome in the ember-console dialect: a Border that draws its
/// surface as a chamfered plate (individual corners can be cut at 45°) and can
/// overlay thin L-brackets on the square corners — the ember-console plate
/// signature. Layout (padding, child inset) is plain Border; only the paint
/// is replaced. CornerRadius is ignored.</summary>
public sealed class TechBorder : Border
{
    public static readonly DependencyProperty CutTopLeftProperty = Dp(nameof(CutTopLeft), 0.0);
    public static readonly DependencyProperty CutTopRightProperty = Dp(nameof(CutTopRight), 0.0);
    public static readonly DependencyProperty CutBottomLeftProperty = Dp(nameof(CutBottomLeft), 0.0);
    public static readonly DependencyProperty CutBottomRightProperty = Dp(nameof(CutBottomRight), 0.0);

    public static readonly DependencyProperty BracketsProperty = DependencyProperty.Register(
        nameof(Brackets), typeof(bool), typeof(TechBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BracketBrushProperty = DependencyProperty.Register(
        nameof(BracketBrush), typeof(Brush), typeof(TechBorder),
        new FrameworkPropertyMetadata(new SolidColorBrush(ThermalPalette.Accent),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BracketLengthProperty = Dp(nameof(BracketLength), 14.0);

    private static DependencyProperty Dp(string name, double def) => DependencyProperty.Register(
        name, typeof(double), typeof(TechBorder),
        new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public double CutTopLeft { get => (double)GetValue(CutTopLeftProperty); set => SetValue(CutTopLeftProperty, value); }
    public double CutTopRight { get => (double)GetValue(CutTopRightProperty); set => SetValue(CutTopRightProperty, value); }
    public double CutBottomLeft { get => (double)GetValue(CutBottomLeftProperty); set => SetValue(CutBottomLeftProperty, value); }
    public double CutBottomRight { get => (double)GetValue(CutBottomRightProperty); set => SetValue(CutBottomRightProperty, value); }
    public bool Brackets { get => (bool)GetValue(BracketsProperty); set => SetValue(BracketsProperty, value); }
    public Brush BracketBrush { get => (Brush)GetValue(BracketBrushProperty); set => SetValue(BracketBrushProperty, value); }
    public double BracketLength { get => (double)GetValue(BracketLengthProperty); set => SetValue(BracketLengthProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;

        double bt = Math.Max(BorderThickness.Left,
            Math.Max(BorderThickness.Top, Math.Max(BorderThickness.Right, BorderThickness.Bottom)));
        double inset = bt > 0 ? bt / 2 : 0;

        Geometry plate = BuildPlate(w, h, inset);
        Pen? pen = null;
        if (BorderBrush is not null && bt > 0)
        {
            pen = new Pen(BorderBrush, bt) { LineJoin = PenLineJoin.Miter };
            pen.Freeze();
        }
        dc.DrawGeometry(Background, pen, plate);

        if (!Brackets) return;

        // L-brackets hugging the top-right and bottom-left corners, drawn just
        // inside the plate edge. Skipped on a corner that is chamfered.
        double len = Math.Min(BracketLength, Math.Min(w, h) / 3);
        var bracketPen = new Pen(BracketBrush, 1.6) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        bracketPen.Freeze();
        double e = 0.8; // edge inset so the 1.6px stroke sits on the border line

        if (CutTopRight <= 0)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(new Point(w - e - len, e), false, false);
                ctx.LineTo(new Point(w - e, e), true, false);
                ctx.LineTo(new Point(w - e, e + len), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, bracketPen, g);
        }
        if (CutBottomLeft <= 0)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(new Point(e + len, h - e), false, false);
                ctx.LineTo(new Point(e, h - e), true, false);
                ctx.LineTo(new Point(e, h - e - len), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, bracketPen, g);
        }
    }

    private Geometry BuildPlate(double w, double h, double inset)
    {
        double x0 = inset, y0 = inset, x1 = w - inset, y1 = h - inset;
        double maxCut = Math.Min(w, h) / 2 - inset;
        double tl = Math.Clamp(CutTopLeft, 0, maxCut);
        double tr = Math.Clamp(CutTopRight, 0, maxCut);
        double br = Math.Clamp(CutBottomRight, 0, maxCut);
        double bl = Math.Clamp(CutBottomLeft, 0, maxCut);

        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(x0 + tl, y0), true, true);
            ctx.LineTo(new Point(x1 - tr, y0), true, false);
            if (tr > 0) ctx.LineTo(new Point(x1, y0 + tr), true, false);
            ctx.LineTo(new Point(x1, y1 - br), true, false);
            if (br > 0) ctx.LineTo(new Point(x1 - br, y1), true, false);
            ctx.LineTo(new Point(x0 + bl, y1), true, false);
            if (bl > 0) ctx.LineTo(new Point(x0, y1 - bl), true, false);
            ctx.LineTo(new Point(x0, y0 + tl), true, false);
            if (tl > 0) ctx.LineTo(new Point(x0 + tl, y0), true, false);
        }
        geo.Freeze();
        return geo;
    }
}

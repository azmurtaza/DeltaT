using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DeltaT.App.Controls;
using DeltaT.Core.Scoring;

namespace DeltaT.App.ViewModels;

/// <summary>One cell of the dashboard's health matrix: a component × aspect readout.
/// Shows the 0–100 health in the verdict color with a fill bar, a state word for the
/// power column (STOCK / OC / UV), or a faint "--" when the sensor doesn't exist or
/// DeltaT is still learning. The evidence sentence rides along as the tooltip.</summary>
public sealed partial class AspectCellViewModel : ObservableObject
{
    private static readonly Brush FaintBrush = Frozen(ThermalPalette.TextFaint);
    private static readonly Brush DimBrush = Frozen(ThermalPalette.TextDim);
    private static readonly Brush CoolBrush = Frozen(ThermalPalette.Cool);

    [ObservableProperty] private string _value = "--";
    [ObservableProperty] private Brush _valueBrush = FaintBrush;
    [ObservableProperty] private double _barValue;
    [ObservableProperty] private bool _hasBar;
    [ObservableProperty] private string _detail = "Still learning this machine.";

    public void Update(AspectHealth? aspect, bool calibrating)
    {
        if (aspect is null || !aspect.Known)
        {
            Value = "--";
            HasBar = false;
            ValueBrush = FaintBrush;
            Detail = aspect?.Detail ?? (calibrating
                ? "Still learning this machine. The readout fills in as the baseline locks."
                : "No reading yet.");
            return;
        }

        Detail = aspect.Detail;
        if (aspect.Score is { } score)
        {
            Value = score.ToString();
            BarValue = score;
            HasBar = true;
            ValueBrush = new SolidColorBrush(ThermalPalette.VerdictColor(score));
        }
        else
        {
            // Power state: a fact, not a health, so it carries no bar and no verdict color.
            // A measured difference (+38%, -22%) reads in steel, the one cool hue in the
            // app, reserved for "this moved, and it isn't a fault"; a machine drawing its
            // baseline watts just says MATCHED, dim, like the fan readout.
            Value = aspect.Status;
            HasBar = false;
            ValueBrush = aspect.Status == "MATCHED" ? DimBrush : CoolBrush;
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>One instrument readout in the dashboard hero's stat strip: a numeral, a
/// tracked caption under it, and the full sentence as tooltip. Rebuilt wholesale on
/// every score pass, so it carries no change notification of its own.</summary>
public sealed class HeroStatViewModel
{
    public string Value { get; }
    public string Caption { get; }
    public Brush Brush { get; }
    public string Detail { get; }

    public HeroStatViewModel(string value, string caption, Brush brush, string detail)
    {
        Value = value;
        Caption = caption;
        if (brush.CanFreeze) brush.Freeze();
        Brush = brush;
        Detail = detail;
    }
}

/// <summary>One column of the health matrix (an aspect), with a CPU and a GPU cell.</summary>
public sealed class AspectColumnViewModel
{
    public HealthAspect Aspect { get; }
    public string Label { get; }
    public AspectCellViewModel Cpu { get; } = new();
    public AspectCellViewModel Gpu { get; } = new();

    public AspectColumnViewModel(HealthAspect aspect, string label)
    {
        Aspect = aspect;
        Label = label;
    }
}

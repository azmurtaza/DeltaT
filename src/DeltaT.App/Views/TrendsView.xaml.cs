using System.Windows;
using System.Windows.Controls;
using DeltaT.App.ViewModels;

namespace DeltaT.App.Views;

public partial class TrendsView : UserControl
{
    public TrendsView()
    {
        InitializeComponent();
    }

    private TrendsViewModel? Vm => DataContext as TrendsViewModel;

    private void OnKind0(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SelectedKindIndex = 0; }
    private void OnKind1(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SelectedKindIndex = 1; }
    private void OnKind2(object sender, RoutedEventArgs e) { if (Vm is { } vm) vm.SelectedKindIndex = 2; }

    /// <summary>Programmatic tab selection (used by the --uishot capture harness):
    /// checks the real segment buttons so the highlight and the data stay in step.</summary>
    public void Select(int kind, string range)
    {
        (kind switch { 1 => KindGpu, 2 => KindSsd, _ => KindCpu }).IsChecked = true;
        (range switch { "7d" => Range7d, "30d" => Range30d, "all" => RangeAll, _ => Range24h }).IsChecked = true;
        Vm?.SetRangeCommand.Execute(range);
    }
}

using System.Windows;
using System.Windows.Controls;
using Kelvin.App.ViewModels;

namespace Kelvin.App.Views;

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
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kelvin.App.ViewModels;

namespace Kelvin.App.Views;

public partial class SettingsView : UserControl
{
    private bool _initialized;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncIntervalPills();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void SyncIntervalPills()
    {
        if (Vm is not { } vm) return;
        _initialized = false;
        (vm.SampleIntervalSeconds switch
        {
            1 => Int1, 3 => Int3, 5 => Int5, _ => Int2,
        }).IsChecked = true;
        _initialized = true;
    }

    private void SetInterval(int seconds)
    {
        if (_initialized && Vm is { } vm)
            vm.SampleIntervalSeconds = seconds;
    }

    private void OnInterval1(object sender, RoutedEventArgs e) => SetInterval(1);
    private void OnInterval2(object sender, RoutedEventArgs e) => SetInterval(2);
    private void OnInterval3(object sender, RoutedEventArgs e) => SetInterval(3);
    private void OnInterval5(object sender, RoutedEventArgs e) => SetInterval(5);

    private void OnCityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm && vm.SearchCityCommand.CanExecute(null))
            vm.SearchCityCommand.Execute(null);
    }
}

using System.ComponentModel;
using System.Windows;
using DeltaT.App.ViewModels;

namespace DeltaT.App.Views;

public partial class FingerprintWindow : Window
{
    public FingerprintWindow()
    {
        InitializeComponent();
        Ui.DarkTitleBar.Apply(this);
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        (DataContext as FingerprintViewModel)?.CancelIfRunning();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnCloseChrome(object sender, RoutedEventArgs e) => Close();
}

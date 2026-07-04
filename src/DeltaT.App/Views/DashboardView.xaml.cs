using System.Windows;
using System.Windows.Controls;

namespace DeltaT.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnRunFingerprint(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).OpenFingerprintWindow();
}

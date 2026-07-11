using System.Windows;

namespace DeltaT.App.Views;

public partial class FeedbackWindow : Window
{
    public FeedbackWindow()
    {
        InitializeComponent();
    }

    private void OnCloseChrome(object sender, RoutedEventArgs e) => Close();
}

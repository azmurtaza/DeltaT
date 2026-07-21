using System.Windows;
using DeltaT.Core.Updates;

namespace DeltaT.App.Views;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(WhatsNewRelease release)
    {
        InitializeComponent();
        DataContext = release;
        Version v = release.Version;
        TitleText.Text = $"What's new in v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        IntroText.Text = release.Intro;
    }

    private void OnCloseChrome(object sender, RoutedEventArgs e) => Close();

    /// <summary>Close the popup and open the feedback reporter preset to "idea", so a user who
    /// thinks a feature should have shipped can ask for it straight from the what's-new screen.</summary>
    private void OnRequestFeature(object sender, RoutedEventArgs e)
    {
        Close();
        ((App)Application.Current).OpenFeedbackWindow(asIdea: true);
    }
}

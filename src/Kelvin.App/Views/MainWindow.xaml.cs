using System.Windows;
using Kelvin.App.ViewModels;

namespace Kelvin.App.Views;

public partial class MainWindow : Window
{
    private DashboardView? _dashboard;
    private TrendsView? _trends;
    private RemarksView? _remarks;
    private SettingsView? _settings;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        if (vm.IsFirstRun)
        {
            NavPanel.IsEnabled = false;
            var onboarding = new OnboardingView { DataContext = vm.Onboarding };
            vm.Onboarding.Completed += () =>
            {
                NavPanel.IsEnabled = true;
                ShowDashboard();
            };
            ContentHost.Content = onboarding;
        }
        else if (ContentHost.Content is null)
        {
            ShowDashboard();
        }
    }

    private void ShowDashboard()
    {
        if (Vm is not { } vm) return;
        _dashboard ??= new DashboardView { DataContext = vm };
        ContentHost.Content = _dashboard;
    }

    private void OnNavDashboard(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ShowDashboard();
    }

    private void OnNavTrends(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _trends ??= new TrendsView { DataContext = vm.Trends };
        ContentHost.Content = _trends;
        _ = vm.Trends.RefreshAsync();
    }

    private void OnNavRemarks(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _remarks ??= new RemarksView { DataContext = vm.RemarksFeed };
        ContentHost.Content = _remarks;
        _ = vm.RemarksFeed.RefreshAsync();
    }

    private void OnNavSettings(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _settings ??= new SettingsView { DataContext = vm.Settings };
        ContentHost.Content = _settings;
        vm.Settings.RefreshInfo();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

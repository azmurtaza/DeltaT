using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DeltaT.App.ViewModels;

namespace DeltaT.App.Views;

public partial class MainWindow : Window
{
    private DashboardView? _dashboard;
    private TrendsView? _trends;
    private DeviceView? _device;
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

    /// <summary>Programmatic navigation (used by the --uishot capture harness).</summary>
    public void NavigateTo(string page)
    {
        RadioButton target = page switch
        {
            "trends" => NavTrends,
            "device" => NavDevice,
            "remarks" => NavRemarks,
            "settings" => NavSettings,
            _ => NavDashboard,
        };
        target.IsChecked = true;
    }

    /// <summary>Show the Trends page on a specific kind/range (--uishot harness).</summary>
    public void SelectTrends(int kind, string range)
    {
        NavTrends.IsChecked = true; // creates + presents the view on first call
        _trends?.Select(kind, range);
    }

    /// <summary>Views crossfade in over 140 ms — the one transition in the app.</summary>
    private void Present(FrameworkElement view)
    {
        ContentHost.Content = view;
        var fade = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(140));
        ContentHost.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowDashboard()
    {
        if (Vm is not { } vm) return;
        _dashboard ??= new DashboardView { DataContext = vm };
        Present(_dashboard);
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
        Present(_trends);
        _ = vm.Trends.RefreshAsync();
    }

    private void OnNavDevice(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _device ??= new DeviceView { DataContext = vm.Device };
        Present(_device);
        vm.Device.Refresh();
    }

    private void OnNavRemarks(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _remarks ??= new RemarksView { DataContext = vm.RemarksFeed };
        Present(_remarks);
        _ = vm.RemarksFeed.RefreshAsync();
    }

    private void OnNavSettings(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        _settings ??= new SettingsView { DataContext = vm.Settings };
        Present(_settings);
        vm.Settings.RefreshInfo();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

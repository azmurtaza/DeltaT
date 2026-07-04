using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Kelvin.App.Services;
using Kelvin.App.ViewModels;
using Kelvin.App.Views;
using Kelvin.Core.Knowledge;
using Kelvin.Core.Machine;
using Kelvin.Core.Monitoring;
using Kelvin.Core.Scoring;
using Kelvin.Core.Storage;
using Kelvin.Core.Weather;

namespace Kelvin.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    private KelvinDb? _db;
    private SettingsStore? _settings;
    private TelemetryRepository? _repo;
    private MonitoringService? _monitor;
    private TelemetryPipeline? _pipeline;
    private AmbientService? _ambient;
    private ScoreCoordinator? _scores;
    private RemarksCoordinator? _remarks;
    private TrayManager? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private DispatcherTimer? _scoreTimer;
    private bool _quitting;
    private bool _trayHintShown;

    public static bool IsSimulated { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool simulate = e.Args.Any(a => a.StartsWith("--simulate", StringComparison.OrdinalIgnoreCase));
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        bool noElevate = e.Args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase);
        IsSimulated = simulate;

        // One Kelvin at a time; a second launch just surfaces the first.
        _mutex = new Mutex(true, @"Local\Kelvin.App.Singleton", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Kelvin.App.Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        // CPU temperature registers need the kernel driver → admin. Relaunch
        // elevated unless we're simulating (or explicitly told not to).
        if (!simulate && !noElevate && !IsElevated())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = string.Join(' ', e.Args),
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Shutdown();
                return;
            }
            catch (Win32Exception)
            {
                // UAC declined — run with whatever sensors we can see (GPU, battery).
            }
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log("dispatcher", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log("domain", ex.ExceptionObject as Exception);

        try
        {
            ComposeAndStart(simulate, e.Args);
        }
        catch (Exception ex)
        {
            Log("startup", ex);
            MessageBox.Show(
                $"Kelvin couldn't start its sensor engine.\n\n{ex.Message}\n\nDetails were written to {LogPath}.",
                "Kelvin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (!minimized)
            ShowMainWindow();
    }

    private void ComposeAndStart(bool simulate, string[] args)
    {
        _db = new KelvinDb(simulate ? SimulatedDbPath() : null);
        _settings = new SettingsStore(_db);
        _repo = new TelemetryRepository(_db);
        _ambient = new AmbientService(_settings);

        MachineIdentity machine = MachineIdentityProvider.Detect();
        bool hasDiscreteGpu = machine.GpuNames.Any(n =>
            n.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || n.Contains("geforce", StringComparison.OrdinalIgnoreCase)
            || n.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || n.Contains("rtx", StringComparison.OrdinalIgnoreCase)
            || n.Contains("gtx", StringComparison.OrdinalIgnoreCase)
            || n.Contains("arc", StringComparison.OrdinalIgnoreCase));
        ThermalProfile profile = ThermalProfileProvider.Resolve(machine, hasDiscreteGpu);

        ISensorSource source = simulate
            ? new SimulatedSensorSource(ParseScenario(args), ambient: () => _ambient.CurrentAmbientC ?? 32)
            : new HardwareSensorSource();

        int intervalSeconds = Math.Clamp(_settings.GetInt(SettingsKeys.SampleIntervalSeconds) ?? 2, 1, 10);
        _monitor = new MonitoringService(source, TimeSpan.FromSeconds(intervalSeconds));
        _pipeline = new TelemetryPipeline(_monitor, _ambient, _repo);
        _scores = new ScoreCoordinator(_repo, _settings, profile, () => _monitor.Latest, FormatTemp);
        _remarks = new RemarksCoordinator(_monitor, _ambient, _repo, _scores, _settings);

        _vm = new MainViewModel(machine, profile, _monitor, _ambient, _scores) { Simulated = simulate };
        _remarks.RemarkRaised += r => _vm.OnRemark(r);
        _tray = new TrayManager(_monitor, ShowMainWindow, Quit);

        _monitor.Start();
        _ = _ambient.StartAsync();

        // Scores: one early pass (so the dashboard fills in), then every 5 minutes.
        _scoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scoreTimer.Tick += (_, _) =>
        {
            _scoreTimer!.Interval = TimeSpan.FromMinutes(5);
            Task.Run(() =>
            {
                try { _scores!.Compute(DateTimeOffset.UtcNow); }
                catch (Exception ex) { Log("score", ex); }
            });
        };
        _scoreTimer.Start();

        // Second-instance "show me" signal.
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal!,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, executeOnlyOnce: false);
    }

    private static SimScenario ParseScenario(string[] args)
    {
        string? arg = args.FirstOrDefault(a => a.StartsWith("--simulate=", StringComparison.OrdinalIgnoreCase));
        return arg?[11..].ToLowerInvariant() switch
        {
            "aging" => SimScenario.AgingPaste,
            "degraded" => SimScenario.DegradedPaste,
            "dusty" => SimScenario.DustyAirflow,
            _ => SimScenario.Healthy,
        };
    }

    /// <summary>Simulation runs against a separate db so fake telemetry never
    /// pollutes the real machine history.</summary>
    private static string SimulatedDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kelvin", "kelvin-sim.db");

    private string FormatTemp(double celsius) =>
        _settings!.GetBool(SettingsKeys.UnitsFahrenheit, false)
            ? $"{celsius * 9 / 5 + 32:0} °F"
            : $"{celsius:0} °C";

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public void ShowMainWindow()
    {
        if (_vm is null)
            return;
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _vm };
            _window.Closing += OnWindowClosing;
            _window.IsVisibleChanged += (_, _) => _vm.UiVisible = _window.IsVisible;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting || !(_settings?.GetBool(SettingsKeys.CloseToTray, true) ?? true))
            return;
        e.Cancel = true;
        _window!.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowFirstTrayHint();
        }
    }

    public void Quit()
    {
        _quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitting = true;
        _scoreTimer?.Stop();
        _remarks?.Dispose();
        _tray?.Dispose();
        _pipeline?.Dispose(); // flushes buffered telemetry
        try { _monitor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _ambient?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); } catch { }
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kelvin", "kelvin.log");

    private static void Log(string area, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {area}: {ex}\n");
        }
        catch { }
    }
}

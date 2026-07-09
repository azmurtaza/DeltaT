using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeltaT.App.Services;
using DeltaT.App.ViewModels;
using DeltaT.App.Views;
using DeltaT.Core.Diagnostics;
using DeltaT.Core.Knowledge;
using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using DeltaT.Core.Remarks;
using DeltaT.Core.Scoring;
using DeltaT.Core.Storage;
using DeltaT.Core.Updates;
using DeltaT.Core.Weather;

namespace DeltaT.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    private DeltaTDb? _db;
    private SettingsStore? _settings;
    private TelemetryRepository? _repo;
    private MonitoringService? _monitor;
    private TelemetryPipeline? _pipeline;
    private AmbientService? _ambient;
    private ScoreCoordinator? _scores;
    private RemarksCoordinator? _remarks;
    private UpdateService? _updates;
    private TrayManager? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private DispatcherTimer? _scoreTimer;
    private bool _quitting;
    private bool _trayHintShown;
    private string? _uishotDir;

    public static bool IsSimulated { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DeltaTDb.MigrateLegacyStore();

        bool simulate = e.Args.Any(a => a.StartsWith("--simulate", StringComparison.OrdinalIgnoreCase));
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        bool noElevate = e.Args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase);
        _uishotDir = e.Args.FirstOrDefault(a => a.StartsWith("--uishot=", StringComparison.OrdinalIgnoreCase))?[9..];
        IsSimulated = simulate;

        // One DeltaT at a time; a second launch just surfaces the first. The
        // screenshot harness is exempt — it runs against a throwaway sim db and
        // must be able to capture alongside a live tray instance.
        _mutex = new Mutex(true, @"Local\DeltaT.App.Singleton", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeltaT.App.Show");
        bool elevatingHandoff = e.Args.Contains("--elevating", StringComparer.OrdinalIgnoreCase);
        if (!isFirst && _uishotDir is null)
        {
            // Normal second launch → just surface the running instance and exit.
            // Elevation handoff is different: the non-elevated instance that spawned
            // this one is exiting to release the singleton, so wait briefly for it to
            // let go and then take over as the primary (elevated) instance. Without
            // this, the elevated relaunch loses the race and the app never comes up
            // elevated — which is exactly how CPU temps silently stay blank.
            if (!(elevatingHandoff && _mutex.WaitOne(TimeSpan.FromSeconds(8))))
            {
                _showSignal.Set();
                Shutdown();
                return;
            }
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
                    Arguments = ElevatedArgs(e.Args),
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Shutdown();
                return;
            }
            catch (Win32Exception)
            {
                // UAC declined — run with whatever sensors we can see (GPU, battery).
                // The dashboard offers a one-click "Restart as administrator" so this
                // is always recoverable without hunting for a shortcut.
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
                $"DeltaT couldn't start its sensor engine.\n\n{ex.Message}\n\nDetails were written to {LogPath}.",
                "DeltaT", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        if (!minimized)
            ShowMainWindow();

        if (_uishotDir is not null)
            _ = CaptureUiShotsAsync(_uishotDir);
    }

    /// <summary>Dev-only (`--uishot=DIR`, combine with --simulate): walks every view,
    /// saves PNGs of the rendered window, then exits. Lets UI changes be verified
    /// without a human clicking through the app.</summary>
    private async Task CaptureUiShotsAsync(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            await Task.Delay(TimeSpan.FromSeconds(8)); // sim sensors + sparklines need a few samples

            // 2× supersample so the vector UI is crisp enough to post full-size.
            const double scale = 2.0;
            async Task Shot(FrameworkElement visual, string name)
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                await Task.Delay(450);
                var bmp = new RenderTargetBitmap(
                    (int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bmp.Render(visual);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using FileStream fs = File.Create(Path.Combine(dir, name + ".png"));
                enc.Save(fs);
            }

            MainWindow win = _window!;

            // Score the seeded history so the dashboard shows a real verdict (the
            // periodic score timer wouldn't fire before we capture), and surface the
            // freshest seeded remark on the dashboard ticker.
            try { _scores?.Compute(DateTimeOffset.UtcNow); } catch (Exception ex) { Log("uishot-score", ex); }
            await Task.Delay(500);
            try
            {
                if (_repo!.GetEvents("remark", 0, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1) is [{ } ev, ..])
                    _vm!.OnRemark(new Remark("seed", DateTimeOffset.FromUnixTimeSeconds(ev.Ts),
                        (RemarkSeverity)Math.Clamp(ev.Severity, 0, 3), ev.Message, null));
            }
            catch (Exception ex) { Log("uishot-remark", ex); }

            win.NavigateTo("dashboard");
            await Shot(win, "dashboard");

            // Trends: several kinds/ranges to show the graph's range on one machine.
            async Task ShotTrends(int kind, string range, string name)
            {
                win.SelectTrends(kind, range);
                for (int i = 0; i < 40 && _vm!.Trends.Loading; i++)
                    await Task.Delay(50);
                await _vm!.Trends.RefreshAsync();
                await Task.Delay(500);
                await Shot(win, name);
            }
            await ShotTrends(0, "24h", "trends_cpu_24h");
            await ShotTrends(1, "7d", "trends_gpu_7d");
            await ShotTrends(0, "30d", "trends_cpu_30d");
            await ShotTrends(2, "24h", "trends_ssd_24h");

            foreach (string page in new[] { "device", "remarks", "settings" })
            {
                win.NavigateTo(page);
                if (page == "remarks") await _vm!.RemarksFeed.RefreshAsync();
                await Shot(win, page);
            }

            // Onboarding renders offscreen (the real one only exists on first run).
            var onboarding = new Border
            {
                Background = (Brush)FindResource("Brush.Bg"),
                Child = new OnboardingView { DataContext = _vm!.Onboarding },
            };
            onboarding.Measure(new Size(1000, 680));
            onboarding.Arrange(new Rect(0, 0, 1000, 680));
            onboarding.UpdateLayout();
            await Shot(onboarding, "onboarding");

            var fpVm = new FingerprintViewModel(
                new FingerprintTest(_monitor!, _ambient!), _repo!, onBattery: false);
            var fp = new FingerprintWindow { DataContext = fpVm };
            fp.Show();
            await Shot(fp, "fingerprint");
            fp.Close();
        }
        catch (Exception ex)
        {
            Log("uishot", ex);
        }
        finally
        {
            Quit();
        }
    }

    private void ComposeAndStart(bool simulate, string[] args)
    {
        _db = new DeltaTDb(simulate ? SimulatedDbPath() : null);
        _settings = new SettingsStore(_db);
        if (_uishotDir is not null)
            _settings.SetBool(SettingsKeys.FirstRunDone, true); // uishot wants the real screens, not onboarding
        _repo = new TelemetryRepository(_db);
        // Dev/demo screenshots (`--seed=healthy|repaste|provisional`): lay down a
        // realistic multi-week history before anything else reads the store.
        if (ParseSeed(args) is { } seed)
            DemoSeeder.Seed(_db, _repo, _settings,
                degraded: seed is "repaste" or "degraded" or "aging",
                DateTimeOffset.UtcNow,
                provisional: seed == "provisional");
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

        ISensorSource source;
        if (simulate)
        {
            source = new SimulatedSensorSource(ParseScenario(args), ambient: () => _ambient.CurrentAmbientC ?? 32,
                warmDemo: _uishotDir is not null);
        }
        else
        {
            var hardware = new HardwareSensorSource();
            // Watchdog notes (sensor stalls, engine reopens) go to the log and the
            // events feed — a self-healed anomaly should leave a visible trace.
            hardware.Diagnostic += msg =>
            {
                Log("sensors", new InvalidOperationException(msg));
                try { _repo?.InsertEvent(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "system", null, null, 1, $"Sensor engine: {msg}"); }
                catch { }
            };
            source = hardware;
        }

        int intervalSeconds = Math.Clamp(_settings.GetInt(SettingsKeys.SampleIntervalSeconds) ?? 2, 1, 10);
        _monitor = new MonitoringService(source, TimeSpan.FromSeconds(intervalSeconds));
        _pipeline = new TelemetryPipeline(_monitor, _ambient, _repo);
        _scores = new ScoreCoordinator(_repo, _settings, profile, () => _monitor.Latest, FormatTemp);
        _remarks = new RemarksCoordinator(_monitor, _ambient, _repo, _scores, _settings);

        var trends = new TrendsViewModel(_repo);
        var remarksFeed = new RemarksViewModel(_repo);
        _updates = new UpdateService(_settings);
        var settingsVm = new SettingsViewModel(_settings, _ambient, _scores, machine, profile, _db,
            _updates, () => _monitor.Latest, simulate);
        var deviceVm = new DeviceViewModel(machine, profile, () => _monitor.Latest, _ambient, _settings);
        var onboarding = new OnboardingViewModel(_settings, _ambient, machine);

        _vm = new MainViewModel(machine, profile, _monitor, _ambient, _scores, _settings,
            trends, remarksFeed, settingsVm, deviceVm, onboarding)
        { Simulated = simulate, Elevated = simulate || IsElevated(), RequestElevation = RelaunchAsAdmin };
        if (args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            _vm.UiVisible = false; // tray start: no window yet, skip card churn
        _tray = new TrayManager(_monitor, ShowMainWindow, Quit);

        _remarks.RemarkRaised += r =>
        {
            _vm.OnRemark(r);
            if (r.Severity >= RemarkSeverity.Warning
                && _settings.GetBool(SettingsKeys.NotificationsEnabled, true)
                && _window?.IsVisible != true)
            {
                _tray.ShowRemarkToast(r);
            }
        };

        _monitor.Start();
        // Screenshot runs keep the seeded weather; a live fetch would overwrite it
        // (and possibly shift the ambient band) mid-capture.
        if (_uishotDir is null)
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

        // Keep installs current: check GitHub shortly after launch and self-update if a
        // newer release is out (unless the user opted out). Never during sim/screenshots.
        if (!simulate && _uishotDir is null && _updates.AutoUpdateEnabled)
            _ = CheckForUpdatesOnStartupAsync();
    }

    /// <summary>Background one-shot: if a newer release exists, download it and hand off
    /// to the installer, which restarts DeltaT. Any failure is swallowed - a missed
    /// update must never stop the app from running.</summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6)); // let startup settle first
            ReleaseInfo? release = await _updates!.CheckAsync();
            if (release is null)
                return;

            string? installer = await _updates.DownloadAsync(release);
            if (installer is null)
                return;

            Dispatcher.Invoke(() => _tray?.ShowInfo("DeltaT is updating",
                $"Installing {release.Tag}. DeltaT will restart in a moment."));
            await Task.Delay(TimeSpan.FromSeconds(2)); // give the toast a beat to show
            _updates.ApplyAndRelaunch(installer);
        }
        catch (Exception ex)
        {
            Log("update", ex);
        }
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

    /// <summary>`--seed=healthy|repaste|provisional` fills the sim db with demo history
    /// for screenshots. Returns null when absent, else the normalized mode string.</summary>
    private static string? ParseSeed(string[] args)
    {
        string? arg = args.FirstOrDefault(a => a.StartsWith("--seed", StringComparison.OrdinalIgnoreCase));
        if (arg is null)
            return null;
        return arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..].ToLowerInvariant() : "healthy";
    }

    /// <summary>Simulation runs against a separate db so fake telemetry never
    /// pollutes the real machine history.</summary>
    private static string SimulatedDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaT", "deltat-sim.db");

    private string FormatTemp(double celsius) =>
        _settings!.GetBool(SettingsKeys.UnitsFahrenheit, false)
            ? $"{celsius * 9 / 5 + 32:0} °F"
            : $"{celsius:0} °C";

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Command line for an elevated relaunch: the original args plus the
    /// <c>--elevating</c> marker, so the new instance knows to wait out the singleton
    /// this one is about to release instead of bouncing off it as a "second launch".</summary>
    private static string ElevatedArgs(string[] args) => string.Join(' ',
        args.Contains("--elevating", StringComparer.OrdinalIgnoreCase) ? args : args.Append("--elevating"));

    /// <summary>User asked to unlock CPU sensors after declining (or never getting) the
    /// UAC prompt: spawn an elevated copy and step aside so it becomes the primary
    /// instance. If UAC is declined again, nothing changes.</summary>
    public void RelaunchAsAdmin()
    {
        if (IsSimulated || IsElevated())
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = ElevatedArgs(Environment.GetCommandLineArgs().Skip(1).ToArray()),
                UseShellExecute = true,
                Verb = "runas",
            });
            Quit(); // release the singleton so the elevated instance takes over cleanly
        }
        catch (Win32Exception)
        {
            // UAC declined again — leave the running instance as it is.
        }
    }

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

    public void OpenFingerprintWindow()
    {
        if (_monitor is null || _ambient is null || _repo is null)
            return;
        var vm = new FingerprintViewModel(
            new FingerprintTest(_monitor, _ambient),
            _repo,
            onBattery: _monitor.Latest is { OnAcPower: false });
        new FingerprintWindow { DataContext = vm, Owner = _window }.ShowDialog();
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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaT", "deltat.log");

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

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
    private FeedbackService? _feedback;
    private TrayManager? _tray;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private DispatcherTimer? _scoreTimer;
    private bool _quitting;
    private bool _trayHintShown;
    private string? _uishotDir;
    private DeltaT.Core.Updates.WhatsNewRelease? _pendingWhatsNew;

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

    // TEMP helper for the full-height what's-new shot.
    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (T d in FindVisuals<T>(c)) yield return d;
        }
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

            // Compare mode: overlay the last 7 days against the prior 7 (the seed spans 30).
            win.SelectTrends(0, "7d");
            _vm!.Trends.EnterCompareCommand.Execute(null);
            _vm.Trends.SetComparePresetCommand.Execute("7d");
            for (int i = 0; i < 40 && _vm.Trends.Loading; i++) await Task.Delay(50);
            await _vm.Trends.RefreshAsync();
            await Task.Delay(500);
            await Shot(win, "trends_compare");

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

            var fpTest = new FingerprintTest(_monitor!, _ambient!);
            var fpVm = new FingerprintViewModel(
                fpTest, new FingerprintSequence(fpTest, _monitor!), _repo!,
                cpuLoad: () => _monitor!.Latest?.Find(ComponentKind.Cpu)?.LoadPercent,
                onBattery: false, hasGpu: true,
                boutProgress: () => (2, 3)); // demo values so the shot captures the tracker
            var fp = new FingerprintWindow { DataContext = fpVm };
            fp.Show();
            await Shot(fp, "fingerprint");

            // Also capture the live "in progress" state (the load phase with the ring
            // gauge tracking CPU temp) — drive the VM straight there instead of waiting
            // out the real 3-minute test. Shown mid-workup so the step badge appears.
            fpVm.State = "running";
            fpVm.InSequence = true;
            fpVm.StepBadge = "TEST 1 OF 2 · CPU";
            fpVm.Phase = "Full CPU load, let it cook";
            fpVm.PhaseHint = "96 s remaining in this phase";
            fpVm.PhaseIndex = 1;
            fpVm.GaugeValue = 82.4;
            fpVm.HasGaugeValue = true;
            await Shot(fp, "fingerprint_running");

            // The calibration workout, mid-run: same running chrome, its own two-leg strip and
            // a load-percent gauge.
            fpVm.Mode = "workout";
            fpVm.InSequence = false;
            fpVm.StepBadge = "";
            fpVm.RunningOverline = "Calibration workout in progress";
            fpVm.StopLabel = "STOP WORKOUT";
            fpVm.Phase = "Holding a heavy load";
            fpVm.PhaseHint = "2:40 left in this workout";
            fpVm.PhaseIndex = 1;
            fpVm.GaugeUnit = "%";
            fpVm.GaugeSub = "TARGET 80%";
            fpVm.GaugeValue = 79;
            fpVm.HasGaugeValue = true;
            await Shot(fp, "workout_running");
            fpVm.Mode = "fingerprint";

            // The workup done screen: one result section per component.
            fpVm.State = "done";
            fpVm.Sections.Clear();
            fpVm.Sections.Add(new FingerprintSection("CPU FINGERPRINT", new StatCell[]
            {
                new("SUSTAINED", "84.2°"), new("PEAK", "91.0°"), new("SOAK RATE", "3.1°/min"),
                new("Δ OUTSIDE", "+62.8°"), new("THROTTLING", "none"),
            }, "Rerun monthly. A steady climb points to cooling wearing down.",
            new FingerprintComparison("+2.4°", "hotter", "RUNNING HOTTER", "VS JUN 12 · WEATHER-CORRECTED")));
            fpVm.Sections.Add(new FingerprintSection("GPU FINGERPRINT", new StatCell[]
            {
                new("SUSTAINED", "71.4°"), new("PEAK", "74.0°"), new("SOAK RATE", "2.6°/min"),
                new("Δ OUTSIDE", "+50.1°"), new("THROTTLING", "none"),
            }, "First GPU fingerprint recorded. Rerun it monthly (plugged in, similar room) and DeltaT will chart the drift."));
            await Shot(fp, "fingerprint_workup");

            fp.Close();

            // Support panel (a normal modal dialog otherwise): render it offscreen for a shot.
            var donate = new Views.DonateWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
            donate.Show();
            await Shot(donate, "donate");
            donate.Close();

            // What's-new popup: same offscreen render, seeded with the newest curated release.
            if (Core.Updates.WhatsNewNotes.Releases.Count > 0)
            {
                var whatsNew = new Views.WhatsNewWindow(Core.Updates.WhatsNewNotes.Releases[^1])
                { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
                whatsNew.Show();
                // Capture the real window exactly as a user sees it, at each scroll page from
                // top to bottom, so a few shots cover the whole list.
                await Task.Delay(300);
                ScrollViewer? wnScroll = FindVisuals<ScrollViewer>(whatsNew).FirstOrDefault();
                await Shot(whatsNew, "whatsnew_1");
                if (wnScroll is not null)
                {
                    int page = 2;
                    double step = wnScroll.ViewportHeight - 24;
                    while (wnScroll.VerticalOffset < wnScroll.ScrollableHeight - 1)
                    {
                        wnScroll.ScrollToVerticalOffset(wnScroll.VerticalOffset + step);
                        await Dispatcher.Yield(DispatcherPriority.Render);
                        await Task.Delay(250);
                        await Shot(whatsNew, "whatsnew_" + page++);
                    }
                }
                whatsNew.Close();
            }
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
        // Dev/demo screenshots (`--seed=healthy|repaste|provisional|overclock|undervolt`):
        // lay down a realistic multi-week history before anything else reads the store.
        if (ParseSeed(args) is { } seed)
            DemoSeeder.Seed(_db, _repo, _settings,
                degraded: seed is "repaste" or "degraded" or "aging",
                DateTimeOffset.UtcNow,
                provisional: seed == "provisional",
                overclock: seed is "overclock" or "boost",
                lightCpu: seed == "mixed",
                undervolt: seed == "undervolt");
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
        // Honour a user who paused background capture (frame-stutter concerns during
        // gaming). The screenshot harness always samples so the demo views fill in.
        if (_uishotDir is null)
            _monitor.IsPaused = !_settings.GetBool(SettingsKeys.CaptureEnabled, true);
        _pipeline = new TelemetryPipeline(_monitor, _ambient, _repo);
        _scores = new ScoreCoordinator(_repo, _settings, profile, () => _monitor.Latest, FormatTemp);
        _remarks = new RemarksCoordinator(_monitor, _ambient, _repo, _scores, _settings);

        var trends = new TrendsViewModel(_repo);
        var remarksFeed = new RemarksViewModel(_repo);
        _updates = new UpdateService(_settings);
        _feedback = new FeedbackService(machine, IsElevated());
        var settingsVm = new SettingsViewModel(_settings, _ambient, _scores, machine, profile, _db,
            _updates, _monitor, () => _monitor.Latest, simulate, showUpdatesPanel: _uishotDir is not null);
        var deviceVm = new DeviceViewModel(machine, profile, () => _monitor.Latest, _ambient, _settings);
        var onboarding = new OnboardingViewModel(_settings, _ambient, machine);

        _vm = new MainViewModel(machine, profile, _monitor, _ambient, _scores, _settings,
            trends, remarksFeed, settingsVm, deviceVm, onboarding)
        { Simulated = simulate, Elevated = simulate || IsElevated(), RequestElevation = RelaunchAsAdmin };
        if (args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            _vm.UiVisible = false; // tray start: no window yet, skip card churn
        _tray = new TrayManager(_monitor, ShowMainWindow, Quit, ShowRemarks);

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

        // Fill the score dials the instant the first sensor snapshot lands, so a reinstall
        // shows its already-learned calibration as fast as it shows trends - not blank at 0%
        // until the periodic timer's first tick (up to 30 s of staring at "CAL 0%"). One-shot:
        // it unsubscribes itself after the first compute.
        void ComputeOnFirstSnapshot(SensorSnapshot _)
        {
            _monitor!.SnapshotCaptured -= ComputeOnFirstSnapshot;
            Task.Run(() =>
            {
                try { _scores!.Compute(DateTimeOffset.UtcNow); }
                catch (Exception ex) { Log("score", ex); }
            });
        }
        _monitor.SnapshotCaptured += ComputeOnFirstSnapshot;

        _monitor.Start();
        // Screenshot runs keep the seeded weather; a live fetch would overwrite it
        // (and possibly shift the ambient band) mid-capture.
        if (_uishotDir is null)
            _ = _ambient.StartAsync();

        // Scores: one early pass (so the dashboard fills in), then an adaptive cadence.
        // While any component is still calibrating we recompute often so the calibration
        // meter visibly climbs as load lands; once everything is locked a score barely
        // changes between passes, so we back off to the cheap 5-minute cadence.
        _scoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scoreTimer.Tick += (_, _) =>
        {
            Task.Run(() =>
            {
                try { _scores!.Compute(DateTimeOffset.UtcNow); }
                catch (Exception ex) { Log("score", ex); }
            });
            bool calibrating = _scores!.Latest.Count == 0
                || _scores.Latest.Values.Any(s => !s.Scored);
            _scoreTimer!.Interval = calibrating ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);
        };
        _scoreTimer.Start();

        // Second-instance "show me" signal.
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal!,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, executeOnlyOnce: false);

        // Keep installs current: check GitHub shortly after launch and self-update if a
        // newer release is out (unless the user opted out). Never during sim/screenshots.
        if (!simulate && _uishotDir is null && _updates.AutoUpdateEnabled)
            _ = CheckForUpdatesOnStartupAsync();

        // What's-new: decide once, now (before onboarding can flip FirstRunDone), so a fresh
        // install is correctly seen as first-run and never gets a changelog. Record the
        // running version regardless of the outcome, so the popup can only ever fire once per
        // version. The window itself is shown the first time the user opens the main window
        // (see ShowMainWindow), so a silent tray-start login doesn't pop it over nothing.
        if (!simulate && _uishotDir is null)
        {
            Version running = UpdateService.CurrentVersion;
            _pendingWhatsNew = Core.Updates.WhatsNewGate.Evaluate(
                running,
                _settings.Get(SettingsKeys.WhatsNewShownVersion),
                firstRun: !_settings.GetBool(SettingsKeys.FirstRunDone, false));
            _settings.Set(SettingsKeys.WhatsNewShownVersion, Core.Updates.WhatsNewGate.VersionKey(running));
        }
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

    /// <summary>`--seed=healthy|repaste|provisional|overclock|undervolt` fills the sim db
    /// with demo history for screenshots. Returns null when absent, else the normalized
    /// mode string.</summary>
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

        // First time the window is actually up this session, surface the what's-new popup if
        // this launch upgraded into a version with notes. Deferred so ShowMainWindow returns
        // first, and one-shot (the field is cleared), so it can't stack.
        if (_pendingWhatsNew is { } release)
        {
            _pendingWhatsNew = null;
            Dispatcher.BeginInvoke(() =>
            {
                try { new WhatsNewWindow(release) { Owner = _window }.ShowDialog(); }
                catch (Exception ex) { Log("whatsnew", ex); }
            });
        }
    }

    /// <summary>Open the app straight onto the Remarks feed. The landing spot for a
    /// clicked remark toast: the remark's advice lives there.</summary>
    public void ShowRemarks()
    {
        ShowMainWindow();
        _window?.NavigateTo("remarks");
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
        var test = new FingerprintTest(_monitor, _ambient);
        var vm = new FingerprintViewModel(
            test,
            new FingerprintSequence(test, _monitor),
            _repo,
            cpuLoad: () => _monitor.Latest?.Find(ComponentKind.Cpu)?.LoadPercent,
            onBattery: _monitor.Latest is { OnAcPower: false },
            hasGpu: _monitor.Latest?.Find(ComponentKind.GpuDiscrete) is not null,
            boutProgress: BoutProgress);
        new FingerprintWindow { DataContext = vm, Owner = _window }.ShowDialog();
    }

    /// <summary>Independent loaded CPU bouts banked this epoch, and the target the baseline wants,
    /// for the workout's multi-session tracker. Counts organic gaming bouts too, not just workouts.</summary>
    private (int Bouts, int Target) BoutProgress()
    {
        if (_scores is null || _repo is null)
            return (0, DeltaT.Core.Scoring.BaselineBuilder.MinLoadedSessions);
        string cpuName = _monitor?.Latest?.Find(ComponentKind.Cpu)?.Name ?? "CPU";
        int mode = _scores.ActiveMode;
        long from = _scores.EffectiveEpochStart(mode).ToUnixTimeSeconds();
        long to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int bouts = _repo.CountLoadedSessions(ComponentKind.Cpu, cpuName, onAc: true, from, to,
            DeltaT.Core.Scoring.BaselineBuilder.SessionGapSeconds, mode);
        return (bouts, DeltaT.Core.Scoring.BaselineBuilder.MinLoadedSessions);
    }

    public void OpenFeedbackWindow(bool asIdea = false)
    {
        if (_feedback is null)
            return;
        var vm = new FeedbackViewModel(_feedback);
        if (asIdea)
            vm.IsIdea = true; // preselect the "idea" kind; the user can still switch to "bug"
        new FeedbackWindow { DataContext = vm, Owner = _window }.ShowDialog();
    }

    public void OpenDonateWindow() =>
        new Views.DonateWindow { Owner = _window }.ShowDialog();

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

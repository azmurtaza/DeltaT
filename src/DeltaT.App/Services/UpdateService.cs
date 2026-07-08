using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using DeltaT.Core.Storage;
using DeltaT.Core.Updates;

namespace DeltaT.App.Services;

/// <summary>Keeps DeltaT current with its GitHub releases. It checks once on startup
/// (unless the user turned auto-update off) and on demand from Settings; when a newer
/// build exists it downloads the setup and hands off to a detached PowerShell helper
/// that runs the installer silently and relaunches the app.
///
/// The helper has to be a separate process because the installer force-closes the
/// running DeltaT.App to unlock its files (see installer PrepareToInstall); a helper
/// spawned detached survives that and does the relaunch the silent installer skips.</summary>
public sealed class UpdateService
{
    private readonly SettingsStore _settings;
    private readonly UpdateChecker _checker = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public UpdateService(SettingsStore settings) => _settings = settings;

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    public static string CurrentVersionLabel
    {
        get
        {
            Version v = CurrentVersion;
            return $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public bool AutoUpdateEnabled
    {
        get => _settings.GetBool(SettingsKeys.AutoUpdate, true);
        set => _settings.SetBool(SettingsKeys.AutoUpdate, value);
    }

    public Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default) =>
        _checker.CheckAsync(CurrentVersion, ct);

    /// <summary>Download the setup to a temp file. Returns the path, or null on failure
    /// (network error, or a file too small to be a real installer).</summary>
    public async Task<string?> DownloadAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DeltaT-update");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"DeltaT-Setup-{release.Version}.exe");
            byte[] bytes = await _http.GetByteArrayAsync(release.DownloadUrl, ct);
            if (bytes.Length < 100_000)
                return null;
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Run the setup silently through a detached helper, then relaunch DeltaT and
    /// shut this instance down so the installer can replace the binaries.</summary>
    public void ApplyAndRelaunch(string installerPath)
    {
        string exe = Process.GetCurrentProcess().MainModule?.FileName
                     ?? Path.Combine(AppContext.BaseDirectory, "DeltaT.App.exe");

        // Wait out the install, pause a beat for file handles to release, then relaunch
        // into the tray. Single-quoted paths so spaces (Program Files) are safe.
        string command =
            $"Start-Process -Wait -FilePath '{installerPath}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'; " +
            $"Start-Sleep -Seconds 1; " +
            $"Start-Process -FilePath '{exe}' -ArgumentList '--minimized'";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}

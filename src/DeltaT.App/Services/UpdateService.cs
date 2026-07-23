using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>The certificate subject DeltaT's own setup is signed with. When this is set,
    /// the updater refuses to run any download not signed by it, which is the only check that
    /// proves a setup came from the maintainer rather than merely arriving over HTTPS.
    ///
    /// EMPTY UNTIL A CODE-SIGNING CERTIFICATE EXISTS. It is deliberately not a "verify if
    /// signed" flag: an attacker would simply ship an unsigned file, so a lenient check is
    /// theatre. Until it is set, authenticity rests on TLS plus the pinned repo owner, and
    /// integrity rests on the digest check below. Set it to e.g. "CN=Azaan Murtaza" the day
    /// the setup is signed, and sign before releasing so no client is left unable to update.</summary>
    public const string ExpectedSigner = "";

    /// <summary>Download the setup to a temp file and prove it is what the release says it is.
    /// Returns the path, or null if anything about it fails to check out.
    ///
    /// This file is about to be executed silently with administrator rights, so "it downloaded
    /// without an exception" is not a standard worth trusting. Three gates: it must be a
    /// plausible size, it must hash to the digest GitHub published for that asset (so a
    /// truncated, corrupted or substituted download dies here), and, once a signing cert
    /// exists, it must carry a valid Authenticode signature from the expected signer.</summary>
    public async Task<string?> DownloadAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        string? path = null;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DeltaT-update");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"DeltaT-Setup-{release.Version}.exe");
            byte[] bytes = await _http.GetByteArrayAsync(release.DownloadUrl, ct);
            if (bytes.Length < 100_000)
                return null;

            // Integrity: the digest GitHub computed for the asset it served us. This catches a
            // mangled or swapped download; it cannot catch a release published by someone who
            // has taken over the account, since the digest would travel with the forgery.
            // Only the signature check can speak to that.
            if (release.Sha256 is { } expected && !HashMatches(bytes, expected))
                return null;

            await File.WriteAllBytesAsync(path, bytes, ct);

            // Authenticity: who signed it. Skipped only while no cert is pinned.
            if (!string.IsNullOrEmpty(ExpectedSigner) && !Authenticode.IsSignedBy(path, ExpectedSigner))
            {
                TryDelete(path);
                return null;
            }

            return path;
        }
        catch
        {
            if (path is not null)
                TryDelete(path);
            return null;
        }
    }

    /// <summary>Constant-time-ish compare of the download against the published digest.</summary>
    private static bool HashMatches(byte[] bytes, string expectedHex)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file, best effort */ }
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

        // Resolve powershell.exe by full path and run from a directory that always exists.
        // With UseShellExecute=false the child inherits our install-dir as its working
        // directory and relies on PATH to find the bare "powershell.exe"; on some machines
        // (a non-default install location, a trimmed PATH) that failed with "the system
        // cannot find the file specified". A fully-qualified exe plus an explicit, guaranteed
        // working directory removes both failure modes.
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            powershell = "powershell.exe";

        Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}

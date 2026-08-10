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

    /// <summary>The setup needs administrator rights (it writes Program Files and registers
    /// the logon task). A helper spawned from a non-elevated DeltaT inherits that token, so
    /// the silent install would sit behind a UAC prompt nobody is there to answer. Checked
    /// before the unattended startup handoff; the manual button in Settings is allowed to
    /// prompt, since the user is standing right there.</summary>
    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>Where the update helper leaves the setup's exit code, so the relaunched app
    /// can tell "installed" from "the installer never ran".</summary>
    private static string ResultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeltaT", "update-result.txt");

    // ---- Loop breaker -----------------------------------------------------------------
    // An install that cannot succeed used to repeat on every launch: check, download 57 MB,
    // hand off, get relaunched by the helper, repeat. The app stayed up for seconds at a
    // time. DeltaT now remembers which version it handed to the installer and stops after
    // UpdateAttemptGate.MaxAttempts if the running version never catches up.

    private Version? AttemptedVersion =>
        _settings.Get(SettingsKeys.UpdateAttemptVersion) is { } s && Version.TryParse(s, out Version? v) ? v : null;

    private int AttemptCount =>
        int.TryParse(_settings.Get(SettingsKeys.UpdateAttemptCount), out int n) ? n : 0;

    /// <summary>Call once at startup: if the version we last handed to the installer is the
    /// one now running, the update landed and the record goes away. Returns the version whose
    /// install FAILED (still recorded, still not running), or null when all is well.</summary>
    public Version? ReconcileLastAttempt()
    {
        Version? attempted = AttemptedVersion;
        if (attempted is null)
            return null;
        if (UpdateAttemptGate.Succeeded(attempted, CurrentVersion))
        {
            ClearAttempt();
            return null;
        }
        return attempted;
    }

    /// <summary>Whether this release may be handed to the installer now, or has already
    /// failed enough times that retrying would just restart the loop.</summary>
    public UpdateAttempt Gate(ReleaseInfo release, DateTimeOffset now) =>
        UpdateAttemptGate.Evaluate(
            AttemptedVersion, AttemptCount, _settings.GetTimestamp(SettingsKeys.UpdateAttemptUtc),
            release.Version, now);

    /// <summary>The setup's exit code from the last handoff, or null if it never reported
    /// one. 0 means the installer itself said it succeeded. Used only to explain a failure.</summary>
    public int? LastInstallerExitCode
    {
        get
        {
            try
            {
                return File.Exists(ResultPath) && int.TryParse(File.ReadAllText(ResultPath).Trim(), out int code)
                    ? code : null;
            }
            catch { return null; }
        }
    }

    /// <summary>One line on why the last update did not take, for Settings and the log.</summary>
    public string DescribeFailure(Version attempted) => LastInstallerExitCode switch
    {
        HelperNotStarted or HelperThrew =>
            $"DeltaT {Label(attempted)} could not be installed: the setup never started, "
            + "which usually means it was blocked by antivirus or the administrator prompt was declined.",
        0 =>
            $"DeltaT {Label(attempted)} reported a successful install but the old version is still running. "
            + "Something is putting the files back, most likely antivirus.",
        { } code =>
            $"DeltaT {Label(attempted)} could not be installed (setup exit code {code}).",
        null =>
            $"DeltaT {Label(attempted)} could not be installed.",
    };

    private const int HelperNotStarted = -1;
    private const int HelperThrew = -2;

    private static string Label(Version v) => $"v{v.Major}.{v.Minor}.{v.Build}";

    /// <summary>Records that this version is about to be handed to the installer. Written
    /// BEFORE the handoff, because the handoff shuts this process down.</summary>
    private void RecordAttempt(Version version)
    {
        Version? attempted = AttemptedVersion;
        int count = attempted == version ? AttemptCount + 1 : 1;
        _settings.Set(SettingsKeys.UpdateAttemptVersion, version.ToString());
        _settings.Set(SettingsKeys.UpdateAttemptCount, count.ToString());
        _settings.SetTimestamp(SettingsKeys.UpdateAttemptUtc, DateTimeOffset.UtcNow);
        TryDelete(ResultPath);
    }

    /// <summary>Forget the attempt history, so the next check starts clean. Used when the
    /// update landed, and when the user asks for one by hand from Settings.</summary>
    public void ClearAttempt()
    {
        _settings.Set(SettingsKeys.UpdateAttemptVersion, "");
        _settings.Set(SettingsKeys.UpdateAttemptCount, "");
        _settings.Set(SettingsKeys.UpdateFailureNoticed, "");
        TryDelete(ResultPath);
    }

    /// <summary>True the first time a given failing version is seen, so the user is told
    /// once rather than on every launch.</summary>
    public bool ShouldAnnounceFailure(Version attempted)
    {
        if (_settings.Get(SettingsKeys.UpdateFailureNoticed) == attempted.ToString())
            return false;
        _settings.Set(SettingsKeys.UpdateFailureNoticed, attempted.ToString());
        return true;
    }

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

            // Already here from a previous attempt and provably the right file? Then don't
            // pull 57 MB again. Only ever reused when the release published a digest we can
            // check it against; an unverifiable leftover is re-downloaded.
            if (release.Sha256 is { } cached && File.Exists(path)
                && HashMatches(await File.ReadAllBytesAsync(path, ct), cached))
                return SignedOrNothing(path);

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
            return SignedOrNothing(path);
        }
        catch
        {
            if (path is not null)
                TryDelete(path);
            return null;
        }
    }

    /// <summary>Authenticity: who signed it. Skipped only while no cert is pinned.</summary>
    private static string? SignedOrNothing(string path)
    {
        if (!string.IsNullOrEmpty(ExpectedSigner) && !Authenticode.IsSignedBy(path, ExpectedSigner))
        {
            TryDelete(path);
            return null;
        }
        return path;
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
    /// shut this instance down so the installer can replace the binaries.
    ///
    /// The attempt is recorded first, because this process is about to end: if the install
    /// does not take, the relaunched app reads that record and refuses to try the same
    /// version forever (see <see cref="UpdateAttemptGate"/>).</summary>
    public void ApplyAndRelaunch(string installerPath, Version version)
    {
        RecordAttempt(version);

        string exe = Process.GetCurrentProcess().MainModule?.FileName
                     ?? Path.Combine(AppContext.BaseDirectory, "DeltaT.App.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);

        // Wait out the install, record what the setup actually said, pause a beat for file
        // handles to release, then relaunch into the tray. The relaunch is unconditional on
        // purpose: DeltaT was force-closed by the installer and the user expects it back
        // whether or not the update worked. What must NOT be unconditional is trying again,
        // which is why the exit code is written down for the next launch to read. A declined
        // administrator prompt or an antivirus block throws instead of returning a code, so
        // that case is caught and recorded too.
        // Single-quoted paths so spaces (Program Files) are safe.
        string command =
            $"$code = {HelperNotStarted}; " +
            $"try {{ $p = Start-Process -Wait -PassThru -FilePath '{installerPath}' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART'; $code = $p.ExitCode }} " +
            $"catch {{ $code = {HelperThrew} }}; " +
            $"try {{ Set-Content -LiteralPath '{ResultPath}' -Value $code -Encoding ascii }} catch {{ }}; " +
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

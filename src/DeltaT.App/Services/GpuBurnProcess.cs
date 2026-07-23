using System.Diagnostics;
using System.IO;
using DeltaT.Core.Diagnostics;

namespace DeltaT.App.Services;

/// <summary>Runs the GPU fingerprint load in a SEPARATE PROCESS instead of on a burn thread
/// inside the app. The load is raw OpenCL against the graphics driver, and on some early
/// drivers that faults hard: a Blackwell RTX 50-series laptop that drives its own display was
/// reported crashing DeltaT every GPU fingerprint. A GPU driver access violation or a TDR
/// reset surfaces as a native corrupted-state exception, which .NET delivers to NO managed
/// catch block, so an in-process burn cannot be guarded, it just kills the app.
///
/// Isolated in a child process, that same fault kills only the child. The parent notices the
/// child died and ends the test with a plain message ("the graphics driver may have reset,
/// updating it usually fixes it") instead of vanishing.
///
/// The child is the very same DeltaT executable relaunched with <c>--gpu-burn</c>
/// (see <see cref="App"/>): it constructs a <see cref="GpuBurner"/>, prints one handshake line,
/// and burns until the parent exits or it is killed. It inherits the parent's (elevated) token,
/// which OpenCL does not need anyway.</summary>
public sealed class GpuBurnProcess : IDisposable
{
    private const string ReadyLine = "BURN-READY";
    private const string ErrorPrefix = "BURN-ERROR:";

    private readonly Process _process;

    /// <summary>Starts the child and blocks until it either confirms the burn is running or
    /// fails to start it. Throws <see cref="InvalidOperationException"/> on failure with the
    /// child's own message when it has one (e.g. "no OpenCL runtime"), so the fingerprint UI
    /// shows the same clean error an in-process burner would have.</summary>
    public GpuBurnProcess(string? gpuName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--gpu-burn");
        psi.ArgumentList.Add("--parent=" + Environment.ProcessId);
        if (!string.IsNullOrWhiteSpace(gpuName))
            psi.ArgumentList.Add("--gpu-name=" + gpuName);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Couldn't start the GPU load process: " + ex.Message, ex);
        }
        if (proc is null)
            throw new InvalidOperationException("Couldn't start the GPU load process.");
        _process = proc;

        // Wait for the handshake line. The child prints BURN-READY once the burner is live, or
        // BURN-ERROR:<message> and exits when it can't create one. A hard native fault at
        // construction just exits the child with no line, which we also treat as a failure.
        // Bounded so a wedged driver (a hang rather than a clean fault) can't freeze the test:
        // the child only has to compile a tiny kernel and start, which is a second or two.
        string? error = null;
        try
        {
            while (ReadLineWithin(TimeSpan.FromSeconds(30)) is { } line)
            {
                if (line.StartsWith(ReadyLine, StringComparison.Ordinal))
                    return; // burning
                if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
                {
                    error = line[ErrorPrefix.Length..].Trim();
                    break;
                }
            }
        }
        catch (IOException) { /* pipe broke = child died: handled below */ }

        // No READY: the child failed or crashed before it could burn. Clean up and report.
        try { if (!_process.HasExited) _process.Kill(); } catch { }
        _process.Dispose();
        throw new InvalidOperationException(error is { Length: > 0 }
            ? error
            : "The GPU load couldn't start. If this machine has a very new GPU, updating the "
              + "graphics driver usually fixes it.");
    }

    /// <summary>A blocking ReadLine that gives up after <paramref name="timeout"/> so a hung
    /// child (as opposed to a cleanly-failing one) can't stall the caller. Returns null on
    /// timeout or end of stream.</summary>
    private string? ReadLineWithin(TimeSpan timeout)
    {
        Task<string?> read = _process.StandardOutput.ReadLineAsync();
        return read.Wait(timeout) ? read.Result : null;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill();
        }
        catch { /* already gone */ }
        try { _process.Dispose(); } catch { }
    }

    /// <summary>The child-process entry point. Called from <see cref="App"/> before any UI when
    /// the app is relaunched with <c>--gpu-burn</c>. Constructs the burner, signals the parent,
    /// then burns until the parent exits or it is killed. Never returns to the WPF startup path.
    /// Any native GPU-driver fault here crashes THIS process only, which is the whole point.</summary>
    public static void RunChild(IReadOnlyList<string> args)
    {
        string? gpuName = Arg(args, "--gpu-name=");
        int parentPid = int.TryParse(Arg(args, "--parent="), out int p) ? p : 0;

        GpuBurner burner;
        try
        {
            burner = new GpuBurner(gpuName);
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(ErrorPrefix + " " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
            Console.Out.Flush();
            Environment.Exit(2);
            return;
        }

        Console.Out.WriteLine(ReadyLine);
        Console.Out.Flush();

        Process? parent = null;
        if (parentPid > 0)
        {
            try { parent = Process.GetProcessById(parentPid); }
            catch { /* parent already gone: fall through and exit, never orphan a burn */ }
        }

        try
        {
            // Burn until the parent goes away. Normal end: the parent kills us on Dispose.
            // Backstop: if the parent process exits (a hard crash that skipped Dispose), stop
            // too so an orphaned burn can't keep cooking the GPU. Only wait forever when no
            // parent was named at all (manual diagnostics), where the burn is meant to run
            // until the process is killed by hand.
            if (parentPid <= 0)
                Thread.Sleep(Timeout.Infinite);
            else
                parent?.WaitForExit();
        }
        catch { }
        finally
        {
            burner.Dispose();
        }
        Environment.Exit(0);
    }

    private static string? Arg(IReadOnlyList<string> args, string prefix) => args
        .FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}

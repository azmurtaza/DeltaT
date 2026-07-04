using System.Diagnostics;

namespace DeltaT.App.Services;

/// <summary>Start-with-Windows via Task Scheduler rather than the Run registry
/// key: a scheduled task can launch elevated at logon without a UAC prompt,
/// which the Run key cannot do for an admin-requiring app.</summary>
public static class AutostartService
{
    private const string TaskName = "DeltaT";
    private const string LegacyTaskName = "Kelvin"; // pre-rename task; exe path in it is dead

    public static bool IsEnabled()
    {
        if (Run($"/Query /TN {TaskName}") == 0)
            return true;
        // Autostart was set up under the old app name — recreate it against the
        // current exe and drop the stale task (needs elevation; harmless if not).
        if (Run($"/Query /TN {LegacyTaskName}") == 0 && Enable())
        {
            Run($"/Delete /TN {LegacyTaskName} /F");
            return true;
        }
        return false;
    }

    public static bool Enable()
    {
        string exe = Environment.ProcessPath!;
        return Run($"/Create /TN {TaskName} /SC ONLOGON /RL HIGHEST /F /TR \"\\\"{exe}\\\" --minimized\"") == 0;
    }

    public static bool Disable() => Run($"/Delete /TN {TaskName} /F") == 0;

    private static int Run(string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            proc!.WaitForExit(10000);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}

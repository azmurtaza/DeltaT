using System.Diagnostics;

namespace Kelvin.App.Services;

/// <summary>Start-with-Windows via Task Scheduler rather than the Run registry
/// key: a scheduled task can launch elevated at logon without a UAC prompt,
/// which the Run key cannot do for an admin-requiring app.</summary>
public static class AutostartService
{
    private const string TaskName = "Kelvin";

    public static bool IsEnabled() => Run($"/Query /TN {TaskName}") == 0;

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

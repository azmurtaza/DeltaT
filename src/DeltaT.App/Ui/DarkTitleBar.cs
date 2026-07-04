using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeltaT.App.Ui;

/// <summary>Windows paints dialog title bars white by default even in dark apps;
/// this flips DWM's immersive dark mode for a window (Win10 20H1+).</summary>
public static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            int enabled = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        };
    }
}

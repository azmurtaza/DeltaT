using System.Runtime.InteropServices;

namespace DeltaT.Core.Monitoring;

/// <summary>System-wide CPU busy percentage from the Windows scheduler's own counters
/// (<c>GetSystemTimes</c>), in microseconds rather than the ~120 ms LibreHardwareMonitor
/// spends producing the same number.
///
/// This exists for the same reason <see cref="NvmlGpuReader"/> does. Elevated (how DeltaT
/// actually runs, since the kernel driver needs it) LHM's CPU update costs ~122 ms per
/// tick: it walks every logical processor reading MSRs with a thread-affinity switch each
/// time. DeltaT needs three things from the CPU each tick - temperature, load and power -
/// and two of them can be had for nothing: temperature from
/// <see cref="CpuMsrTemperatureReader"/> (~1 ms, the same architectural register LHM reads)
/// and load from here. Only package power still needs LHM, and power is a slow enough
/// signal to be refreshed on a long clock.
///
/// The value is the busy fraction across all logical processors since the previous call,
/// which is exactly what LHM's "CPU Total" load reports. The first call has no previous
/// sample to difference against and returns null.</summary>
public sealed class CpuLoadReader
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _primed;

    public double? Read()
    {
        if (!GetSystemTimes(out FileTime idleFt, out FileTime kernelFt, out FileTime userFt))
            return null;

        ulong idle = idleFt.Value, kernel = kernelFt.Value, user = userFt.Value;
        if (!_primed)
        {
            (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
            _primed = true;
            return null; // nothing to difference against yet
        }

        // Kernel time INCLUDES idle time on Windows, so total = kernel + user and busy is
        // whatever wasn't idle.
        ulong idleDelta = idle - _prevIdle;
        ulong totalDelta = kernel - _prevKernel + (user - _prevUser);
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);

        if (totalDelta == 0)
            return null; // two reads inside one clock tick
        double busy = 100.0 * (totalDelta - idleDelta) / totalDelta;
        return Math.Clamp(busy, 0, 100);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}

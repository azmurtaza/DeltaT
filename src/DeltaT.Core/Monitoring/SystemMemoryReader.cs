using System.Runtime.InteropServices;

namespace DeltaT.Core.Monitoring;

/// <summary>Physical RAM usage straight from Windows, with no kernel driver and no
/// LibreHardwareMonitor involvement. <c>GlobalMemoryStatusEx</c> is a plain Win32 call
/// present on every supported Windows version, costs microseconds, and needs no
/// elevation, so it serves two jobs: it is the fallback when LHM exposes no usable
/// memory node (the "DeltaT can't read my RAM" reports), and it is the yardstick that
/// tells LHM's physical-RAM node apart from its pagefile-backed commit-charge node
/// without having to trust a hardware name.</summary>
public static class SystemMemoryReader
{
    /// <summary>Total/used physical RAM in GiB, matching the unit LHM reports so the two
    /// sources are directly comparable. Null if the OS call fails or returns something
    /// implausible (never fake a zero onto the dashboard).</summary>
    public static SystemMemoryReading? TryRead()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        try
        {
            if (!GlobalMemoryStatusEx(ref status))
                return null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }

        const double Gib = 1024d * 1024 * 1024;
        double total = status.ullTotalPhys / Gib;
        double avail = status.ullAvailPhys / Gib;
        if (total <= 0 || avail < 0 || avail > total)
            return null;

        double used = total - avail;
        return new SystemMemoryReading(
            Math.Round(used, 1),
            Math.Round(total, 1),
            Math.Round(used / total * 100, 1));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}

/// <summary>Physical memory in GiB plus the usage percentage those two imply.</summary>
public readonly record struct SystemMemoryReading(double UsedGb, double TotalGb, double LoadPercent);

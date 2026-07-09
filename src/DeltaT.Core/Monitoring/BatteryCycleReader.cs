using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads the battery's charge/discharge cycle count from the ACPI/OEM WMI
/// provider (<c>root\wmi:BatteryCycleCount</c>) — the same figure vendor tools and
/// <c>powercfg /batteryreport</c> surface. LibreHardwareMonitor doesn't expose cycle
/// count, and many laptop firmwares don't populate it at all, so this reads it
/// separately and simply stays dark when it's unavailable.
///
/// Cycle count changes at most once every day or two, so the WMI query runs on a slow
/// background timer and callers just read the cached value; <see cref="CurrentCycles"/>
/// never blocks the sampling loop.</summary>
public sealed class BatteryCycleReader : IDisposable
{
    // Cycles tick up over days, not seconds — a lazy refresh is plenty.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly Timer _timer;
    private volatile object? _cycles; // boxed int? — volatile ref for lock-free read
    private int _disposed;
    private int _consecutiveMisses;
    private const int GiveUpAfter = 3; // whether a battery reports cycles is a static fact

    public BatteryCycleReader()
    {
        // Fire immediately, then every Interval.
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, Interval);
    }

    /// <summary>Last known cycle count, or null if the provider isn't present /
    /// returned nothing plausible.</summary>
    public int? CurrentCycles => (int?)_cycles;

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CycleCount FROM BatteryCycleCount");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["CycleCount"] is not { } raw)
                        continue;
                    int cycles = Convert.ToInt32(raw);
                    // 0 means "firmware doesn't track it", not "brand new" — treat as absent.
                    if (cycles is > 0 and < 100000)
                    {
                        _cycles = cycles;
                        _consecutiveMisses = 0;
                        return;
                    }
                }
            }
            _cycles = null; // provider present but no usable value
            NoteMiss();
        }
        catch
        {
            _cycles = null; // no provider / access denied — feature simply stays dark
            NoteMiss();
        }
    }

    /// <summary>Whether a machine reports a battery cycle count never changes at
    /// runtime, so once we've struck out a few times, stop polling WMI forever.</summary>
    private void NoteMiss()
    {
        if (_cycles is not null) return;
        if (++_consecutiveMisses >= GiveUpAfter)
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timer.Dispose();
    }
}

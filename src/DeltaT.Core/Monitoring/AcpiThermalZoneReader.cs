using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Last-resort CPU-ish temperature from the ACPI thermal zone
/// (<c>root\wmi:MSAcpi_ThermalZoneTemperature</c>), used only when
/// LibreHardwareMonitor can't read the CPU at all — e.g. a very new part its current
/// build doesn't parse yet. It's a board/zone temperature, not the exact die, but on
/// most laptops it tracks the CPU closely enough to be far better than a blank card,
/// and it's available on essentially every Windows machine (when elevated).
///
/// Lazy on purpose: the timer only starts the first time something actually asks for a
/// value, so on the overwhelming majority of machines — where LHM reads the CPU fine —
/// this never runs at all. The query needs admin (a non-elevated read is access-denied),
/// so it self-disables after a few misses rather than polling WMI forever for nothing.</summary>
public sealed class AcpiThermalZoneReader : IDisposable
{
    // A fallback die-temp proxy still wants to track load, but WMI isn't free — a few
    // seconds is a fair balance for a zone temperature that moves slowly anyway.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(6);

    private Timer? _timer;
    private readonly object _startGate = new();
    private volatile object? _celsius; // boxed double? — volatile ref for lock-free read
    private int _disposed;
    private int _consecutiveMisses;
    private const int GiveUpAfter = 3;

    /// <summary>Latest zone temperature in °C, or null if unavailable. First touch starts
    /// the background poll; before the first successful read this is null.</summary>
    public double? CurrentCelsius
    {
        get
        {
            EnsureStarted();
            return (double?)_celsius;
        }
    }

    private void EnsureStarted()
    {
        if (_timer is not null || Volatile.Read(ref _disposed) != 0)
            return;
        lock (_startGate)
        {
            if (_timer is null && Volatile.Read(ref _disposed) == 0)
                _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, Interval);
        }
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            double? hottest = null;
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["CurrentTemperature"] is not { } raw)
                        continue;
                    // ACPI reports tenths of a Kelvin.
                    double c = Convert.ToDouble(raw) / 10.0 - 273.15;
                    if (c is > 15 and < 120 && (hottest is not { } h || c > h))
                        hottest = Math.Round(c, 1);
                }
            }
            if (hottest is not null)
            {
                _celsius = hottest;
                _consecutiveMisses = 0;
                return;
            }
            _celsius = null;
            NoteMiss();
        }
        catch
        {
            _celsius = null; // no provider / access denied — feature stays dark
            NoteMiss();
        }
    }

    private void NoteMiss()
    {
        if (_celsius is not null) return;
        if (++_consecutiveMisses >= GiveUpAfter)
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timer?.Dispose();
    }
}

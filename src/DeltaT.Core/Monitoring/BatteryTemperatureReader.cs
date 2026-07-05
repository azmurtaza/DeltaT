using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads battery temperature from the ACPI/OEM WMI provider
/// (<c>root\wmi:BatteryTemperature</c>) — the same source NitroSense and other
/// vendor tools use. LibreHardwareMonitor doesn't surface battery temperature on
/// most laptops (this Acer among them), so this fills the gap.
///
/// The query needs admin and takes tens of ms, so it runs on a background timer
/// (~every 20 s — battery temperature drifts slowly) and callers just read the
/// cached value; <see cref="CurrentCelsius"/> never blocks the sampling loop.</summary>
public sealed class BatteryTemperatureReader : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly Timer _timer;
    private volatile object? _celsius; // boxed double? — volatile ref for lock-free read
    private int _disposed;
    private int _consecutiveMisses;
    private const int GiveUpAfter = 3; // battery-temp support is a static hardware fact

    /// <summary>Raised once with the first raw provider value (for diagnostics/logging),
    /// so an unexpected temperature encoding can be spotted. Never raised again.</summary>
    public event Action<uint, double?>? FirstReading;
    private int _firstReported;

    public BatteryTemperatureReader()
    {
        // Fire immediately, then every Interval.
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, Interval);
    }

    /// <summary>Last known battery temperature in °C, or null if the provider isn't
    /// present / returned nothing plausible.</summary>
    public double? CurrentCelsius => (double?)_celsius;

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT Temperature FROM BatteryTemperature");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["Temperature"] is not { } raw)
                        continue;
                    uint value = Convert.ToUInt32(raw);
                    double? celsius = Interpret(value);
                    if (celsius is not null)
                    {
                        _celsius = celsius;
                        _consecutiveMisses = 0;
                        if (Interlocked.Exchange(ref _firstReported, 1) == 0)
                            FirstReading?.Invoke(value, celsius);
                        return;
                    }
                }
            }
            _celsius = null; // provider present but no usable value
            NoteMiss();
        }
        catch
        {
            _celsius = null; // no provider / access denied — feature simply stays dark
            NoteMiss();
        }
    }

    /// <summary>Whether a machine exposes battery temperature never changes at
    /// runtime, so once we've struck out a few times, stop polling WMI forever
    /// rather than paying the query cost every 20 s for nothing.</summary>
    private void NoteMiss()
    {
        if (_celsius is not null) return;
        if (++_consecutiveMisses >= GiveUpAfter)
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Decode the provider's integer into °C. Windows/ACPI battery
    /// temperature is tenths of a degree Kelvin, which is what this provider almost
    /// always reports; the other encodings are tried only if that's implausible, so a
    /// normal 20–45 °C battery decodes correctly and nonsense is rejected as null.</summary>
    public static double? Interpret(uint raw)
    {
        if (raw == 0)
            return null;
        static bool Plausible(double c) => c is >= -5 and <= 90;

        double deciKelvin = raw / 10.0 - 273.15;
        if (Plausible(deciKelvin)) return Math.Round(deciKelvin, 1);

        double kelvin = raw - 273.15;
        if (Plausible(kelvin)) return Math.Round(kelvin, 1);

        double deciCelsius = raw / 10.0;
        if (Plausible(deciCelsius)) return Math.Round(deciCelsius, 1);

        if (Plausible(raw)) return raw;
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _timer.Dispose();
    }
}

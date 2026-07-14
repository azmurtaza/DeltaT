using System.Runtime.InteropServices;
using System.Text;

namespace DeltaT.Core.Monitoring;

/// <summary>One NVML sample: the fast-moving values, straight from the driver.</summary>
public readonly record struct NvmlSample(double? TemperatureC, double? LoadPercent, double? PowerW);

/// <summary>Reads GPU temperature, load and package power straight from NVIDIA's own
/// management library (<c>nvml.dll</c>, shipped with every NVIDIA driver) instead of going
/// through LibreHardwareMonitor.
///
/// Why this exists, in one number: LHM's NVIDIA update costs ~61 ms because it also pulls
/// clocks, PCIe throughput, D3D engine counters and memory statistics that DeltaT never
/// reads. NVML returns the three values DeltaT actually uses in ~0.19 ms, about 300x
/// cheaper. At a 2 s sampling interval that alone was ~3% of a CPU core burned around the
/// clock, and worse than the average suggests: it is a driver call taking a driver lock, so
/// it lands in the middle of game frames (measured spikes to 200-570 ms). This is the same
/// P/Invoke-the-driver's-own-library approach <see cref="GpuBurner"/> already uses for
/// OpenCL, with no new package dependency.
///
/// What it deliberately does NOT provide: the hotspot temperature (NVML's field API reports
/// it unsupported on this driver) and fan RPM (NVML gives a duty-cycle percentage, not RPM,
/// and fan normalization needs real RPM). Those keep coming from LHM, which is why the
/// LHM session is still opened and refreshed - just far less often, since the values it is
/// still needed for move slowly.
///
/// Self-gating, like every other optional source here: no nvml.dll (AMD/Intel machine, a
/// broken driver install), a failed init, or no device whose name matches the card the
/// sensors watch, and this reader latches dead. DeltaT then falls back to reading
/// everything from LHM exactly as before. Not thread-safe by design: call it from the
/// monitor thread.</summary>
public sealed class NvmlGpuReader : IDisposable
{
    private const string Dll = "nvml.dll";

    private IntPtr _device = IntPtr.Zero;
    private bool _initTried;
    private bool _dead;
    private bool _initialized;

    /// <summary>True once NVML is live and bound to a device: the caller can then poll the
    /// GPU at full rate and let LHM idle.</summary>
    public bool IsLive => _initialized && _device != IntPtr.Zero && !_dead;

    /// <summary>Name of the device NVML bound to, for the diagnostic log.</summary>
    public string? DeviceName { get; private set; }

    /// <param name="gpuName">Name of the GPU whose sensors DeltaT is reading (LHM's name for
    /// it). On a hybrid laptop NVML only ever enumerates NVIDIA cards, but matching by name
    /// keeps a multi-GPU desktop honest: the watts must come from the same card as the
    /// temperature, or thermal resistance would be nonsense.</param>
    public NvmlSample Read(string? gpuName)
    {
        if (_dead)
            return default;
        if (!_initTried)
            Init(gpuName);
        if (!IsLive)
            return default;

        double? temp = GetTemperature(_device, 0, out uint t) == 0 ? t : null;
        double? load = GetUtilization(_device, out NvmlUtilization u) == 0 ? u.Gpu : null;
        double? watts = GetPowerUsage(_device, out uint mw) == 0 ? mw / 1000.0 : null;

        // Every call failing at once means the driver went away (a driver update, an eGPU
        // unplugged). Fall back to LHM rather than reporting a dead GPU.
        if (temp is null && load is null && watts is null)
        {
            _dead = true;
            return default;
        }

        return new NvmlSample(
            temp is >= 0 and < 150 ? temp : null,
            load is >= 0 and <= 100 ? load : null,
            watts is > 0 and < 500 ? Math.Round(watts.Value, 1) : null);
    }

    private void Init(string? gpuName)
    {
        _initTried = true;
        try
        {
            if (NvmlInit() != 0)
            {
                _dead = true;
                return;
            }
            _initialized = true;

            if (GetDeviceCount(out uint count) != 0 || count == 0)
            {
                _dead = true;
                return;
            }

            // A name match is always required, even when only one NVIDIA card is present.
            // Binding to "the only card" would also bind on a machine whose sensors DeltaT
            // is reading from an AMD or Intel GPU (gpuName null or someone else's card), and
            // then temperature and watts would come from two different pieces of silicon.
            // Failing to match just means falling back to LHM: slower, never wrong.
            for (uint i = 0; i < count; i++)
            {
                if (GetHandleByIndex(i, out IntPtr dev) != 0)
                    continue;
                string name = DeviceNameOf(dev);
                if (Matches(name, gpuName))
                {
                    _device = dev;
                    DeviceName = name;
                    return;
                }
            }
            _dead = true; // NVIDIA present, but not the card the sensors watch
        }
        catch (DllNotFoundException)
        {
            _dead = true; // no NVIDIA driver on this machine (AMD/Intel): expected, stay quiet
        }
        catch (EntryPointNotFoundException)
        {
            _dead = true; // an NVML too old for these entry points
        }
        catch
        {
            _dead = true;
        }
    }

    /// <summary>Names are compared loosely: LHM and NVML agree on the model but not always on
    /// decoration ("NVIDIA GeForce RTX 3050 6GB Laptop GPU" vs the same without "NVIDIA").</summary>
    public static bool Matches(string nvmlName, string? lhmName)
    {
        if (string.IsNullOrWhiteSpace(nvmlName) || string.IsNullOrWhiteSpace(lhmName))
            return false;
        string a = Simplify(nvmlName), b = Simplify(lhmName!);
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);

        static string Simplify(string s) => s
            .Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string DeviceNameOf(IntPtr dev)
    {
        var buffer = new StringBuilder(96);
        return GetName(dev, buffer, (uint)buffer.Capacity) == 0 ? buffer.ToString() : "";
    }

    public void Dispose()
    {
        _device = IntPtr.Zero;
        _dead = true;
        if (!_initialized)
            return;
        _initialized = false;
        try { NvmlShutdown(); }
        catch { /* the driver is going away anyway */ }
    }

    // ---- NVML (returns 0 = NVML_SUCCESS) -----------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;    // % of the last sampling period with work resident
        public uint Memory;
    }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport(Dll, EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int GetDeviceCount(out uint count);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int GetHandleByIndex(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
    private static extern int GetName(IntPtr device, StringBuilder name, uint length);

    /// <param name="sensor">0 = NVML_TEMPERATURE_GPU (the die).</param>
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int GetTemperature(IntPtr device, uint sensor, out uint temperatureC);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int GetUtilization(IntPtr device, out NvmlUtilization utilization);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int GetPowerUsage(IntPtr device, out uint milliwatts);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeltaT.Core.Diagnostics;

/// <summary>Full ALU load on the GPU through OpenCL — the compute channel every
/// vendor's driver already ships (NVIDIA/AMD/Intel install the ICD loader with the
/// graphics driver), so there is no SDK, package, or bundled binary. The burn kernel
/// is pure fused-multiply-add over a 64 KB cache-resident buffer: heat comes from
/// doing math at full tilt, which is exactly the load a paste measurement wants.
///
/// Dispatches self-tune to ~12 ms of GPU time each, two orders of magnitude under
/// the Windows GPU watchdog (TDR, 2 s), so the desktop stays responsive even when
/// the burned GPU also drives the display and a driver reset is never approached.
/// Strictly read-only toward the machine otherwise: no display surfaces, no clock
/// or fan control. Dispose stops the load instantly.
///
/// Construction throws <see cref="InvalidOperationException"/> when no usable GPU
/// compute device exists (no OpenCL runtime, or only CPU devices) — callers treat
/// that as "this machine can't run a GPU fingerprint", never as a crash.</summary>
public sealed class GpuBurner : IDisposable
{
    private const double TargetDispatchMs = 12;
    private const int BufferFloat4s = 4096;          // 64 KB — L2-resident, ALU-bound
    private static readonly UIntPtr[] GlobalSize = { (UIntPtr)(1 << 20) };

    // Four interleaved mad chains: enough ILP to saturate the schedulers, values
    // free to run to inf/NaN — ALU throughput (and heat) is identical either way.
    private const string KernelSource = """
        __kernel void burn(__global float4* buf, const int iters)
        {
            float4 a = buf[get_global_id(0) & 4095];
            float4 b = a + (float4)(0.5f);
            float4 c = a * (float4)(1.25f);
            float4 d = a - (float4)(0.75f);
            for (int i = 0; i < iters; i++)
            {
                a = mad(a, b, c);
                b = mad(b, c, d);
                c = mad(c, d, a);
                d = mad(d, a, b);
            }
            buf[get_global_id(0) & 4095] = a + b + c + d;
        }
        """;

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private volatile int _permille;   // duty target, 50..1000, read each dispatch
    private IntPtr _context, _queue, _program, _kernel, _buffer;

    public string DeviceName { get; }

    /// <summary>Settable at runtime so a controller can steer the achieved GPU load into a
    /// bucket. The load-vs-duty curve is nonlinear (NVML's utilization metric inflates toward
    /// 100 %), so an open-loop duty overshoots badly. 0.05..1.0; 1.0 is full tilt.</summary>
    public double Utilization
    {
        get => _permille / 1000.0;
        set => _permille = (int)(Math.Clamp(value, 0.05, 1.0) * 1000);
    }

    /// <summary>Starts burning immediately. <paramref name="preferredNameContains"/>
    /// steers device choice toward the sensor-visible GPU (e.g. the discrete card on
    /// a hybrid laptop); without a match, any non-Intel GPU device is preferred.
    /// <paramref name="targetUtilization"/> (0..1, default full) duty-cycles the dispatch
    /// loop so the calibration workout can hold a partial GPU load, not just 100%.</summary>
    public GpuBurner(string? preferredNameContains, double targetUtilization = 1.0)
    {
        Utilization = targetUtilization;
        (IntPtr device, DeviceName) = PickDevice(preferredNameContains);
        try
        {
            _context = Cl.clCreateContext(IntPtr.Zero, 1, new[] { device }, IntPtr.Zero, IntPtr.Zero, out int err);
            Check(err, "context");
            _queue = Cl.clCreateCommandQueue(_context, device, 0, out err);
            Check(err, "queue");
            _program = Cl.clCreateProgramWithSource(_context, 1, new[] { KernelSource }, null, out err);
            Check(err, "program");
            Check(Cl.clBuildProgram(_program, 1, new[] { device }, null, IntPtr.Zero, IntPtr.Zero), "build");
            _kernel = Cl.clCreateKernel(_program, "burn", out err);
            Check(err, "kernel");
            _buffer = Cl.clCreateBuffer(_context, Cl.CL_MEM_READ_WRITE, (UIntPtr)(BufferFloat4s * 16), IntPtr.Zero, out err);
            Check(err, "buffer");
            Check(Cl.clSetKernelArg(_kernel, 0, (UIntPtr)IntPtr.Size, ref _buffer), "arg0");
        }
        catch
        {
            ReleaseAll();
            throw;
        }

        _thread = new Thread(Burn) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Burn()
    {
        int iters = 256;
        var sw = new Stopwatch();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int i = iters;
                if (Cl.clSetKernelArg(_kernel, 1, (UIntPtr)sizeof(int), ref i) != 0)
                    return;
                sw.Restart();
                if (Cl.clEnqueueNDRangeKernel(_queue, _kernel, 1, null, GlobalSize, null, 0, null, IntPtr.Zero) != 0)
                    return;
                Cl.clFinish(_queue);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms > 0.5)
                    iters = (int)Math.Clamp(iters * (TargetDispatchMs / ms), 32, 1_000_000);

                // Duty-cycle to a partial load: idle the engine for a proportional slice
                // after each dispatch. sleep/(dispatch+sleep) = 1 − target. Still one
                // dispatch per loop, so each stays far under the TDR watchdog.
                double target = _permille / 1000.0;
                if (target < 0.999)
                {
                    double sleepMs = ms * (1.0 / target - 1.0);
                    if (sleepMs >= 1)
                        Thread.Sleep((int)Math.Min(sleepMs, 500));
                }
            }
        }
        finally
        {
            ReleaseAll();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // The burn thread owns the CL handles and releases them on its way out; if it
        // somehow wedges inside the driver, leaking beats blocking the caller forever.
        _thread?.Join(2000);
        _cts.Dispose();
    }

    private void ReleaseAll()
    {
        if (_buffer != IntPtr.Zero) { Cl.clReleaseMemObject(_buffer); _buffer = IntPtr.Zero; }
        if (_kernel != IntPtr.Zero) { Cl.clReleaseKernel(_kernel); _kernel = IntPtr.Zero; }
        if (_program != IntPtr.Zero) { Cl.clReleaseProgram(_program); _program = IntPtr.Zero; }
        if (_queue != IntPtr.Zero) { Cl.clReleaseCommandQueue(_queue); _queue = IntPtr.Zero; }
        if (_context != IntPtr.Zero) { Cl.clReleaseContext(_context); _context = IntPtr.Zero; }
    }

    private static (IntPtr Device, string Name) PickDevice(string? preferred)
    {
        IntPtr best = IntPtr.Zero;
        string bestName = "";
        int bestScore = -1;
        try
        {
            if (Cl.clGetPlatformIDs(0, null, out uint platformCount) != 0 || platformCount == 0)
                throw NoDevice();
            var platforms = new IntPtr[platformCount];
            Cl.clGetPlatformIDs(platformCount, platforms, out _);

            foreach (IntPtr platform in platforms)
            {
                if (Cl.clGetDeviceIDs(platform, Cl.CL_DEVICE_TYPE_GPU, 0, null, out uint deviceCount) != 0 || deviceCount == 0)
                    continue; // platform has no GPU devices (e.g. a CPU-only runtime)
                var devices = new IntPtr[deviceCount];
                Cl.clGetDeviceIDs(platform, Cl.CL_DEVICE_TYPE_GPU, deviceCount, devices, out _);

                foreach (IntPtr device in devices)
                {
                    string name = DeviceInfoString(device, Cl.CL_DEVICE_NAME);
                    int score = preferred is { Length: > 0 } p
                                && (name.Contains(p, StringComparison.OrdinalIgnoreCase)
                                    || p.Contains(name, StringComparison.OrdinalIgnoreCase))
                        ? 2
                        : name.Contains("intel", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = device;
                        bestName = name;
                    }
                }
            }
        }
        catch (DllNotFoundException e)
        {
            throw NoDevice(e);
        }
        catch (EntryPointNotFoundException e)
        {
            throw NoDevice(e);
        }

        if (best == IntPtr.Zero)
            throw NoDevice();
        return (best, bestName);

        static InvalidOperationException NoDevice(Exception? inner = null) => new(
            "No GPU compute engine is available - the graphics driver doesn't expose OpenCL. " +
            "Updating the GPU driver usually restores it.", inner);
    }

    private static string DeviceInfoString(IntPtr device, uint param)
    {
        var buf = new byte[256];
        if (Cl.clGetDeviceInfo(device, param, (UIntPtr)buf.Length, buf, out UIntPtr size) != 0)
            return "";
        int len = Math.Min((int)size, buf.Length);
        while (len > 0 && buf[len - 1] == 0) len--; // trailing NUL
        return Encoding.ASCII.GetString(buf, 0, len).Trim();
    }

    private static void Check(int err, string what)
    {
        if (err != 0)
            throw new InvalidOperationException($"OpenCL {what} setup failed (error {err}).");
    }

    /// <summary>Raw OpenCL 1.x entry points — the subset a burner needs, bound against
    /// the ICD loader the GPU driver installs (System32\OpenCL.dll).</summary>
    private static class Cl
    {
        public const ulong CL_DEVICE_TYPE_GPU = 1 << 2;
        public const uint CL_DEVICE_NAME = 0x102B;
        public const ulong CL_MEM_READ_WRITE = 1 << 0;

        [DllImport("OpenCL")]
        public static extern int clGetPlatformIDs(uint numEntries, [Out] IntPtr[]? platforms, out uint numPlatforms);

        [DllImport("OpenCL")]
        public static extern int clGetDeviceIDs(IntPtr platform, ulong deviceType, uint numEntries, [Out] IntPtr[]? devices, out uint numDevices);

        [DllImport("OpenCL")]
        public static extern int clGetDeviceInfo(IntPtr device, uint paramName, UIntPtr paramValueSize, [Out] byte[] paramValue, out UIntPtr paramValueSizeRet);

        [DllImport("OpenCL")]
        public static extern IntPtr clCreateContext(IntPtr properties, uint numDevices, IntPtr[] devices, IntPtr pfnNotify, IntPtr userData, out int errcodeRet);

        [DllImport("OpenCL")]
        public static extern IntPtr clCreateCommandQueue(IntPtr context, IntPtr device, ulong properties, out int errcodeRet);

        [DllImport("OpenCL", CharSet = CharSet.Ansi)]
        public static extern IntPtr clCreateProgramWithSource(IntPtr context, uint count, string[] strings, UIntPtr[]? lengths, out int errcodeRet);

        [DllImport("OpenCL", CharSet = CharSet.Ansi)]
        public static extern int clBuildProgram(IntPtr program, uint numDevices, IntPtr[] deviceList, string? options, IntPtr pfnNotify, IntPtr userData);

        [DllImport("OpenCL", CharSet = CharSet.Ansi)]
        public static extern IntPtr clCreateKernel(IntPtr program, string kernelName, out int errcodeRet);

        [DllImport("OpenCL")]
        public static extern IntPtr clCreateBuffer(IntPtr context, ulong flags, UIntPtr size, IntPtr hostPtr, out int errcodeRet);

        [DllImport("OpenCL")]
        public static extern int clSetKernelArg(IntPtr kernel, uint argIndex, UIntPtr argSize, ref IntPtr argValue);

        [DllImport("OpenCL")]
        public static extern int clSetKernelArg(IntPtr kernel, uint argIndex, UIntPtr argSize, ref int argValue);

        [DllImport("OpenCL")]
        public static extern int clEnqueueNDRangeKernel(IntPtr queue, IntPtr kernel, uint workDim, UIntPtr[]? globalOffset, UIntPtr[] globalSize, UIntPtr[]? localSize, uint numEventsInWaitList, IntPtr[]? eventWaitList, IntPtr evt);

        [DllImport("OpenCL")]
        public static extern int clFinish(IntPtr queue);

        [DllImport("OpenCL")] public static extern int clReleaseMemObject(IntPtr obj);
        [DllImport("OpenCL")] public static extern int clReleaseKernel(IntPtr kernel);
        [DllImport("OpenCL")] public static extern int clReleaseProgram(IntPtr program);
        [DllImport("OpenCL")] public static extern int clReleaseCommandQueue(IntPtr queue);
        [DllImport("OpenCL")] public static extern int clReleaseContext(IntPtr context);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeltaT.Core.Diagnostics;

/// <summary>Full load on the GPU through OpenCL — the compute channel every vendor's
/// driver already ships (NVIDIA/AMD/Intel install the ICD loader with the graphics
/// driver), so there is no SDK, package, or bundled binary. Strictly read-only toward
/// the machine: no display surfaces, no clock or fan control. Dispose stops the load
/// instantly.
///
/// <para><b>Reaching real board power.</b> The first version was pure fused-multiply-add
/// over a 64 KB cache-resident buffer, synchronised with <c>clFinish</c> after every
/// dispatch. Reported on a 140 W RTX 3060 Laptop, that drew ~120 W where FurMark v2 draws
/// 139-140 W, and a fingerprint taken at 86% of board power measures a machine the user
/// never runs. Two things were leaving power on the table. The kernel ran entirely out of
/// registers after its first load, so the memory controller, L2 and the DRAM pipe were idle
/// while only the FP32 lanes drew current. And the host synchronised after each ~12 ms
/// dispatch, so the engine drained and sat idle across every round trip.</para>
///
/// <para>So the kernel now interleaves a streaming touch of a buffer far larger than L2
/// with the mad chains, at roughly one memory operation per 128 fused multiply-adds. That
/// ratio is deliberate and the tuning is one-directional: the goal is the most power, not
/// the most bandwidth, and a memory-BOUND kernel draws LESS than an ALU-bound one because
/// the lanes stall waiting on DRAM. The streaming is there to light up a part of the chip
/// the old kernel left dark, not to become the bottleneck. Eight independent chains (up from
/// four) give the schedulers more ILP to hide what latency remains. And dispatches are
/// pipelined <see cref="QueueDepth"/> deep instead of synchronised one at a time, so the
/// engine always has work queued behind the one it is running.</para>
///
/// <para>Each dispatch still self-tunes to ~12 ms of GPU time, two orders of magnitude under
/// the Windows GPU watchdog (TDR, 2 s), so a driver reset is never approached even when the
/// burned GPU also drives the display. The queue depth multiplies the time between host
/// syncs, not the length of any single command, which is what the watchdog actually watches.</para>
///
/// <para>Construction throws <see cref="InvalidOperationException"/> when no usable GPU
/// compute device exists (no OpenCL runtime, or only CPU devices) — callers treat that as
/// "this machine can't run a GPU fingerprint", never as a crash.</para></summary>
public sealed class GpuBurner : IDisposable
{
    private const double TargetDispatchMs = 12;
    private const int BufferFloat4s = 4096;          // 64 KB — L2-resident, the ALU working set
    private static readonly UIntPtr[] GlobalSize = { (UIntPtr)(1 << 20) };

    /// <summary>How many dispatches stay in flight before the host waits. Deep enough that
    /// the engine never drains across a round trip, shallow enough that Dispose still ends
    /// the load within a few tens of milliseconds.</summary>
    private const int QueueDepth = 4;

    /// <summary>Streaming buffer size, in float4s (16 bytes each). 64 MB by default: far past
    /// any current L2 (an RTX 3060's is 3 MB), so the touches genuinely reach DRAM, while
    /// staying small enough to allocate on a 2 GB card. Power of two, so the wrap is a mask.
    /// Falls back through smaller powers of two when the device won't allocate it.</summary>
    private const int StreamFloat4s = 4 << 20;

    /// <summary>How far apart consecutive streaming touches sit, in float4s. 4 MB of stride
    /// steps past every cache level and every prefetcher, so each touch is a real DRAM
    /// transaction rather than a hit the caches absorb.</summary>
    private const uint StreamStride = 256 * 1024;

    // Eight interleaved mad chains: enough ILP to keep the schedulers fed across the memory
    // touch's latency. Values are free to run to inf/NaN — ALU throughput (and heat) is
    // identical either way. INNER is the fused-multiply-add count between memory touches:
    // 16 iterations x 8 chains = 128 mads per streaming read/write pair, which keeps the
    // kernel ALU-bound (the point) while never letting the memory pipe go idle.
    private const string KernelSource = """
        #define INNER 16
        __kernel void burn(__global float4* buf, __global float4* stream,
                           const int iters, const uint mask)
        {
            size_t gid = get_global_id(0);
            float4 a = buf[gid & 4095];
            float4 b = a + (float4)(0.5f);
            float4 c = a * (float4)(1.25f);
            float4 d = a - (float4)(0.75f);
            float4 e = a * (float4)(0.5f) + (float4)(1.5f);
            float4 f = a - (float4)(0.25f);
            float4 g = a * (float4)(2.0f);
            float4 h = a + (float4)(3.0f);
            uint idx = ((uint)gid * 16u) & mask;
            for (int o = 0; o < iters; o++)
            {
                idx = (idx + STRIDE) & mask;
                a += stream[idx];
                for (int i = 0; i < INNER; i++)
                {
                    a = mad(a, b, c);
                    b = mad(b, c, d);
                    c = mad(c, d, a);
                    d = mad(d, a, b);
                    e = mad(e, f, g);
                    f = mad(f, g, h);
                    g = mad(g, h, e);
                    h = mad(h, e, f);
                }
                stream[idx] = h;
            }
            buf[gid & 4095] = a + b + c + d + e + f + g + h;
        }
        """;

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private volatile int _permille;   // duty target, 50..1000, read each dispatch
    private IntPtr _context, _queue, _program, _kernel, _buffer, _stream;
    private uint _streamMask;

    public string DeviceName { get; }

    /// <summary>Bytes of streaming buffer the burn is actually running over. Zero when the
    /// device refused every size and the kernel fell back to registers only, which is the old
    /// behavior and still burns, just cooler. Exposed so <c>--gpuburn</c> can report it.</summary>
    public long StreamBytes { get; private set; }

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
            Check(Cl.clBuildProgram(_program, 1, new[] { device }, $"-D STRIDE={StreamStride}u", IntPtr.Zero, IntPtr.Zero), "build");
            _kernel = Cl.clCreateKernel(_program, "burn", out err);
            Check(err, "kernel");
            _buffer = Cl.clCreateBuffer(_context, Cl.CL_MEM_READ_WRITE, (UIntPtr)(BufferFloat4s * 16), IntPtr.Zero, out err);
            Check(err, "buffer");
            Check(Cl.clSetKernelArg(_kernel, 0, (UIntPtr)IntPtr.Size, ref _buffer), "arg0");

            // The streaming buffer, halved until the device accepts one. A small card (or one
            // already full of a game's textures) can refuse the first ask, and a burner that
            // throws there would be worse than one that runs the old register-only load.
            for (int count = StreamFloat4s; count >= 64 * 1024; count /= 2)
            {
                _stream = Cl.clCreateBuffer(_context, Cl.CL_MEM_READ_WRITE, (UIntPtr)((long)count * 16), IntPtr.Zero, out err);
                if (err == 0 && _stream != IntPtr.Zero)
                {
                    _streamMask = (uint)(count - 1);
                    StreamBytes = (long)count * 16;
                    break;
                }
                _stream = IntPtr.Zero;
            }
            if (_stream == IntPtr.Zero)
            {
                // No streaming buffer: point the kernel's stream argument at the small ALU
                // buffer and mask the index down to it. Every touch then hits L2 instead of
                // DRAM, which is exactly the old behavior, so the load degrades rather than fails.
                _stream = _buffer;
                _streamMask = BufferFloat4s - 1;
                StreamBytes = 0;
            }
            Check(Cl.clSetKernelArg(_kernel, 1, (UIntPtr)IntPtr.Size, ref _stream), "arg1");
            Check(Cl.clSetKernelArg(_kernel, 3, (UIntPtr)sizeof(uint), ref _streamMask), "arg3");
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
        // Each outer iteration is 16 x 8 fused multiply-adds plus one streaming read/write,
        // so the starting count is far lower than the old kernel's and the tuner converges
        // onto the same ~12 ms of GPU time per dispatch from below.
        int iters = 8;
        var sw = new Stopwatch();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Set the work size once per batch, then queue the whole batch: OpenCL
                // captures argument values at enqueue, and holding them still across a batch
                // keeps that guarantee obvious rather than merely true.
                int i = iters;
                if (Cl.clSetKernelArg(_kernel, 2, (UIntPtr)sizeof(int), ref i) != 0)
                    return;

                sw.Restart();
                int queued = 0;
                for (; queued < QueueDepth; queued++)
                {
                    if (Cl.clEnqueueNDRangeKernel(_queue, _kernel, 1, null, GlobalSize, null, 0, null, IntPtr.Zero) != 0)
                        break;
                }
                if (queued == 0)
                    return;
                // One wait per batch instead of one per dispatch. While the host is between
                // batches the engine is still chewing through the ones behind the current
                // command, so it never drains: that idle gap was costing real board power.
                Cl.clFinish(_queue);
                double ms = sw.Elapsed.TotalMilliseconds / queued;
                if (ms > 0.5)
                    iters = (int)Math.Clamp(iters * (TargetDispatchMs / ms), 1, 100_000);

                // Duty-cycle to a partial load: idle the engine for a proportional slice
                // after each batch. sleep/(batch+sleep) = 1 − target. The dispatches inside a
                // batch stay ~12 ms each, so each command is still far under the TDR watchdog;
                // only the interval between host syncs grew.
                double target = _permille / 1000.0;
                if (target < 0.999)
                {
                    double sleepMs = ms * queued * (1.0 / target - 1.0);
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
        // _stream aliases _buffer when the device refused a streaming allocation; releasing
        // the same object twice is a double free, so clear the alias without releasing it.
        if (_stream == _buffer) _stream = IntPtr.Zero;
        if (_stream != IntPtr.Zero) { Cl.clReleaseMemObject(_stream); _stream = IntPtr.Zero; }
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
        public static extern int clSetKernelArg(IntPtr kernel, uint argIndex, UIntPtr argSize, ref uint argValue);

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

using System.Management;
using System.Security.Principal;
using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using System.Text;
using LibreHardwareMonitor.Hardware;

// Diagnostic spike: opens every LibreHardwareMonitor category and dumps whatever
// this machine actually exposes. Output goes to console and sensor-dump.txt so an
// elevated run (separate window) still leaves a readable result behind.

var sb = new StringBuilder();
void Line(string s = "")
{
    Console.WriteLine(s);
    sb.AppendLine(s);
}

// `--eval`: run the detection-accuracy benchmark and print the numbers. No hardware
// needed; it drives the real scoring + diagnosis engine over known, noisy scenarios.
if (args.Contains("--eval", StringComparer.OrdinalIgnoreCase))
{
    var report = DeltaT.Core.Scoring.DetectionBenchmark.Run();
    Console.WriteLine($"DeltaT detection accuracy benchmark  ({DateTime.Now:yyyy-MM-dd})");
    Console.WriteLine(new string('=', 70));
    Console.WriteLine();
    Console.WriteLine("Cause attribution (does DeltaT name the right cause, or correctly clear it?)");
    Console.WriteLine($"  {"condition",-16} {"trials",7} {"accuracy",9} {"mean score",11}");
    foreach (var c in report.Conditions)
        Console.WriteLine($"  {c.Condition,-16} {c.Trials,7} {c.Accuracy,8:P0} {c.MeanScore,10:0.0}");
    Console.WriteLine();
    Console.WriteLine($"  Fault detection rate (paste/dust/fan/mount): {report.FaultDetectionRate,6:P1}");
    Console.WriteLine($"  Confounder clear rate (OC/undervolt/cold/healthy, i.e. NOT false-alarmed): {report.ConfounderClearRate,6:P1}");
    Console.WriteLine($"  Overall accuracy: {report.OverallAccuracy,6:P1}");
    Console.WriteLine();
    Console.WriteLine("Detection sensitivity (severity at which DeltaT reacts)");
    Console.WriteLine($"  {"fault",-18} {"watch (<85)",12} {"act (<70)",11} {"names cause",12}");
    foreach (var s in report.Sensitivity)
        Console.WriteLine($"  {s.Fault,-18} {FmtSev(s.FlagsAgingAtC),12} {FmtSev(s.FlagsActionAtC),11} {FmtSev(s.NamesCauseAtC),12}");
    Console.WriteLine();
    Console.WriteLine("  (watch/act columns are °C of extra load-rise for paste/dust, ° of hotspot gap for mount)");
    Console.WriteLine();

    // Phase 0: is a workout-acquired baseline as faithful as an organic one? Numbers, not
    // intuition. The design lever is how closely the workout targets each bucket's real
    // operating point, so report a well-targeted workout and a naive burner side by side.
    void FidelityTable(string title, double bias)
    {
        var fid = DeltaT.Core.Scoring.DetectionBenchmark.RunAcquisitionFidelity(synBias: bias);
        Console.WriteLine($"Baseline-acquisition fidelity - {title} (workout offset {bias:P0} of bucket power)");
        Console.WriteLine($"  {"condition",-16} {"organic",9} {"synthetic",11} {"syn max",9} {"org flip",9} {"syn flip",9}");
        foreach (var f in fid.Conditions)
            Console.WriteLine($"  {f.Condition,-16} {f.OrganicMeanAbsErr,8:0.00} {f.SyntheticMeanAbsErr,10:0.00} {f.SyntheticMaxAbsErr,8:0.0} {f.OrganicFlips,9} {f.SyntheticFlips,9}");
        Console.WriteLine($"  overall: organic {fid.OrganicMeanAbsErr:0.00} | synthetic {fid.SyntheticMeanAbsErr:0.00} pts (max {fid.SyntheticMaxAbsErr:0.0}) | fault-flips org {fid.OrganicFlips} syn {fid.SyntheticFlips}");
        Console.WriteLine($"  VERDICT: {(fid.SyntheticNoWorseThanOrganic() ? "AS FAITHFUL as organic (accuracy held) -> GO" : "LESS faithful than organic -> NO-GO")}");
        Console.WriteLine();
    }
    Console.WriteLine("(flip = leading FAULT class changed vs the true baseline; Healthy/PowerConfig/HighAmbient are one class)");
    Console.WriteLine();
    FidelityTable("well-targeted workout", DeltaT.Core.Scoring.DetectionBenchmark.WellTargetedWorkoutBias);
    FidelityTable("naive burner", DeltaT.Core.Scoring.DetectionBenchmark.NaiveBurnerBias);
    return;

    static string FmtSev(double? c) => c is { } v ? $"{v:0.#}°" : "n/a";
}

// `--baseline`: dump this machine's LEARNED baseline (delta/power/fan per load bucket) from
// the real DeltaT database. The Phase 0 accuracy question for the calibration-workout feature
// is whether the workout's per-bucket watts match what the machine learned organically, so
// this is the other half of `--workout`: read baseline.power_avg and compare by eye. No admin
// needed (just reads the local db); does not touch the running app's data.
if (args.Contains("--baseline", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var db = new DeltaT.Core.Storage.DeltaTDb();
        var settings = new DeltaT.Core.Storage.SettingsStore(db);
        var repo = new DeltaT.Core.Storage.TelemetryRepository(db);
        int epoch = (int)(settings.GetDouble(DeltaT.Core.Storage.SettingsKeys.BaselineEpoch) ?? 0);
        var rows = repo.GetBaseline(epoch);
        Console.WriteLine($"Learned baseline  (epoch {epoch}, {rows.Count} cells)");
        Console.WriteLine(new string('=', 70));
        if (rows.Count == 0)
            Console.WriteLine("  no baseline rows yet - this machine hasn't locked a baseline for this epoch.");
        foreach (var g in rows.GroupBy(r => (r.Kind, r.Name)))
        {
            Console.WriteLine($"\n{g.Key.Kind} \"{g.Key.Name}\"");
            Console.WriteLine($"  {"bucket",-8} {"band",-5} {"delta",7} {"power",8} {"fan",7} {"minutes",8}");
            foreach (var r in g.OrderBy(r => r.Bucket).ThenBy(r => r.Band))
                Console.WriteLine($"  {r.Bucket,-8} {r.Band,-5} {r.DeltaAvg,6:0.0}° " +
                    $"{(r.PowerAvg is { } p ? $"{p,6:0.0}W" : "     --"),8} " +
                    $"{(r.FanAvg is { } f ? $"{f,5:0}r" : "    --"),7} {r.Minutes,8}");
        }
        Console.WriteLine();
        Console.WriteLine("Compare the power column against the workout's per-bucket watts (--workout): a small");
        Console.WriteLine("gap means a workout at this power config would calibrate faithfully (Phase 0 GO).");
    }
    catch (Exception ex) { Console.WriteLine($"baseline read failed: {ex.Message}"); }
    return;
}

bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Line($"DeltaT sensor spike - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Line($"Elevated: {elevated}{(elevated ? "" : "   << CPU package temps + storage SMART need admin")}");
Line(new string('=', 78));

// `--geo`: what does automatic location actually resolve to on this machine? Prints the
// raw Windows Geolocator coordinates (current app params vs a high-accuracy fresh fix)
// and how BigDataCloud names each, so a "wrong city" report can be pinned to either the
// coordinate (positioning limit) or the name (reverse-geocode choice).
if (args.Contains("--geo", StringComparer.OrdinalIgnoreCase))
{
    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaT/1.0");

    string Prop(System.Text.Json.JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
    string Inv(double d) => d.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    async Task Name(double lat, double lon, string tag)
    {
        try
        {
            string u = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={Inv(lat)}&longitude={Inv(lon)}&localityLanguage=en";
            using var d = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(u));
            var r = d.RootElement;
            Line($"     name[{tag}]: raw city='{Prop(r, "city")}'  ->  DeltaT resolves: '{DeltaT.Core.Weather.AmbientService.ResolvePlaceName(r)}, {Prop(r, "countryName")}'");
        }
        catch (Exception ex) { Line($"     reverse-geocode error: {ex.Message}"); }
    }

    async Task Geo()
    {
        Line("Windows Geolocator auto-detect diagnostic:");
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            Line($"  access: {access}");
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) { Line("  denied/off -> app would fall back to IP providers"); return; }

            var coarse = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 1500 };
            var pc = (await coarse.GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(12)).AsTask()).Coordinate;
            Line($"  [current app: 1500m, 1h cache]  lat {Inv(pc.Point.Position.Latitude)}  lon {Inv(pc.Point.Position.Longitude)}  accuracy {pc.Accuracy:0} m  source {pc.PositionSource}");
            await Name(pc.Point.Position.Latitude, pc.Point.Position.Longitude, "current");

            var hi = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.High };
            var ph = (await hi.GetGeopositionAsync(TimeSpan.Zero, TimeSpan.FromSeconds(15)).AsTask()).Coordinate;
            Line($"  [high accuracy, fresh fix]      lat {Inv(ph.Point.Position.Latitude)}  lon {Inv(ph.Point.Position.Longitude)}  accuracy {ph.Accuracy:0} m  source {ph.PositionSource}");
            await Name(ph.Point.Position.Latitude, ph.Point.Position.Longitude, "high");
        }
        catch (Exception ex) { Line($"  geolocator error: {ex.Message}"); }
    }
    Geo().GetAwaiter().GetResult();
    Line("");
    Line("Reference: real Sialkot city center is ~32.4927, 74.5313; Daska ~32.3242, 74.3500.");
    System.IO.File.WriteAllText("geo-dump.txt", sb.ToString());
    return;
}

// `--msr`: compare LibreHardwareMonitor's native CPU reading against the direct
// MSR/SMN reader the app falls back to on CPUs newer than the pinned LHM build
// (Arrow/Lunar/Panther Lake, post-Zen-5). On a natively supported CPU the two
// columns should agree within a couple of degrees; on an unsupported one the LHM
// column is blank and the MSR column is what DeltaT actually shows.
if (args.Contains("--msr", StringComparer.OrdinalIgnoreCase))
{
    var msrComputer = new Computer { IsCpuEnabled = true };
    msrComputer.Open();
    var reader = new DeltaT.Core.Monitoring.CpuMsrTemperatureReader();
    var msrVisitor = new UpdateVisitor();
    for (int i = 0; i < 8; i++)
    {
        msrComputer.Accept(msrVisitor);
        foreach (IHardware hw in msrComputer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu)
                continue;
            float? lhmMax = null;
            foreach (ISensor s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Temperature && !s.Name.Contains("Distance") && s.Value is { } v)
                    lhmMax = lhmMax is { } m && m > v ? m : v;
            }
            DeltaT.Core.Monitoring.CpuMsrReading? msr = reader.TryRead(hw.Name);
            Line($"[{i}] {hw.Name}: LHM {(lhmMax?.ToString("0.0") ?? "--"),6} °C | MSR {(msr is { } r ? $"{r.TemperatureC:0.0} °C  (TjMax {r.TjMaxC?.ToString("0") ?? "?"}, throttling {r.Throttling})" : "--")}");
        }
        Thread.Sleep(700);
    }
    msrComputer.Close();
    return;
}

// `--cost`: what does one sensor pass actually cost? DeltaT's whole polling design rests on
// these numbers (fast readers every tick, LHM's expensive per-hardware Update() on a slow
// clock), and they were previously measured by hand, which is how they went stale when the
// kernel driver changed from WinRing0 to PawnIO. Run this ELEVATED: a non-elevated run fails
// fast on exactly the expensive paths and flatters every figure.
if (args.Contains("--cost", StringComparer.OrdinalIgnoreCase))
{
    const int Samples = 20;
    Line($"elevated : {elevated}{(elevated ? "" : "   << numbers below are NOT valid; the costly paths fail fast unelevated")}");
    Line($"PawnIO   : {(DeltaT.Core.Monitoring.PawnIoStatus.IsInstalled ? $"present ({DeltaT.Core.Monitoring.PawnIoStatus.Version})" : "MISSING - CPU register reads will fail")}");
    Line();

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static double MedianMs(Action action, int samples)
    {
        var times = new List<double>(samples);
        var sw = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < samples; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    var costComputer = new Computer
    {
        IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true,
        IsStorageEnabled = true, IsBatteryEnabled = true,
    };
    costComputer.Open();

    Line("LibreHardwareMonitor per-hardware Update()  (median of 20):");
    double lhmTotal = 0;
    foreach (IHardware hw in costComputer.Hardware)
    {
        double ms = MedianMs(() => hw.Update(), Samples);
        lhmTotal += ms;
        Line($"  {hw.HardwareType,-14} {Trim(hw.Name, 34),-34} {ms,8:0.00} ms");
    }
    Line($"  {"",-14} {"TOTAL if polled every tick",-34} {lhmTotal,8:0.00} ms");
    Line();

    Line("DeltaT's own fast readers (what actually runs every tick):");
    var costMsr = new DeltaT.Core.Monitoring.CpuMsrTemperatureReader();
    var costLoad = new DeltaT.Core.Monitoring.CpuLoadReader();
    var costNvml = new DeltaT.Core.Monitoring.NvmlGpuReader();
    var costFans = new DeltaT.Core.Monitoring.LaptopFanReader();
    string cpuName = costComputer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name ?? "";
    string gpuName = costComputer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)?.Name ?? "";

    double msrMs = MedianMs(() => costMsr.TryRead(cpuName), Samples);
    double loadMs = MedianMs(() => costLoad.Read(), Samples);
    double nvmlMs = MedianMs(() => costNvml.Read(gpuName), Samples);
    double fanMs = MedianMs(() => costFans.Read(), Samples);

    Line($"  CPU temp   CpuMsrTemperatureReader (PawnIO)   {msrMs,8:0.00} ms   {(costMsr.TryRead(cpuName) is { } cr ? $"{cr.TemperatureC:0.0} °C" : "no reading")}");
    Line($"  CPU load   CpuLoadReader (GetSystemTimes)     {loadMs,8:0.00} ms");
    Line($"  GPU        NvmlGpuReader (nvml.dll)           {nvmlMs,8:0.00} ms   {(costNvml.IsLive ? "live" : "not available")}");
    Line($"  Fans       LaptopFanReader (vendor WMI)       {fanMs,8:0.00} ms   {costFans.ActiveVendor ?? "none"}   [polled every 6 s, not every tick]");
    Line();
    Line($"  per-tick total (temp + load + GPU)            {msrMs + loadMs + nvmlMs,8:0.00} ms");
    Line($"  at the 2 s sampling interval that is          {(msrMs + loadMs + nvmlMs) / 2000.0 * 100,8:0.000} % of one core");
    Line();
    Line("Compare against the LHM totals above: that gap is the whole reason for the split.");
    Line("Update the perf numbers in CLAUDE.md whenever the sensor layer changes.");

    costMsr.Dispose();
    costNvml.Dispose();
    costFans.Dispose();
    costComputer.Close();
    return;
}

// `--gpuburn`: prove the OpenCL burner (the GPU fingerprint's load engine) works on
// this machine: pick the compute device, burn ~12 s, and watch the GPU temp/load
// ramp through LHM. Heat comes purely from math; Dispose ends the load instantly.
if (args.Contains("--gpuburn", StringComparer.OrdinalIgnoreCase))
{
    var gpuComputer = new Computer { IsGpuEnabled = true };
    gpuComputer.Open();
    var gpuVisitor = new UpdateVisitor();
    try
    {
        using var burner = new DeltaT.Core.Diagnostics.GpuBurner(null);
        Line($"burning on: {burner.DeviceName}");
        for (int i = 1; i <= 12; i++)
        {
            Thread.Sleep(1000);
            gpuComputer.Accept(gpuVisitor);
            foreach (IHardware hw in gpuComputer.Hardware)
            {
                if (hw.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
                    continue;
                // Core and hotspot printed separately: DeltaT (and NitroSense) headline
                // "GPU Core"; the hotspot runs several degrees above it by design, so a
                // max-of-all-sensors readout would look inflated next to the app.
                float? core = null, hotspot = null, load = null;
                foreach (ISensor s in hw.Sensors)
                {
                    if (s is { SensorType: SensorType.Temperature, Name: "GPU Core", Value: { } t }) core = t;
                    if (s is { SensorType: SensorType.Temperature, Name: "GPU Hot Spot", Value: { } h }) hotspot = h;
                    if (s is { SensorType: SensorType.Load, Name: "GPU Core", Value: { } l }) load = l;
                }
                Line($"[{i,2}s] {hw.Name}: core {core?.ToString("0.0") ?? "--"} °C   hotspot {hotspot?.ToString("0.0") ?? "--"} °C   {load?.ToString("0") ?? "--"}% load");
            }
        }
        Line("stopping burner - load should vanish instantly");
    }
    catch (Exception ex)
    {
        Line($"GPU burner failed: {ex.Message}");
    }
    gpuComputer.Close();
    return;
}

// `--workout`: Phase 1 of the guided-calibration feature. Drives the CPU then the GPU at
// graded target loads (Medium/Heavy/Full) with the duty-cycling burners and measures what
// each level ACTUALLY produces on this machine: load %, package watts, temp, and which
// calibration bucket the load lands in. Phase 0 proved a workout only holds accuracy if its
// per-bucket operating point matches real use; this is how we learn that point on real
// hardware instead of assuming it. Needs admin for CPU package power/temp (PawnIO), like the
// other CPU spikes. Optional arg: seconds per level's measure window (default 15).
if (args.Contains("--workout", StringComparer.OrdinalIgnoreCase))
{
    int measureS = 15;
    foreach (string a in args)
        if (int.TryParse(a, out int s) && s is >= 3 and <= 120) measureS = s;
    const int warmupS = 22; // let package power/temp settle before averaging (CPU decays ~30 s)

    var comp = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
    comp.Open();
    var wkVisitor = new UpdateVisitor();

    static double? Read(Computer c, HardwareType[] types, SensorType type, params string[] names)
    {
        foreach (IHardware hw in c.Hardware)
        {
            if (Array.IndexOf(types, hw.HardwareType) < 0) continue;
            foreach (ISensor s in hw.Sensors)
                if (s.SensorType == type && s.Value is { } v
                    && (names.Length == 0 || Array.Exists(names, n => n.Equals(s.Name, StringComparison.OrdinalIgnoreCase))))
                    return v;
        }
        return null;
    }

    var cpuTypes = new[] { HardwareType.Cpu };
    var gpuTypes = new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel };
    (double? Load, double? Power, double? Temp) SampleCpu() => (
        Read(comp, cpuTypes, SensorType.Load, "CPU Total"),
        Read(comp, cpuTypes, SensorType.Power, "CPU Package", "Package"),
        Read(comp, cpuTypes, SensorType.Temperature, "CPU Package", "Core Max", "Core (Tctl/Tdie)"));
    (double? Load, double? Power, double? Temp) SampleGpu() => (
        Read(comp, gpuTypes, SensorType.Load, "GPU Core"),
        Read(comp, gpuTypes, SensorType.Power, "GPU Package", "GPU Power"),
        Read(comp, gpuTypes, SensorType.Temperature, "GPU Core"));

    // Closed-loop hold: create the burner, then steer its duty each second so the MEASURED
    // load converges on targetPct (open-loop duty overshoots, so a fixed duty won't do). Once
    // settled, average load/power/temp over the measure window. targetPct >= 100 means full tilt.
    void Hold(string label, double targetPct, Func<double, IDisposable> makeBurner,
              Action<IDisposable, double> setUtil, Func<(double? Load, double? Power, double? Temp)> sample)
    {
        var loads = new List<double>(); var powers = new List<double>(); var temps = new List<double>();
        IDisposable? burner = null;
        bool full = targetPct >= 100;
        double util = full ? 1.0 : targetPct / 100.0; // first guess; the loop corrects it
        try
        {
            burner = makeBurner(util);
            for (int i = 0; i < warmupS + measureS; i++)
            {
                Thread.Sleep(1000);
                comp.Accept(wkVisitor);
                var (l, p, t) = sample();

                // Proportional control toward the target load during warmup (skip for full tilt).
                if (!full && l is { } cur)
                {
                    util = Math.Clamp(util + 0.004 * (targetPct - cur), 0.05, 1.0);
                    setUtil(burner!, util);
                }

                if (i < warmupS) continue;
                if (l is { } lv) loads.Add(lv);
                if (p is { } pv) powers.Add(pv);
                if (t is { } tv) temps.Add(tv);
            }
        }
        catch (Exception ex) { Line($"  {label,-16} FAILED: {ex.Message}"); return; }
        finally { burner?.Dispose(); }

        string L = loads.Count > 0 ? $"{loads.Average(),5:0.0}%" : "   --";
        string P = powers.Count > 0 ? $"{powers.Average(),6:0.0} W" : "     --";
        string T = temps.Count > 0 ? $"{temps.Average(),5:0.0} °C" : "   --";
        string duty = full ? "100%" : $"{util * 100,0:0}%";
        string bucket = loads.Count > 0 ? LoadBuckets.FromPercent(loads.Average()).Label() : "?";
        Line($"  {label,-16} load {L}   power {P}   temp {T}   (duty {duty,-4}) -> {bucket}");
        Thread.Sleep(6000); // brief idle between levels so the next starts from a lower point
    }

    Line();
    Line($"Guided-calibration workout probe (measure {measureS}s after {warmupS}s warmup per level)");
    Line($"elevated: {elevated}{(elevated ? "" : "   << CPU package power/temp need admin (PawnIO); rerun as admin")}");
    Line(new string('-', 78));

    string? gpuName = null;
    foreach (IHardware hw in comp.Hardware)
        if (Array.IndexOf(gpuTypes, hw.HardwareType) >= 0 && !hw.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            gpuName = hw.Name;

    static void SetCpu(IDisposable b, double u) => ((DeltaT.Core.Diagnostics.CpuBurner)b).Utilization = u;
    static void SetGpu(IDisposable b, double u) => ((DeltaT.Core.Diagnostics.GpuBurner)b).Utilization = u;

    Line("CPU (closed-loop all-core burn, steered to target load):");
    Hold("Medium (~55%)", 55, u => new DeltaT.Core.Diagnostics.CpuBurner(u), SetCpu, SampleCpu);
    Hold("Heavy (~80%)", 80, u => new DeltaT.Core.Diagnostics.CpuBurner(u), SetCpu, SampleCpu);
    Hold("Full (100%)", 100, u => new DeltaT.Core.Diagnostics.CpuBurner(u), SetCpu, SampleCpu);

    Line();
    Line($"GPU (closed-loop OpenCL burn{(gpuName is null ? "" : $", {gpuName}")}):");
    try
    {
        Hold("Medium (~55%)", 55, u => new DeltaT.Core.Diagnostics.GpuBurner(gpuName, u), SetGpu, SampleGpu);
        Hold("Heavy (~80%)", 80, u => new DeltaT.Core.Diagnostics.GpuBurner(gpuName, u), SetGpu, SampleGpu);
        Hold("Full (100%)", 100, u => new DeltaT.Core.Diagnostics.GpuBurner(gpuName, u), SetGpu, SampleGpu);
    }
    catch (Exception ex) { Line($"  GPU workout unavailable: {ex.Message}"); }

    Line();
    Line("Read the load% column: each level should land in its named bucket, and the watts are");
    Line("this machine's real per-bucket operating point (compare against baseline.power_avg).");
    comp.Close();
    System.IO.File.WriteAllText("workout-probe.txt", sb.ToString());
    return;
}

// `--acer`: probe the Acer gaming WMI interface (the NitroSense channel) that
// AcerWmiFanReader rides: a raw GetGamingSysInfo sweep over sensor ids, then live
// CPU/GPU fan RPM beside the LHM CPU temperature — load the machine while it runs
// and the RPMs should ramp with the heat. Read-only throughout; needs admin
// (root\wmi denies standard users). Leaves acer-wmi-dump.txt next to the exe.
// Vendor-agnostic fan probe report: what does THIS machine's firmware actually expose?
// The one thing a user on hardware we don't own can run and send back.
if (args.Contains("--fans", StringComparer.OrdinalIgnoreCase))
{
    MachineIdentity id = MachineIdentityProvider.Detect();
    Line($"machine : {id.Display}  (family '{id.SystemFamily}', laptop={id.IsLaptop})");
    Line($"elevated: {elevated}{(elevated ? "" : "   << root\\wmi denies standard users; rerun as admin")}");
    Line();

    Line("firmware interfaces (does the class exist on this machine?):");
    (string Vendor, string Namespace, string Class)[] interfaces =
    {
        ("Acer Nitro/Predator", @"root\wmi", "AcerGamingFunction"),
        ("Lenovo Legion/LOQ", @"root\WMI", "LENOVO_FAN_METHOD"),
        ("ASUS ROG/TUF", @"root\WMI", "AsusAtkWmi_WMNB"),
        ("HP business-class", @"root\HP\InstrumentedBIOS", "HPBIOS_BIOSNumericSensor"),
        ("HP business-class (alt)", @"root\WMI", "HPBIOS_BIOSNumericSensor"),
    };
    foreach ((string vendor, string ns, string cls) in interfaces)
    {
        string verdict;
        try
        {
            using var s = new ManagementObjectSearcher(ns, $"SELECT * FROM {cls}");
            int n = s.Get().Count;
            verdict = n > 0 ? $"PRESENT ({n} instance(s))" : "class known, no instances";
        }
        catch (Exception ex)
        {
            verdict = $"absent ({ex.GetType().Name})";
        }
        Line($"  {vendor,-24} {ns}:{cls}");
        Line($"  {"",-24}   -> {verdict}");
    }

    // HP's sensor class is a plain query, so its contents can just be dumped.
    try
    {
        using var hp = new ManagementObjectSearcher(
            @"root\HP\InstrumentedBIOS", "SELECT Name, CurrentReading, BaseUnits, UnitModifier FROM HPBIOS_BIOSNumericSensor");
        List<ManagementObject> sensors = hp.Get().Cast<ManagementObject>().ToList();
        if (sensors.Count > 0)
        {
            Line();
            Line("HPBIOS_BIOSNumericSensor instances (BaseUnits 19 = RPM):");
            foreach (ManagementObject mo in sensors)
            {
                using (mo)
                    Line($"  {mo["Name"],-28} reading={mo["CurrentReading"],8}  baseUnits={mo["BaseUnits"],4}  modifier={mo["UnitModifier"]}");
            }
        }
    }
    catch { /* not an HP business BIOS */ }

    Line();
    Line("live read through the real probe coordinator (10 samples, 2 s apart):");
    using (var fans = new DeltaT.Core.Monitoring.LaptopFanReader())
    {
        for (int i = 0; i < 10; i++)
        {
            LaptopFanSample s = fans.Read();
            Line($"  [{i:00}] cpu={FmtFan(s.CpuRpm)}  gpu={FmtFan(s.GpuRpm)}  latched={fans.ActiveVendor ?? "(none yet)"}");
            Thread.Sleep(2000);
        }
        Line();
        Line(fans.ActiveVendor is { } v
            ? $"RESULT: fan telemetry works on this machine via the {v} interface."
            : "RESULT: no vendor answered. DeltaT runs fine, but with no fan RPM the scoring "
              + "falls back to raw deltas (no fan normalization). On an HP Omen/Victus the fans are "
              + "EC-only: run --omen to probe the Embedded Controller directly. On an MSI it is "
              + "expected (not yet covered).");
    }

    static string FmtFan(double? rpm) => rpm is { } r ? $"{r,5:0} rpm" : "   -- ";
    return;
}

// `--omen`: probe the HP OMEN/Victus Embedded Controller fan registers (0xB0-0xB3) directly.
// These chassis expose no WMI RPM getter, so the EC is the only source. Reads go through LHM's
// signed-PawnIO, mutex-arbitrated WindowsEmbeddedControllerIO: read-only, arbitrated with the
// OS/other apps, never a bare port poke. Load the machine while it runs and the RPMs should
// ramp with the heat. Needs admin + PawnIO. Leaves omen-ec-dump.txt next to the exe. This is
// the command to ask an HP Omen/Victus user to run and send back.
if (args.Contains("--omen", StringComparer.OrdinalIgnoreCase))
{
    MachineIdentity omenId = MachineIdentityProvider.Detect();
    Line($"machine : {omenId.Display}  (family '{omenId.SystemFamily}', laptop={omenId.IsLaptop})");
    Line($"elevated: {elevated}{(elevated ? "" : "   << EC access needs admin; rerun as admin")}");
    Line($"PawnIO  : {(DeltaT.Core.Monitoring.PawnIoStatus.IsInstalled ? $"installed (v{DeltaT.Core.Monitoring.PawnIoStatus.Version})" : "NOT installed  << no signed EC module; install PawnIO first")}");
    Line($"gate    : IsHpOmenOrVictus = {DeltaT.Core.Monitoring.HpOmenEcFanReader.IsHpOmenOrVictus(omenId)}  (the reader stays dark unless this is true)");
    Line();

    if (!DeltaT.Core.Monitoring.PawnIoStatus.IsInstalled)
    {
        Line("Skipping EC read: PawnIO is not installed, so there is no signed EC module to read through.");
        string omenNoDrv = Path.Combine(AppContext.BaseDirectory, "omen-ec-dump.txt");
        File.WriteAllText(omenNoDrv, sb.ToString());
        Console.WriteLine($"\nSaved to {omenNoDrv}");
        return;
    }

    // Raw EC dump of the fan window (0xB0-0xB7): both byte orders printed so the endianness of
    // the RPM word can be settled on real hardware. Read-only; one arbitrated batch.
    ushort[] window = { 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7 };
    try
    {
        using var ecIo = new LibreHardwareMonitor.Hardware.Motherboard.Lpc.EC.WindowsEmbeddedControllerIO();
        var buf = new byte[window.Length];
        ecIo.Read(window, buf);
        Line("raw EC bytes 0xB0-0xB7 (fan RPM words per OmenMon: 0xB0=fan1, 0xB2=fan2):");
        Line("  reg    byte   +as word with next (LE / BE)");
        for (int i = 0; i < window.Length; i++)
        {
            string word = i + 1 < window.Length
                ? $"LE={buf[i] | (buf[i + 1] << 8),6}   BE={buf[i + 1] | (buf[i] << 8),6}"
                : "";
            Line($"  0x{window[i]:X2}   0x{buf[i]:X2}   {word}");
        }
        Line();
        Line($"decoded fan1 (0xB0/B1): {DescribeFan(buf[0], buf[1])}");
        Line($"decoded fan2 (0xB2/B3): {DescribeFan(buf[2], buf[3])}");
    }
    catch (Exception ex)
    {
        Line($"EC read failed: {ex.GetType().Name}: {ex.Message}");
        Line("(BusMutexLockingFailedException here means the EC was busy — this is the safe failure, not a lockup.)");
    }

    Line();
    Line("live read through the real probe coordinator (12 samples, 1 s apart) vs LHM CPU temp:");
    Line("load the CPU/GPU now — a real Omen fan should spin up and its RPM climb with the heat.");
    using (var omenFans = new DeltaT.Core.Monitoring.LaptopFanReader())
    {
        var omenComputer = new Computer { IsCpuEnabled = true };
        omenComputer.Open();
        var omenVisitor = new UpdateVisitor();
        for (int i = 0; i < 12; i++)
        {
            omenComputer.Accept(omenVisitor);
            float? lhmMax = null;
            foreach (IHardware hw in omenComputer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu)
                    continue;
                foreach (ISensor s in hw.Sensors)
                    if (s.SensorType == SensorType.Temperature && !s.Name.Contains("Distance") && s.Value is { } v)
                        lhmMax = lhmMax is { } m && m > v ? m : v;
            }
            DeltaT.Core.Monitoring.LaptopFanSample r = omenFans.Read();
            Line($"  [{i,2}] CPU fan {(r.CpuRpm is { } c ? $"{c,5:0} rpm" : "   --   ")}   GPU fan {(r.GpuRpm is { } g ? $"{g,5:0} rpm" : "   --   ")}   CPU {(lhmMax?.ToString("0.0") ?? "--"),5} °C   [{omenFans.ActiveVendor ?? "probing"}]");
            Thread.Sleep(1000);
        }
        omenComputer.Close();
        Line();
        Line(omenFans.ActiveVendor is { } vendor
            ? $"RESULT: fan telemetry works via the {vendor} EC path. Confirm the RPMs match HWiNFO/OmenMon."
            : "RESULT: the EC path did not decode a fan. Send the raw-bytes block above; the fan word "
              + "may sit at a different register or byte order on this model.");
    }

    string omenOut = Path.Combine(AppContext.BaseDirectory, "omen-ec-dump.txt");
    File.WriteAllText(omenOut, sb.ToString());
    Console.WriteLine($"\nSaved to {omenOut}");

    static string DescribeFan(byte b0, byte b1)
        => DeltaT.Core.Monitoring.HpOmenEcFanReader.DecodeFan(b0, b1, out double rpm) switch
        {
            DeltaT.Core.Monitoring.HpOmenEcFanReader.FanDecode.Parked => "parked (all-zero) — a valid quiet state",
            DeltaT.Core.Monitoring.HpOmenEcFanReader.FanDecode.Rpm => $"{rpm:0} rpm",
            _ => "non-zero but not a believable RPM (wrong register or byte order?)",
        };
    return;
}

if (args.Contains("--acer", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AcerGamingFunction");
        ManagementObject? inst = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        if (inst is null)
        {
            Line("AcerGamingFunction: class exists but no instance - not Acer gaming firmware?");
        }
        else
        {
            using (inst)
            {
                Line($"AcerGamingFunction instance: {inst["InstanceName"]}");
                Line();
                Line("GetGamingSysInfo sweep (input = id<<8 | 0x01; status 0 = sensor answered):");
                Line("  id    raw                   status  value(bits 8-23)");
                for (uint id = 0; id <= 0x14; id++)
                {
                    using ManagementBaseObject ip = inst.GetMethodParameters("GetGamingSysInfo");
                    ip["gmInput"] = (id << 8) | 0x01u;
                    using ManagementBaseObject op = inst.InvokeMethod("GetGamingSysInfo", ip, null);
                    ulong raw = Convert.ToUInt64(op["gmOutput"]);
                    Line($"  0x{id:X2}  0x{raw:X16}  {raw & 0xFF,4}  {(raw >> 8) & 0xFFFF,8}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Line($"sweep failed: {ex.Message}{(elevated ? "" : "   << root\\wmi needs admin")}");
    }

    Line();
    Line("live fan RPM (LaptopFanReader: Acer ids 0x02/0x06, Lenovo FanID 1/2) vs LHM CPU temp:");
    using (var fans = new DeltaT.Core.Monitoring.LaptopFanReader())
    {
        var acerComputer = new Computer { IsCpuEnabled = true };
        acerComputer.Open();
        var acerVisitor = new UpdateVisitor();
        for (int i = 0; i < 12; i++)
        {
            acerComputer.Accept(acerVisitor);
            float? lhmMax = null;
            foreach (IHardware hw in acerComputer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu)
                    continue;
                foreach (ISensor s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Temperature && !s.Name.Contains("Distance") && s.Value is { } v)
                        lhmMax = lhmMax is { } m && m > v ? m : v;
                }
            }
            DeltaT.Core.Monitoring.LaptopFanSample r = fans.Read();
            Line($"  [{i,2}] CPU fan {(r.CpuRpm is { } c ? $"{c,5:0} rpm" : "   --   ")}   GPU fan {(r.GpuRpm is { } g ? $"{g,5:0} rpm" : "   --   ")}   CPU {(lhmMax?.ToString("0.0") ?? "--"),5} °C   [{fans.ActiveVendor ?? "probing"}]");
            Thread.Sleep(1000);
        }
        acerComputer.Close();
    }

    string acerOut = Path.Combine(AppContext.BaseDirectory, "acer-wmi-dump.txt");
    File.WriteAllText(acerOut, sb.ToString());
    Console.WriteLine();
    Console.WriteLine($"Saved to {acerOut}");
    return;
}

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsStorageEnabled = true,
    IsBatteryEnabled = true,
    IsControllerEnabled = true,
    IsPsuEnabled = true,
};

computer.Open();

// A few passes so throughput/min/max sensors settle before we snapshot.
var visitor = new UpdateVisitor();
for (int i = 0; i < 3; i++)
{
    computer.Accept(visitor);
    Thread.Sleep(1000);
}

foreach (IHardware hw in computer.Hardware)
    Dump(hw, 0);

computer.Close();

string outPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "sensor-dump.txt");
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine();
Console.WriteLine($"Saved to {outPath}");

void Dump(IHardware hw, int depth)
{
    string indent = new string(' ', depth * 2);
    Line();
    Line($"{indent}[{hw.HardwareType}] {hw.Name}");
    foreach (ISensor s in hw.Sensors.OrderBy(x => x.SensorType).ThenBy(x => x.Name))
        Line($"{indent}  {s.SensorType,-13} {s.Name,-40} {Fmt(s.Value),10}   (min {Fmt(s.Min)}, max {Fmt(s.Max)})");
    foreach (IHardware sub in hw.SubHardware)
        Dump(sub, depth + 1);
}

static string Fmt(float? v) => v.HasValue ? v.Value.ToString("0.##") : "-";

internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware sub in hardware.SubHardware)
            sub.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }
}

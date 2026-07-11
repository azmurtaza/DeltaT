using System.Management;
using System.Security.Principal;
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

// `--acer`: probe the Acer gaming WMI interface (the NitroSense channel) that
// AcerWmiFanReader rides: a raw GetGamingSysInfo sweep over sensor ids, then live
// CPU/GPU fan RPM beside the LHM CPU temperature — load the machine while it runs
// and the RPMs should ramp with the heat. Read-only throughout; needs admin
// (root\wmi denies standard users). Leaves acer-wmi-dump.txt next to the exe.
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

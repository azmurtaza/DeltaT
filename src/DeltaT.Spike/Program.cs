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

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
Line($"Kelvin sensor spike — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Line($"Elevated: {elevated}{(elevated ? "" : "   << CPU package temps + storage SMART need admin")}");
Line(new string('=', 78));

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

static string Fmt(float? v) => v.HasValue ? v.Value.ToString("0.##") : "—";

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

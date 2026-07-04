using System.Management;

namespace Kelvin.Core.Machine;

public sealed record MachineIdentity(
    string Manufacturer,
    string Model,
    string SystemFamily,
    bool IsLaptop,
    string CpuName,
    IReadOnlyList<string> GpuNames)
{
    public string Display => $"{Manufacturer} {Model}".Trim();
}

/// <summary>Who am I running on? WMI gives make/model/chassis; that feeds the
/// thermal-profile fallback chain (exact model → brand series → category).</summary>
public static class MachineIdentityProvider
{
    public static MachineIdentity Detect()
    {
        string manufacturer = "Unknown", model = "Unknown", family = "";
        bool isLaptop = false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model, SystemFamily, PCSystemType FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                manufacturer = (obj["Manufacturer"] as string)?.Trim() ?? manufacturer;
                model = (obj["Model"] as string)?.Trim() ?? model;
                family = (obj["SystemFamily"] as string)?.Trim() ?? family;
                // PCSystemType 2 = Mobile
                isLaptop = obj["PCSystemType"] is ushort t && t == 2;
            }
        }
        catch { /* WMI unavailable — identity stays generic */ }

        if (!isLaptop)
            isLaptop = HasBattery();

        return new MachineIdentity(manufacturer, model, family, isLaptop, GetCpuName(), GetGpuNames());
    }

    private static bool HasBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            return searcher.Get().Count > 0;
        }
        catch { return false; }
    }

    private static string GetCpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementBaseObject obj in searcher.Get())
                if (obj["Name"] is string name)
                    return name.Trim();
        }
        catch { }
        return "Unknown CPU";
    }

    private static IReadOnlyList<string> GetGpuNames()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementBaseObject obj in searcher.Get())
                if (obj["Name"] is string name)
                    names.Add(name.Trim());
        }
        catch { }
        return names;
    }
}

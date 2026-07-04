using Kelvin.Core.Knowledge;
using Kelvin.Core.Machine;
using Xunit;

namespace Kelvin.Core.Tests;

public class ProfileResolutionTests
{
    private static MachineIdentity Machine(string manufacturer, string model, string family = "", bool laptop = true) =>
        new(manufacturer, model, family, laptop, "Test CPU", Array.Empty<string>());

    [Fact]
    public void NitroV15_ResolvesToExactModelProfile()
    {
        ThermalProfile p = ThermalProfileProvider.Resolve(
            Machine("Acer", "Nitro ANV15-51", "Acer Nitro V 15"), hasDiscreteGpu: true);
        Assert.Equal("acer-nitro-v15", p.Id);
        Assert.NotNull(p.Cpu);
        Assert.NotNull(p.Gpu);
    }

    [Fact]
    public void OtherNitro_FallsBackToSeriesProfile()
    {
        ThermalProfile p = ThermalProfileProvider.Resolve(
            Machine("Acer", "Nitro AN515-58"), hasDiscreteGpu: true);
        Assert.Equal("acer-nitro", p.Id);
    }

    [Fact]
    public void UnknownGamingLaptop_FallsBackToCategory()
    {
        ThermalProfile p = ThermalProfileProvider.Resolve(
            Machine("Some Brand", "Mystery 9000"), hasDiscreteGpu: true);
        Assert.Equal("generic-gaming-laptop", p.Id);
    }

    [Fact]
    public void UnknownThinLaptop_WithoutDiscreteGpu_IsThinAndLight()
    {
        ThermalProfile p = ThermalProfileProvider.Resolve(
            Machine("Some Brand", "Airbook 13"), hasDiscreteGpu: false);
        Assert.Equal("thin-light-laptop", p.Id);
        Assert.Null(p.Gpu);
    }

    [Fact]
    public void Desktop_IsDesktop()
    {
        ThermalProfile p = ThermalProfileProvider.Resolve(
            Machine("Custom", "Tower", laptop: false), hasDiscreteGpu: true);
        Assert.Equal("generic-desktop", p.Id);
    }

    [Fact]
    public void LegionBeatsGenericByPriority()
    {
        ThermalProfile p = ThermalProfileProvider.Resolve(
            Machine("LENOVO", "Legion 5 15ARH7H"), hasDiscreteGpu: true);
        Assert.Equal("lenovo-legion", p.Id);
    }
}

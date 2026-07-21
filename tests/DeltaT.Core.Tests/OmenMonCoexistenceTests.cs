using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>DeltaT and OmenMon both reach the HP OMEN/Victus EC through the same signed PawnIO
/// module, and two processes on one EC session knock each other's readings out (GitHub issue #1).
/// The fix is cooperative: read fan RPM from OmenMon's pipe when it publishes, and otherwise yield
/// the EC to OmenMon entirely. These pin the pure parser and the yield gate.</summary>
public class OmenMonCoexistenceTests
{
    // --------------------------------------------------------------- pipe parse

    [Theory]
    // Canonical JSON the OmenMon side is asked to publish.
    [InlineData("{\"cpu\":4022,\"gpu\":3623}", 4022.0, 3623.0)]
    [InlineData("{\"gpu\": 3623, \"cpu\": 4022}", 4022.0, 3623.0)]   // order independent
    // Lenient spellings and separators, so a small publisher-side difference still reads.
    [InlineData("cpuFan=4022; gpuFan=3623", 4022.0, 3623.0)]
    [InlineData("CPU: 4022 RPM, GPU: 3623 RPM", 4022.0, 3623.0)]
    // A parked fan is a real 0, not absence; an unmentioned fan is null ("--" upstream).
    [InlineData("{\"cpu\":0,\"gpu\":3623}", 0.0, 3623.0)]
    [InlineData("{\"cpu\":4022}", 4022.0, null)]
    public void ParseFanData_PullsBothFansOutOfOnePublishedLine(string line, double? cpu, double? gpu)
    {
        LaptopFanSample s = OmenMonPipeFanReader.ParseFanData(line);
        Assert.Equal(cpu, s.CpuRpm);
        Assert.Equal(gpu, s.GpuRpm);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage with no numbers")]
    [InlineData(null)]
    public void ParseFanData_YieldsNoReadingWhenTheLineCarriesNoFan(string? line)
    {
        LaptopFanSample s = OmenMonPipeFanReader.ParseFanData(line);
        Assert.False(s.HasAny);
    }

    // ------------------------------------------------------------ pipe hardware gate

    [Fact]
    public void PipeReader_StaysDarkOnHardwareOmenMonNeverRunsOn()
    {
        // OmenMon only exists on HP OMEN/Victus, so on anything else the probe must rule itself out
        // rather than connect into a pipe that will never appear.
        var acer = new MachineIdentity("Acer", "Nitro AN515-45", "Nitro", IsLaptop: true, "cpu", new[] { "gpu" });
        using var probe = new OmenMonPipeFanReader(() => acer);

        Assert.False(probe.Read().HasAny);
        Assert.True(probe.IsDead);
    }

    // ----------------------------------------------------------------- EC yield

    [Fact]
    public void EcReader_YieldsTheEc_WhenOmenMonIsRunning()
    {
        // With OmenMon present the EC reader must go dark without ever opening an EC session, so
        // OmenMon keeps every reading. (An OMEN identity is injected; the conflict probe is forced.)
        var omen = new MachineIdentity("HP", "Victus by HP Gaming Laptop 15", "HP Victus",
            IsLaptop: true, "cpu", new[] { "gpu" });
        using var probe = new HpOmenEcFanReader(() => omen, ecConflictPresent: () => true);

        Assert.False(probe.Read().HasAny);
        Assert.True(probe.IsDead);
    }
}

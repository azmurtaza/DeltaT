using DeltaT.Core.Monitoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>NVML binds to a device by name so that on a multi-GPU machine the watts and the
/// temperature come from the SAME card: thermal resistance (°C/W) is meaningless if they
/// don't. These pin the matching rule, the only part of the reader that can be wrong
/// without the driver telling us.</summary>
public class NvmlGpuReaderTests
{
    [Theory]
    // LHM and NVML name the same card slightly differently.
    [InlineData("NVIDIA GeForce RTX 3050 6GB Laptop GPU", "NVIDIA GeForce RTX 3050 6GB Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 3050 6GB Laptop GPU", "GeForce RTX 3050 6GB Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 4090", true)]
    // Different cards in one box must never be paired up.
    [InlineData("NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 3050 6GB Laptop GPU", false)]
    [InlineData("NVIDIA GeForce RTX 3050 6GB Laptop GPU", "AMD Radeon RX 7900 XTX", false)]
    // Nothing to match against: stay dark rather than guess.
    [InlineData("NVIDIA GeForce RTX 3050", null, false)]
    [InlineData("", "NVIDIA GeForce RTX 3050", false)]
    public void Matches_PairsTheSameCardAndOnlyTheSameCard(string nvmlName, string? lhmName, bool expected) =>
        Assert.Equal(expected, NvmlGpuReader.Matches(nvmlName, lhmName));

    [Fact]
    public void OnAMachineWithNoNvidiaCard_TheReaderStaysDarkInsteadOfThrowing()
    {
        // No nvml.dll (an AMD or Intel machine) must degrade to "no sample", never an
        // exception on the monitor thread. Passing a non-NVIDIA name also means that even
        // where the DLL exists, it won't bind.
        using var reader = new NvmlGpuReader();

        NvmlSample sample = reader.Read("AMD Radeon RX 7900 XTX");

        Assert.Null(sample.TemperatureC);
        Assert.Null(sample.LoadPercent);
        Assert.Null(sample.PowerW);
        Assert.False(reader.IsLive);
    }
}

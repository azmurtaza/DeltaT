using DeltaT.Core.Monitoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>LHM reports an APU's integrated Radeon and a discrete Radeon under the same
/// <c>HardwareType.GpuAmd</c>. Before this rule an AMD APU on a hybrid laptop was mapped as a
/// second discrete GPU and stole the GpuDiscrete slot from the real NVIDIA card, so the GPU
/// fingerprint and score read the sensorless iGPU ("no sensor" / wrong-temp bug reports on the
/// HP Victus 8645HS + RTX 3050 and the Acer Nitro 5600H + RTX 3060). These pin the name rule.</summary>
public class GpuClassificationTests
{
    [Theory]
    // The two reported hybrid laptops: with a real discrete GPU present, the APU's Radeon must
    // read as integrated so the NVIDIA (or discrete-Radeon) card keeps the slot.
    [InlineData("AMD Radeon(TM) 760M Graphics", true, true)]
    [InlineData("AMD Radeon(TM) Graphics", true, true)]                 // Ryzen 5 5600H Vega
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", true, true)]
    [InlineData("AMD Radeon 780M", true, true)]
    // The SAME APUs on a machine with NO discrete GPU (a Ryzen ultrabook, a handheld): they
    // stay the GPU so those machines don't lose their readout. This is the surgical guard.
    [InlineData("AMD Radeon(TM) 760M Graphics", false, false)]
    [InlineData("AMD Radeon(TM) Graphics", false, false)]
    [InlineData("AMD Radeon(TM) R7 Graphics", false, false)]
    // Genuine discrete Radeon cards are never demoted, with or without another discrete sibling.
    [InlineData("AMD Radeon RX 7900 XTX", true, false)]
    [InlineData("AMD Radeon RX 6600M", true, false)]
    [InlineData("AMD Radeon RX 6700 XT", false, false)]
    [InlineData("AMD Radeon Pro W6600", false, false)]
    [InlineData("AMD Radeon VII", false, false)]
    public void IsIntegratedAmdGpu_DemotesTheApuOnlyWhenARealDiscreteGpuExists(
        string name, bool otherDiscreteGpuPresent, bool expectedIntegrated) =>
        Assert.Equal(expectedIntegrated, HardwareSensorSource.IsIntegratedAmdGpu(name, otherDiscreteGpuPresent));

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", true)]
    [InlineData("AMD Radeon RX 6600M", true)]
    [InlineData("AMD Radeon Pro W6600", true)]
    [InlineData("AMD Radeon VII", true)]
    [InlineData("AMD Radeon(TM) 760M Graphics", false)]
    [InlineData("AMD Radeon(TM) Graphics", false)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", false)]
    public void IsDiscreteAmdName_RecognisesRealCardsNotApuGraphics(string name, bool expected) =>
        Assert.Equal(expected, HardwareSensorSource.IsDiscreteAmdName(name));

    // Windows' Win32_VideoController lists a hybrid laptop's parked NVIDIA dGPU even while LHM
    // can't see it, so the OS name is the signal that keeps the APU's iGPU from stealing the
    // discrete slot (the ROG Zephyrus G14 "GPU fingerprint reads the CPU/iGPU" report).
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce GTX 1650", true)]
    [InlineData("NVIDIA RTX A2000", true)]
    [InlineData("NVIDIA Quadro T1000", true)]
    [InlineData("AMD Radeon RX 6600M", true)]           // discrete Radeon in a hybrid AMD box
    [InlineData("AMD Radeon(TM) 780M Graphics", false)] // the APU iGPU: must NOT count as discrete
    [InlineData("AMD Radeon(TM) Graphics", false)]
    [InlineData("Intel(R) UHD Graphics", false)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", false)]
    [InlineData("Microsoft Basic Display Adapter", false)]
    public void IsDiscreteGpuName_SpotsARealCardFromTheOsControllerList(string name, bool expected) =>
        Assert.Equal(expected, HardwareSensorSource.IsDiscreteGpuName(name));
}

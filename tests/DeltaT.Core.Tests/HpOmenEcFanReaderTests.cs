using DeltaT.Core.Machine;
using DeltaT.Core.Monitoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>The pure halves of HpOmenEcFanReader: the EC fan-word decode (two bytes → RPM,
/// with parked/implausible classification and endian tolerance) and the hardware gate that
/// keeps the HP register map off every machine that isn't an OMEN/Victus.</summary>
public class HpOmenEcFanReaderTests
{
    // ------------------------------------------------------------------ decode

    [Fact]
    public void AllZero_IsParked_NotAirflow()
    {
        // Omen fans stop at cool idle. That must read as a valid quiet state, not "0 rpm",
        // and must not count as a failed read (an idle machine keeps its turn until fans spin).
        Assert.Equal(HpOmenEcFanReader.FanDecode.Parked, HpOmenEcFanReader.DecodeFan(0x00, 0x00, out double rpm));
        Assert.Equal(0, rpm);
    }

    [Fact]
    public void LittleEndianWord_DecodesToRpm()
    {
        // 3000 rpm = 0x0BB8, stored low-byte-first per OmenMon: b0=0xB8, b1=0x0B.
        Assert.Equal(HpOmenEcFanReader.FanDecode.Rpm, HpOmenEcFanReader.DecodeFan(0xB8, 0x0B, out double rpm));
        Assert.Equal(3000, rpm);
    }

    [Fact]
    public void BigEndianWord_IsAcceptedWhenOnlyItIsPlausible()
    {
        // Same 3000 rpm stored high-byte-first: b0=0x0B, b1=0xB8. Little-endian would read
        // 0xB80B (47115, implausible), so the reader falls back to the big-endian value.
        Assert.Equal(HpOmenEcFanReader.FanDecode.Rpm, HpOmenEcFanReader.DecodeFan(0x0B, 0xB8, out double rpm));
        Assert.Equal(3000, rpm);
    }

    [Fact]
    public void NonZeroButOutOfRange_IsImplausible()
    {
        // 0xFFFF both ways is far above any fan: wrong register map or wrong machine.
        Assert.Equal(HpOmenEcFanReader.FanDecode.Implausible, HpOmenEcFanReader.DecodeFan(0xFF, 0xFF, out double rpm));
        Assert.Equal(0, rpm);
    }

    [Fact]
    public void LoneLowByte_IsImplausible_NotTreatedAsTinyFan()
    {
        // A single low byte (0xB8 = 184) is below the spin floor in either order, so it is not
        // mistaken for a fan crawling at 184 rpm.
        Assert.Equal(HpOmenEcFanReader.FanDecode.Implausible, HpOmenEcFanReader.DecodeFan(0xB8, 0x00, out _));
    }

    // -------------------------------------------------------------------- gate

    [Theory]
    [InlineData("HP", "OMEN by HP Laptop 16", "103C_53311M HP OMEN", true)]
    [InlineData("HP", "Victus by HP Gaming Laptop", "103C_53311M HP Victus", true)]
    [InlineData("Hewlett-Packard", "OMEN 15", "", true)]
    [InlineData("HP", "HP EliteBook 840 G9", "103C_53307F HP EliteBook", false)] // business HP: WMI path, not EC
    [InlineData("Acer", "Nitro ANV15-51", "Nitro", false)]                       // never poke another vendor's EC
    [InlineData("LENOVO", "Legion 5", "Legion", false)]
    public void Gate_LatchesOnlyOmenOrVictus(string mfr, string model, string family, bool expected)
    {
        var id = new MachineIdentity(mfr, model, family, IsLaptop: true, "cpu", new[] { "gpu" });
        Assert.Equal(expected, HpOmenEcFanReader.IsHpOmenOrVictus(id));
    }

    [Fact]
    public void NonHpOmenHardware_RulesItselfDeadWithoutTouchingTheEc()
    {
        // With a non-HP identity the probe must go dark on the first Read and never construct an
        // EC reader — the strongest safety guarantee (no EC access on hardware we don't understand).
        var id = new MachineIdentity("Acer", "Nitro ANV15-51", "Nitro", IsLaptop: true, "cpu", new[] { "gpu" });
        using var probe = new HpOmenEcFanReader(() => id);
        LaptopFanSample sample = probe.Read();
        Assert.False(sample.HasAny);
        Assert.True(probe.IsDead);
    }
}

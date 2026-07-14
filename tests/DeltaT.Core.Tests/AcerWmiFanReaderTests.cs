using DeltaT.Core.Monitoring;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>The pure protocol half of AcerWmiFanReader: a GetGamingSysInfo result
/// is status in bits 0-7 (0 = the sensor answered) and the reading in bits 8-23.</summary>
public class AcerWmiFanReaderTests
{
    [Fact]
    public void NonzeroStatus_MeansNoAnswer()
    {
        Assert.False(AcerWmiFanReader.TryDecode((2800UL << 8) | 0x01, out double? rpm));
        Assert.Null(rpm);
    }

    [Fact]
    public void Rpm_IsReadFromBits8To23()
    {
        Assert.True(AcerWmiFanReader.TryDecode(2800UL << 8, out double? rpm));
        Assert.Equal(2800.0, rpm);
    }

    [Fact]
    public void ParkedFan_IsValidAnswer_ButNotAirflowData()
    {
        // Nitro/Predator fans stop at cool idle: status OK, value 0. That must not
        // read as "0 rpm airflow" (never fake data) nor count as a sensor failure.
        Assert.True(AcerWmiFanReader.TryDecode(0UL, out double? rpm));
        Assert.Null(rpm);
    }

    [Fact]
    public void CpuTemperature_DecodesFromTheSameFrame()
    {
        // Sensor id 0x01 is the CPU temperature NitroSense shows, and it needs no kernel
        // driver — it's the fallback for a machine with no PawnIO. Same frame layout as the
        // fan ids: the dev ANV15-51 answered 0x4000 (64 °C) while the package MSR read 64.
        Assert.True(AcerWmiFanReader.TryDecodeValue(0x4000UL, out double value));
        Assert.Equal(64.0, value);
    }

    [Fact]
    public void UnansweredTemperature_IsNotZeroDegrees()
    {
        // A non-zero status byte means the firmware didn't answer. Reading that frame's
        // payload as "0 °C" would hand scoring an ice-cold CPU.
        Assert.False(AcerWmiFanReader.TryDecodeValue((64UL << 8) | 0x02, out _));
    }

    [Fact]
    public void GarbageAboveBit23_IsIgnored()
    {
        ulong raw = (0xDEADBEEFUL << 24) | (3200UL << 8);
        Assert.True(AcerWmiFanReader.TryDecode(raw, out double? rpm));
        Assert.Equal(3200.0, rpm);
    }

    [Fact]
    public void ImplausibleRpm_IsDropped()
    {
        Assert.True(AcerWmiFanReader.TryDecode(0xFFFFUL << 8, out double? rpm));
        Assert.Null(rpm);
    }
}

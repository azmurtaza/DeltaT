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

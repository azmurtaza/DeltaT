using DeltaT.Core.Monitoring;
using Xunit;

namespace DeltaT.Core.Tests;

public class LenovoWmiFanReaderTests
{
    [Theory]
    [InlineData(0, null)]        // parked fan: a valid answer, but not airflow data
    [InlineData(-1, null)]       // garbage
    [InlineData(10000, null)]    // out of range
    [InlineData(2800, 2800.0)]   // real RPM passes through directly
    public void PlausibleRpm_KeepsOnlyRealAirflow(int raw, double? expected) =>
        Assert.Equal(expected, LenovoWmiFanReader.PlausibleRpm(raw));
}

public class AsusWmiFanReaderTests
{
    [Theory]
    [InlineData(0, null)]           // parked fan
    [InlineData(28, 2800.0)]        // status word carries hundreds of RPM
    [InlineData(0x00010028, 4000.0)] // high half is status flags, not speed: masked off
    [InlineData(100, null)]         // 10000 rpm: out of range
    public void RpmFromStatus_MasksAndScalesTheStatusWord(long status, double? expected) =>
        Assert.Equal(expected, AsusWmiFanReader.RpmFromStatus(status));
}

public class HpWmiFanReaderTests
{
    [Theory]
    [InlineData("GPU Fan", true)]
    [InlineData("gpu0 fan", true)]
    [InlineData("CPU0 Fan", false)]
    [InlineData("Fan 1", false)]   // single-fan chassis: defaults to the CPU side
    [InlineData(null, false)]
    public void IsGpuFan_ReadsTheSensorLabel(string? name, bool expected) =>
        Assert.Equal(expected, HpWmiFanReader.IsGpuFan(name));

    [Theory]
    [InlineData(0.0, null)]         // parked fan
    [InlineData(2500.0, 2500.0)]
    [InlineData(12000.0, null)]     // out of range
    [InlineData(null, null)]        // sensor present but reading absent
    public void PlausibleRpm_KeepsOnlyRealAirflow(double? raw, double? expected) =>
        Assert.Equal(expected, HpWmiFanReader.PlausibleRpm(raw));
}

public class LaptopFanReaderCoordinatorTests
{
    private sealed class FakeProbe : ILaptopFanProbe
    {
        private readonly Queue<LaptopFanSample> _samples;
        private readonly LaptopFanSample _steady;
        public FakeProbe(string vendor, LaptopFanSample steady, params LaptopFanSample[] leadIn)
        {
            Vendor = vendor;
            _steady = steady;
            _samples = new Queue<LaptopFanSample>(leadIn);
        }
        public string Vendor { get; }
        public bool IsDead { get; private set; }
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public void MarkDead() => IsDead = true;
        public LaptopFanSample Read()
        {
            Reads++;
            return _samples.Count > 0 ? _samples.Dequeue() : _steady;
        }
        public void Dispose() { Disposed = true; IsDead = true; }
    }

    [Fact]
    public void FirstProbeWithAirflow_WinsAndOthersAreDisposed()
    {
        var acer = new FakeProbe("Acer", default);                          // never answers
        var lenovo = new FakeProbe("Lenovo", new LaptopFanSample(2600, 2400));
        var reader = new LaptopFanReader(new ILaptopFanProbe[] { acer, lenovo });

        LaptopFanSample s = reader.Read();

        Assert.Equal(2600, s.CpuRpm);
        Assert.Equal("Lenovo", reader.ActiveVendor);
        Assert.True(acer.Disposed);            // the loser is released immediately
        Assert.False(lenovo.Disposed);
    }

    [Fact]
    public void OnceLatched_OnlyTheWinnerIsPolled()
    {
        var acer = new FakeProbe("Acer", new LaptopFanSample(3000, null));
        var lenovo = new FakeProbe("Lenovo", new LaptopFanSample(2600, 2400));
        var reader = new LaptopFanReader(new ILaptopFanProbe[] { acer, lenovo });

        reader.Read();          // Acer wins on the first airflow sample
        int lenovoReadsAfterWin = lenovo.Reads;
        reader.Read();
        reader.Read();

        Assert.Equal("Acer", reader.ActiveVendor);
        Assert.Equal(lenovoReadsAfterWin, lenovo.Reads);   // loser never polled again
        Assert.Equal(3, acer.Reads);
    }

    [Fact]
    public void ParkedFans_DoNotLatchAWinner()
    {
        // Both probes answer, but with parked fans (no airflow) on the first sample,
        // then the right one spins up. The coordinator must not commit early.
        var lenovo = new FakeProbe("Lenovo", new LaptopFanSample(2600, 2400), new LaptopFanSample(null, null));
        var reader = new LaptopFanReader(new ILaptopFanProbe[] { lenovo });

        Assert.False(reader.Read().HasAny);       // parked: no winner yet
        Assert.Null(reader.ActiveVendor);
        Assert.True(reader.Read().HasAny);         // spun up: latches now
        Assert.Equal("Lenovo", reader.ActiveVendor);
    }

    [Fact]
    public void DeadProbes_AreSkipped_AndReaderStaysDark()
    {
        var acer = new FakeProbe("Acer", default);
        acer.MarkDead();
        var reader = new LaptopFanReader(new ILaptopFanProbe[] { acer });

        Assert.False(reader.Read().HasAny);
        Assert.Null(reader.ActiveVendor);
        Assert.Equal(0, acer.Reads);              // a dead probe is never polled
    }
}

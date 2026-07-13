using DeltaT.Core.Knowledge;
using Xunit;

namespace DeltaT.Core.Tests;

public class ProfileExpectationTests
{
    [Fact]
    public void ColdOutsideHoldsTheRoomAnchor()
    {
        // 0 °C outside must not lower the expectation: silicon idles against its
        // own heat floor, so a healthy die still sits near room-anchored figures.
        Assert.Equal(45, ProfileExpectation.ExpectedTempC(20, 0, 98));
        Assert.Equal(91, ProfileExpectation.ExpectedTempC(66, -10, 98));
    }

    [Fact]
    public void WarmOutsideRaisesOneToOne()
    {
        Assert.Equal(55, ProfileExpectation.ExpectedTempC(20, 35, 98));
        Assert.Equal(45.5, ProfileExpectation.ExpectedTempC(20, 25.5, 98));
    }

    [Fact]
    public void UnknownAmbientUsesTheReferenceRoom()
    {
        Assert.Equal(ProfileExpectation.ReferenceAmbientC + 66,
            ProfileExpectation.ExpectedTempC(66, null, 98));
    }

    [Fact]
    public void ExpectationNeverExceedsConcern()
    {
        // 40 °C outside + 66° heavy rise = 106, but the silicon throttles at its
        // limit instead of climbing — cap at the chassis concern threshold.
        Assert.Equal(98, ProfileExpectation.ExpectedTempC(66, 40, 98));
    }

    [Fact]
    public void ExactlyReferenceAmbientIsUnchanged()
    {
        Assert.Equal(45, ProfileExpectation.ExpectedTempC(20, 25, 98));
    }
}

using DeltaT.Core.Updates;
using Xunit;

namespace DeltaT.Core.Tests;

/// <summary>The loop breaker. A self-update hands off to an installer and gets relaunched
/// either way, so an install that cannot succeed used to repeat on every launch forever.
/// These lock the rule that stops it.</summary>
public class UpdateAttemptGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Version Release = new(2, 4, 0);

    [Fact]
    public void AVersionNeverTried_IsAllowedThrough()
    {
        Assert.Equal(UpdateAttempt.Proceed,
            UpdateAttemptGate.Evaluate(null, 0, null, Release, Now));
    }

    [Fact]
    public void ADifferentVersionsHistory_DoesNotBlockANewRelease()
    {
        Assert.Equal(UpdateAttempt.Proceed,
            UpdateAttemptGate.Evaluate(new Version(2, 3, 0), 9, Now.AddHours(-1), Release, Now));
    }

    [Fact]
    public void OneFailedAttempt_GetsASecondChance()
    {
        Assert.Equal(UpdateAttempt.Proceed,
            UpdateAttemptGate.Evaluate(Release, 1, Now.AddMinutes(-1), Release, Now));
    }

    [Fact]
    public void TwoFailedAttempts_StopTheLoop()
    {
        Assert.Equal(UpdateAttempt.Failing,
            UpdateAttemptGate.Evaluate(Release, 2, Now.AddMinutes(-1), Release, Now));
        // and it stays stopped, however many restarts the helper manages
        Assert.Equal(UpdateAttempt.Failing,
            UpdateAttemptGate.Evaluate(Release, 40, Now.AddMinutes(-1), Release, Now));
    }

    [Fact]
    public void AfterTheRetryWindow_ItTriesOnceMore()
    {
        DateTimeOffset stale = Now - UpdateAttemptGate.RetryAfter - TimeSpan.FromMinutes(1);
        Assert.Equal(UpdateAttempt.Proceed,
            UpdateAttemptGate.Evaluate(Release, 5, stale, Release, Now));
    }

    [Fact]
    public void ARecordWithNoTimestamp_StillBlocks()
    {
        // Belt and braces: a half-written record must not read as "the window has passed".
        Assert.Equal(UpdateAttempt.Failing,
            UpdateAttemptGate.Evaluate(Release, 2, null, Release, Now));
    }

    [Theory]
    [InlineData("2.4.0.0", true)]   // the assembly version the tag names
    [InlineData("2.4.1.0", true)]   // overtaken by a later manual install
    [InlineData("2.3.0.0", false)]  // still the old build: the install did not take
    public void Succeeded_ComparesTheTagAgainstTheRunningAssembly(string running, bool expected)
    {
        Assert.Equal(expected, UpdateAttemptGate.Succeeded(Release, Version.Parse(running)));
    }

    [Fact]
    public void Succeeded_IsFalseWithNothingRecorded()
    {
        Assert.False(UpdateAttemptGate.Succeeded(null, new Version(2, 4, 0, 0)));
    }
}

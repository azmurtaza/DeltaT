using DeltaT.Core.Updates;
using Xunit;

namespace DeltaT.Core.Tests;

public class WhatsNewGateTests
{
    // The version the curated notes are keyed to. Kept in one place so the tests track the
    // notes list without hard-coding the number in every assertion.
    private static Version Announced => WhatsNewNotes.Releases[^1].Version;

    [Fact]
    public void FreshInstall_NeverShows()
    {
        // First-ever run: onboarding is the welcome, not a changelog — even into an announced version.
        Assert.Null(WhatsNewGate.Evaluate(Announced, lastShown: null, firstRun: true));
    }

    [Fact]
    public void UpgradeIntoAnnouncedVersion_Shows()
    {
        WhatsNewRelease? notes = WhatsNewGate.Evaluate(Announced, lastShown: "2.1.0", firstRun: false);
        Assert.NotNull(notes);
        Assert.Equal(Announced, notes!.Version);
    }

    [Fact]
    public void ExistingUser_FirstBuildWithTheKey_Shows()
    {
        // An existing (onboarded) user whose store has no recorded version yet, arriving on the
        // announced build, is a genuine upgrade and should see it.
        Assert.NotNull(WhatsNewGate.Evaluate(Announced, lastShown: null, firstRun: false));
    }

    [Fact]
    public void AlreadyShownForThisVersion_DoesNotRepeat()
    {
        string key = WhatsNewGate.VersionKey(Announced);
        Assert.Null(WhatsNewGate.Evaluate(Announced, lastShown: key, firstRun: false));
    }

    [Fact]
    public void Downgrade_DoesNotShow()
    {
        var older = new Version(Announced.Major, Announced.Minor - 1, 0);
        Assert.Null(WhatsNewGate.Evaluate(older, lastShown: WhatsNewGate.VersionKey(Announced), firstRun: false));
    }

    [Fact]
    public void VersionWithoutNotes_DoesNotShow()
    {
        // A patch release with no curated entry: nothing to announce, so nothing pops.
        var noNotes = new Version(Announced.Major, Announced.Minor, Announced.Build + 7);
        Assert.Null(WhatsNewNotes.For(noNotes));
        Assert.Null(WhatsNewGate.Evaluate(noNotes, lastShown: WhatsNewGate.VersionKey(Announced), firstRun: false));
    }

    [Fact]
    public void VersionKey_DropsRevision()
    {
        Assert.Equal("2.2.0", WhatsNewGate.VersionKey(new Version(2, 2, 0, 4)));
    }
}

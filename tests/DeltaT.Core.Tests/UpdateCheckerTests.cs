using DeltaT.Core.Updates;
using Xunit;

namespace DeltaT.Core.Tests;

public class UpdateCheckerTests
{
    private static string Release(string tag, bool draft = false, bool prerelease = false, bool withAsset = true)
    {
        string assets = withAsset
            ? $$"""[{"name":"DeltaT-Setup-{{tag.TrimStart('v')}}.exe","browser_download_url":"https://example.com/DeltaT-Setup.exe"}]"""
            : "[]";
        return $$"""
            {"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},
             "html_url":"https://github.com/azmurtaza/DeltaT/releases/tag/{{tag}}","body":"notes here","assets":{{assets}}}
            """;
    }

    [Fact]
    public void NewerVersion_WithInstaller_IsOffered()
    {
        ReleaseInfo? info = UpdateChecker.ParseLatest(Release("v1.1.0"), new Version(1, 0, 8));
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 1, 0), info!.Version);
        Assert.Equal("https://example.com/DeltaT-Setup.exe", info.DownloadUrl);
        Assert.Contains("notes", info.Notes);
    }

    [Theory]
    [InlineData("v1.0.8")]  // same version
    [InlineData("v1.0.7")]  // older
    public void SameOrOlderVersion_IsNotOffered(string tag) =>
        Assert.Null(UpdateChecker.ParseLatest(Release(tag), new Version(1, 0, 8)));

    [Fact]
    public void DraftsAndPrereleases_AreIgnored()
    {
        Assert.Null(UpdateChecker.ParseLatest(Release("v2.0.0", draft: true), new Version(1, 0, 8)));
        Assert.Null(UpdateChecker.ParseLatest(Release("v2.0.0", prerelease: true), new Version(1, 0, 8)));
    }

    [Fact]
    public void NewerVersion_WithoutInstallerAsset_IsNotOffered() =>
        Assert.Null(UpdateChecker.ParseLatest(Release("v1.1.0", withAsset: false), new Version(1, 0, 8)));

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3-beta", 1, 2, 3)]
    public void ParseVersion_HandlesTagShapes(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), UpdateChecker.ParseVersion(tag));

    [Fact]
    public void ParseVersion_Garbage_IsNull() => Assert.Null(UpdateChecker.ParseVersion("not-a-version"));
}

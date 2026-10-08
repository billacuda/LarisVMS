using System.Text.Json;
using LarisVMS.Core;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public sealed class ReleaseCheckTests
{
    [Theory]
    [InlineData("v0.213.1-beta", "0.213.1")]
    [InlineData("0.214.0", "0.214.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.214.0+f19db159a7", "0.214.0")]
    [InlineData("0.188.0.244", "0.188.0.244")]
    public void ReleaseVersion_ParsesTagsAndBuildStrings(string input, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(input, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void ReleaseVersion_RejectsJunk(string? input) => Assert.False(ReleaseVersion.TryParse(input, out _));

    private static JsonElement Releases(string json) => JsonDocument.Parse(json).RootElement;

    private const string Sample = """
        [
          { "tag_name": "v0.216.0-beta", "html_url": "https://github.com/billacuda/LarisVMS/releases/tag/v0.216.0-beta", "draft": true,  "prerelease": false },
          { "tag_name": "v0.215.0-beta", "html_url": "https://github.com/billacuda/LarisVMS/releases/tag/v0.215.0-beta", "draft": false, "prerelease": true },
          { "tag_name": "v0.213.1-beta", "html_url": "https://github.com/billacuda/LarisVMS/releases/tag/v0.213.1-beta", "draft": false, "prerelease": false },
          { "tag_name": "nightly",       "html_url": "https://github.com/billacuda/LarisVMS/releases/tag/nightly",       "draft": false, "prerelease": true }
        ]
        """;

    [Fact]
    public void PicksTheHighestNonDraftRelease_IncludingPreReleases()
    {
        var found = ReleaseCheckService.FindNewerRelease(Releases(Sample), "0.214.0+f19db159");

        Assert.NotNull(found);
        Assert.Equal("0.215.0", found!.Version);
        Assert.Equal("https://github.com/billacuda/LarisVMS/releases/tag/v0.215.0-beta", found.Url);
    }

    [Theory]
    [InlineData("0.215.0")]
    [InlineData("0.216.0")]
    public void NoNoticeWhenRunningTheSameOrNewer(string running) =>
        Assert.Null(ReleaseCheckService.FindNewerRelease(Releases(Sample), running));

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void NoNoticeWhenTheRunningVersionIsUnknown(string? running) =>
        Assert.Null(ReleaseCheckService.FindNewerRelease(Releases(Sample), running));

    [Fact]
    public void IgnoresALinkOutsideTheLarisVMSReleases()
    {
        var json = """[ { "tag_name": "v9.0.0", "html_url": "https://example.com/evil", "draft": false } ]""";
        Assert.Null(ReleaseCheckService.FindNewerRelease(Releases(json), "0.214.0"));
    }

    [Fact]
    public void NoNoticeForAnEmptyOrUnexpectedResponse()
    {
        Assert.Null(ReleaseCheckService.FindNewerRelease(Releases("[]"), "0.214.0"));
        Assert.Null(ReleaseCheckService.FindNewerRelease(Releases("""{ "message": "API rate limit exceeded" }"""), "0.214.0"));
    }
}

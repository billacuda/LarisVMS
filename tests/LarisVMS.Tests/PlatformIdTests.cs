using System.Runtime.InteropServices;
using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>The platform string is matched against approved builds and covered by update signatures,
/// so the node's and the server's spelling must agree exactly.</summary>
public sealed class PlatformIdTests
{
    [Theory]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    public void FormatsRuntimeIdentifiers(string os, Architecture architecture, string expected) =>
        Assert.Equal(expected, PlatformId.For(os, architecture));

    [Fact]
    public void CurrentMatchesThisOs()
    {
        Assert.Equal(OperatingSystem.IsWindows(), PlatformId.IsWindows(PlatformId.Current));
        Assert.EndsWith(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64" : "-x64", PlatformId.Current);
    }

    [Theory]
    [InlineData("win-x64", "proxy-win-x64")]
    [InlineData("linux-x64", "proxy-linux-x64")]
    [InlineData("proxy-linux-x64", "proxy-linux-x64")]
    public void ProxyPlatformsArePrefixedOnce(string platform, string expected) =>
        Assert.Equal(expected, PlatformId.ForProxy(platform));

    [Theory]
    [InlineData("linux-x64", "linux-x64")]
    [InlineData(" Win-X64 ", "win-x64")]
    [InlineData("linux-arm64", "linux-arm64")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("windows", null)]
    [InlineData("proxy-linux-x64", null)]
    [InlineData("linux-x64; DROP", null)]
    [InlineData("-x64", null)]
    public void SanitizesReportedPlatforms(string? reported, string? expected) =>
        Assert.Equal(expected, PlatformId.Sanitize(reported));

    [Theory]
    [InlineData("proxy-linux-x64", "linux-x64", false)]
    [InlineData("win-x64", "win-x64", true)]
    [InlineData("proxy-win-x64", "win-x64", true)]
    public void SplitsTheOsBackOut(string platform, string os, bool windows)
    {
        Assert.Equal(os, PlatformId.OsOf(platform));
        Assert.Equal(windows, PlatformId.IsWindows(platform));
    }
}

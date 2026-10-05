using System.Net;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class IpAllowListPolicyTests
{
    [Fact]
    public void ParseTreatsABareIpAsAFullHostNetwork()
    {
        var result = IpAllowListPolicy.Parse("10.0.0.5");

        Assert.Empty(result.InvalidLines);
        var network = Assert.Single(result.Networks);
        Assert.Equal(32, network.PrefixLength);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), network.BaseAddress);
    }

    [Fact]
    public void ParseAcceptsACidrBlock()
    {
        var result = IpAllowListPolicy.Parse("10.0.0.0/24");

        Assert.Empty(result.InvalidLines);
        var network = Assert.Single(result.Networks);
        Assert.Equal(24, network.PrefixLength);
    }

    [Fact]
    public void ParseSkipsBlankAndCommentLines()
    {
        var result = IpAllowListPolicy.Parse("10.0.0.5\n\n  \n# a comment\n#192.168.1.1");

        Assert.Single(result.Networks);
        Assert.Empty(result.InvalidLines);
    }

    [Fact]
    public void ParseCollectsInvalidLinesRatherThanDroppingThem()
    {
        var result = IpAllowListPolicy.Parse("10.0.0.5\nnot-an-ip\n10.0.0.0/999");

        Assert.Single(result.Networks);
        Assert.Equal(2, result.InvalidLines.Count);
        Assert.Contains("not-an-ip", result.InvalidLines);
        Assert.Contains("10.0.0.0/999", result.InvalidLines);
    }

    [Fact]
    public void IsAllowedIsOpenByDefaultWithAnEmptyList()
    {
        Assert.True(IpAllowListPolicy.IsAllowed(IPAddress.Parse("203.0.113.9"), []));
        Assert.True(IpAllowListPolicy.IsAllowed(null, []));
    }

    [Fact]
    public void IsAllowedMatchesAnAddressInsideAConfiguredCidr()
    {
        var networks = IpAllowListPolicy.Parse("10.0.0.0/24").Networks;

        Assert.True(IpAllowListPolicy.IsAllowed(IPAddress.Parse("10.0.0.42"), networks));
        Assert.False(IpAllowListPolicy.IsAllowed(IPAddress.Parse("10.0.1.1"), networks));
    }

    [Fact]
    public void IsAllowedRejectsANullRemoteIpWhenAListIsConfigured()
    {
        var networks = IpAllowListPolicy.Parse("10.0.0.0/24").Networks;

        Assert.False(IpAllowListPolicy.IsAllowed(null, networks));
    }

    [Fact]
    public void IsAllowedUnmapsAnIPv4MappedIPv6AddressBeforeMatching()
    {
        var networks = IpAllowListPolicy.Parse("10.0.0.0/24").Networks;
        var mapped = IPAddress.Parse("::ffff:10.0.0.42");

        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(IpAllowListPolicy.IsAllowed(mapped, networks));
    }

    [Fact]
    public void IsAllowedNeverMatchesAcrossMismatchedAddressFamilies()
    {
        var networks = IpAllowListPolicy.Parse("10.0.0.0/24").Networks;
        var ipv6 = IPAddress.Parse("2001:db8::1");

        Assert.False(IpAllowListPolicy.IsAllowed(ipv6, networks));
    }

    [Theory]
    [InlineData("::ffff:10.0.0.42", "10.0.0.42")]
    [InlineData("::1", "127.0.0.1")]
    [InlineData("10.0.0.42", "10.0.0.42")]
    [InlineData("2001:db8::1", "[2001:db8::1]")]
    public void ToUrlHostReturnsAHostUsableInAPlainUrl(string remote, string expected)
    {
        var host = IpAllowListPolicy.ToUrlHost(IPAddress.Parse(remote));

        Assert.Equal(expected, host);
        Assert.True(Uri.TryCreate($"ws://{host}:8554/live/1", UriKind.Absolute, out _));
    }
}

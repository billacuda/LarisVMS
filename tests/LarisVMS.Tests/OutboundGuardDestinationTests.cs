using System.Net;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Tests;

/// <summary>
/// The outbound guard judges an IPv6 address that carries an IPv4 one by the IPv4 address, and,
/// behind a proxy, still checks the real destination.
/// </summary>
public class OutboundGuardDestinationTests
{
    [Theory]
    [InlineData("64:ff9b::a9fe:a9fe", false)]     // NAT64 of 169.254.169.254 (cloud metadata)
    [InlineData("64:ff9b::7f00:1", false)]        // NAT64 of 127.0.0.1
    [InlineData("2002:7f00:1::", false)]          // 6to4 of 127.0.0.1
    [InlineData("::10.0.0.5", false)]             // IPv4-compatible 10.0.0.5
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("64:ff9b::808:808", true)]        // NAT64 of 8.8.8.8
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::1", false)]
    public void WrappedIPv4AddressesAreJudgedByWhereTheyGo(string address, bool isPublic) =>
        Assert.Equal(isPublic, OutboundGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void CloudMetadataThroughNat64IsntACameraAddress() =>
        Assert.False(OutboundGuard.IsCameraAddress(IPAddress.Parse("64:ff9b::a9fe:a9fe")));

    [Theory]
    [InlineData("169.254.169.254", false)]
    [InlineData("[::1]", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("localhost", false)]
    public async Task ThroughAProxyTheRealDestinationIsStillChecked(string host, bool allowed) =>
        Assert.Equal(allowed, await OutboundGuard.DestinationAllowedAsync(host, OutboundGuard.IsPublic, CancellationToken.None));
}

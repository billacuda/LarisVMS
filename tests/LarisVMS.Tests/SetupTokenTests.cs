using System.Net;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class SetupTokenTests
{
    [Theory]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("127.0.0.1", true, false)]   // a proxy on the server: the browser is somewhere else
    [InlineData("192.168.1.20", false, false)]
    [InlineData(null, false, false)]
    public void OnlyABrowserOnTheServerItselfSkipsTheToken(string? remote, bool forwarded, bool local) =>
        Assert.Equal(local, SetupToken.IsLocalRequest(remote is null ? null : IPAddress.Parse(remote), forwarded));

    [Theory]
    [InlineData("127.0.0.1", false, false, true)]
    [InlineData("192.168.1.20", false, false, true)]
    [InlineData("10.4.5.6", false, false, true)]
    [InlineData("172.20.0.9", false, false, true)]
    [InlineData("172.32.0.9", false, false, false)]   // just outside 172.16/12
    [InlineData("169.254.3.4", false, false, true)]
    [InlineData("::ffff:192.168.1.20", false, false, true)]
    [InlineData("fd12:3456::1", false, false, true)]
    [InlineData("fe80::1", false, false, true)]
    [InlineData("203.0.113.7", false, false, false)]  // the internet asks for the token
    [InlineData("2001:db8::1", false, false, false)]
    [InlineData("192.168.1.20", true, false, false)]  // through a proxy: the browser could be anywhere
    [InlineData("192.168.1.20", false, true, false)]  // AlwaysRequireToken
    [InlineData("127.0.0.1", false, true, true)]      // the server itself never needs it
    [InlineData(null, false, false, false)]
    public void TheLocalNetworkSkipsTheTokenUnlessItsAlwaysRequired(string? remote, bool forwarded, bool alwaysRequire, bool trusted) =>
        Assert.Equal(trusted, SetupToken.IsTrustedRequest(remote is null ? null : IPAddress.Parse(remote), forwarded, alwaysRequire));

    [Theory]
    [InlineData("Via")]
    [InlineData("Forwarded")]
    [InlineData("X-Real-IP")]
    [InlineData("X-Original-For")]
    [InlineData("CF-Connecting-IP")]
    public void AnyProxyHeaderMeansTheBrowserIsElsewhere(string header)
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary { [header] = "1" };
        Assert.True(SetupToken.CameThroughProxy(headers));
        Assert.False(SetupToken.CameThroughProxy(new Microsoft.AspNetCore.Http.HeaderDictionary { ["User-Agent"] = "x" }));
    }

    [Fact]
    public void TheVisionChannelNeedsTheNodesSecret()
    {
        var secret = LarisVMS.Core.Security.LocalControlSecret.New();
        Assert.True(LarisVMS.Core.Security.LocalControlSecret.Matches(secret, secret));
        Assert.False(LarisVMS.Core.Security.LocalControlSecret.Matches(secret, LarisVMS.Core.Security.LocalControlSecret.New()));
        Assert.False(LarisVMS.Core.Security.LocalControlSecret.Matches(secret, null));
        Assert.False(LarisVMS.Core.Security.LocalControlSecret.Matches("", ""));   // no secret given: nothing gets in
    }

    [Theory]
    [InlineData("abcde-fghjk-mnpqr-stuvw", "ABCDEFGHJKMNPQRSTUVW")]
    [InlineData(" ABCDE FGHJK\r\nMNPQR-STUVW ", "ABCDEFGHJKMNPQRSTUVW")]
    public void TheTokenIsTakenHoweverItsTypedOrPasted(string typed, string normalized) =>
        Assert.Equal(normalized, SetupToken.Normalize(typed));
}

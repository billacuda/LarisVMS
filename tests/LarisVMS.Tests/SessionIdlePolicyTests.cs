using Microsoft.AspNetCore.Http;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class SessionIdlePolicyTests
{
    [Theory]
    [InlineData(new int[0], null)]
    [InlineData(new[] { 0, 0 }, null)]
    [InlineData(new[] { 0, 30 }, 30)]
    [InlineData(new[] { 60, 15, 0 }, 15)]
    [InlineData(new[] { 99999 }, SessionIdlePolicy.MaxMinutes)]
    [InlineData(new[] { -5 }, null)]
    public void TheShortestTimeoutAnyRoleSetsApplies(int[] perRole, int? expectedMinutes) =>
        Assert.Equal(expectedMinutes is { } m ? TimeSpan.FromMinutes(m) : null, SessionIdlePolicy.EffectiveTimeout(perRole));

    [Fact]
    public void ASessionIsIdleOnlyPastTheTimeout()
    {
        var last = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var timeout = TimeSpan.FromMinutes(15);
        Assert.False(SessionIdlePolicy.IsIdle(last, last.AddMinutes(15), timeout));
        Assert.True(SessionIdlePolicy.IsIdle(last, last.AddMinutes(16), timeout));
        Assert.False(SessionIdlePolicy.IsIdle(last, last.AddDays(30), null));
    }

    [Theory]
    [InlineData("GET", "/", true)]
    [InlineData("GET", "/Cameras", true)]
    [InlineData("POST", "/Admin/Settings/Users", true)]
    [InlineData("GET", "/api/notifications", false)]
    [InlineData("POST", "/api/cameras/1/ptz", false)]
    [InlineData("GET", "/css/site.css", false)]
    [InlineData("GET", "/_blazor/negotiate", false)]
    [InlineData("HEAD", "/", false)]
    public void OnlyOpeningPagesAndSendingFormsCountsAsActivity(string method, string path, bool activity) =>
        Assert.Equal(activity, SessionIdlePolicy.IsActivity(method, new PathString(path)));

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15", "Safari · macOS")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0 Mobile/15E148 Safari/604.1", "Chrome · iOS")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox · Linux")]
    [InlineData("curl/8.4.0", "")]
    [InlineData(null, "")]
    public void BrowsersAreNamedForTheSessionsList(string? userAgent, string expected) =>
        Assert.Equal(expected, SessionIdlePolicy.DescribeBrowser(userAgent));
}

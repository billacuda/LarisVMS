using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>Failover plan phase 1: MediaRoutingService.ResolveAsync — proxy is the safe default, and
/// direct only when the node has demonstrably stood up a healthy client endpoint.</summary>
public class MediaRoutingServiceTests
{
    private static MediaRoutingService Make(string? globalDirect = null, bool? globalInsecure = null)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var settings = new SettingsResolver(db);
        if (globalDirect is not null) settings.SetGlobalAsync("LiveView.DirectStreaming", globalDirect, "test").GetAwaiter().GetResult();
        if (globalInsecure is not null) settings.SetGlobalAsync("LiveView.AllowInsecureClientEndpoint", globalInsecure.Value.ToString(), "test").GetAwaiter().GetResult();
        return new MediaRoutingService(settings, db);
    }

    private static MediaRouteInputs Healthy(string? mode = "Direct", bool? insecureOverride = null, bool selfSigned = false, string? lastError = null)
        => new(mode, insecureOverride, "node1.example.com", 8555, DateTime.UtcNow.AddYears(1), selfSigned, lastError);

    [Fact]
    public async Task ProxyWhenGlobalIsProxyAndNoNodeOverride()
    {
        var route = await Make(globalDirect: "Proxy").ResolveAsync(Healthy(mode: null));
        Assert.Equal(MediaStreamMode.Proxy, route.Mode);
    }

    [Fact]
    public async Task DirectWhenGlobalDirectAndEndpointHealthy()
    {
        var route = await Make(globalDirect: "Direct").ResolveAsync(Healthy(mode: null));
        Assert.Equal(MediaStreamMode.Direct, route.Mode);
        Assert.Equal("node1.example.com", route.DirectHost);
        Assert.Equal(8555, route.DirectPort);
        Assert.False(route.Insecure);
    }

    [Fact]
    public async Task PerNodeDirectOverridesAGlobalProxy()
    {
        var route = await Make(globalDirect: "Proxy").ResolveAsync(Healthy(mode: "Direct"));
        Assert.Equal(MediaStreamMode.Direct, route.Mode);
    }

    [Fact]
    public async Task ProxyWhenTheCertHasExpired()
    {
        var inputs = new MediaRouteInputs("Direct", null, "node1.example.com", 8555, DateTime.UtcNow.AddMinutes(-1), false, null);
        Assert.Equal(MediaStreamMode.Proxy, (await Make().ResolveAsync(inputs)).Mode);
    }

    [Fact]
    public async Task ProxyWhenTheNodeReportedAnEndpointError()
    {
        Assert.Equal(MediaStreamMode.Proxy, (await Make().ResolveAsync(Healthy(lastError: "bind failed"))).Mode);
    }

    [Fact]
    public async Task ProxyWhenNoHostOrPortReported()
    {
        var noHost = new MediaRouteInputs("Direct", null, null, 8555, DateTime.UtcNow.AddYears(1), false, null);
        var noPort = new MediaRouteInputs("Direct", null, "node1.example.com", null, DateTime.UtcNow.AddYears(1), false, null);
        Assert.Equal(MediaStreamMode.Proxy, (await Make().ResolveAsync(noHost)).Mode);
        Assert.Equal(MediaStreamMode.Proxy, (await Make().ResolveAsync(noPort)).Mode);
    }

    [Fact]
    public async Task SelfSignedIsProxyUnlessInsecureModeIsOn()
    {
        Assert.Equal(MediaStreamMode.Proxy, (await Make(globalDirect: "Direct").ResolveAsync(Healthy(selfSigned: true))).Mode);

        var withInsecure = await Make(globalDirect: "Direct", globalInsecure: true).ResolveAsync(Healthy(selfSigned: true));
        Assert.Equal(MediaStreamMode.Direct, withInsecure.Mode);
        Assert.True(withInsecure.Insecure);
    }

    [Fact]
    public async Task PerNodeInsecureOverrideAllowsASelfSignedEndpoint()
    {
        var route = await Make(globalDirect: "Direct").ResolveAsync(Healthy(insecureOverride: true, selfSigned: true));
        Assert.Equal(MediaStreamMode.Direct, route.Mode);
        Assert.True(route.Insecure);
    }

    [Fact]
    public async Task NullInputsResolvesToProxy()
    {
        Assert.Equal(MediaStreamMode.Proxy, (await Make(globalDirect: "Direct").ResolveAsync((MediaRouteInputs?)null)).Mode);
    }
}

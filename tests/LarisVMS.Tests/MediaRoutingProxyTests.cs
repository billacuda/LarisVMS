using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Services;
using NodeEntity = LarisVMS.Core.Entities.Node;
using MediaProxyEntity = LarisVMS.Core.Entities.MediaProxy;

namespace LarisVMS.Tests;

/// <summary>Failover plan phase 2: MediaRoutingService.ResolveAsync(Node) with proxy assignments —
/// a healthy assigned proxy wins over direct/central, an unhealthy one is skipped, and it falls
/// through to the phase-1 direct/proxy decision when no proxy is usable.</summary>
public class MediaRoutingProxyTests
{
    private static (MediaRoutingService Routing, ApplicationDbContext Db) Make()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var settings = new SettingsResolver(db);
        // SettingsResolver's cache is a process-wide static — another test in this class
        // (ASelfSignedProxyIsUsedWhenInsecureModeIsOn) sets LiveView.AllowInsecureClientEndpoint
        // globally, so start each case from a clean cache rather than whatever ran before it.
        settings.InvalidateAsync().GetAwaiter().GetResult();
        return (new MediaRoutingService(settings, db), db);
    }

    private static MediaProxyEntity SeedProxy(ApplicationDbContext db, bool healthy = true, bool enabled = true,
        bool selfSigned = false, int? reportedPort = 4443, string host = "proxy1.example.com")
    {
        var p = new MediaProxyEntity
        {
            Id = Guid.NewGuid(), Name = "p", Host = host, Port = 4443, ApiKeyHash = "h",
            Enabled = enabled, Healthy = healthy, ReportedPort = reportedPort,
            CertNotAfter = DateTime.UtcNow.AddYears(1), CertIsSelfSigned = selfSigned,
        };
        db.MediaProxies.Add(p);
        db.SaveChanges();
        return p;
    }

    private static NodeEntity SeedNode(ApplicationDbContext db, Guid? primary = null, Guid? backup = null)
    {
        var n = new NodeEntity
        {
            Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k",
            PrimaryProxyId = primary, BackupProxyId = backup,
        };
        db.Nodes.Add(n);
        db.SaveChanges();
        return n;
    }

    [Fact]
    public async Task AHealthyPrimaryProxyIsUsed()
    {
        var (routing, db) = Make();
        var proxy = SeedProxy(db);
        var node = SeedNode(db, primary: proxy.Id);

        var route = await routing.ResolveAsync(node);

        Assert.Equal(MediaStreamMode.Direct, route.Mode);
        Assert.Equal("proxy1.example.com", route.DirectHost);
        Assert.Equal(4443, route.DirectPort);
    }

    [Fact]
    public async Task AnUnhealthyPrimaryFallsToTheHealthyBackup()
    {
        var (routing, db) = Make();
        var primary = SeedProxy(db, healthy: false, host: "p-primary");
        var backup = SeedProxy(db, healthy: true, host: "p-backup");
        var node = SeedNode(db, primary: primary.Id, backup: backup.Id);

        var route = await routing.ResolveAsync(node);

        Assert.Equal(MediaStreamMode.Direct, route.Mode);
        Assert.Equal("p-backup", route.DirectHost);
    }

    [Fact]
    public async Task NoHealthyProxyFallsThroughToProxyThroughCentral()
    {
        var (routing, db) = Make();
        var primary = SeedProxy(db, healthy: false);
        var node = SeedNode(db, primary: primary.Id);

        var route = await routing.ResolveAsync(node);

        // No node client endpoint and the global toggle defaults to Proxy → central relay.
        Assert.Equal(MediaStreamMode.Proxy, route.Mode);
    }

    [Fact]
    public async Task ADisabledProxyIsSkipped()
    {
        var (routing, db) = Make();
        var primary = SeedProxy(db, enabled: false);
        var node = SeedNode(db, primary: primary.Id);

        Assert.Equal(MediaStreamMode.Proxy, (await routing.ResolveAsync(node)).Mode);
    }

    [Fact]
    public async Task ASelfSignedProxyIsSkippedWhenInsecureModeIsOff()
    {
        var (routing, db) = Make();
        var proxy = SeedProxy(db, selfSigned: true);
        var node = SeedNode(db, primary: proxy.Id);

        Assert.Equal(MediaStreamMode.Proxy, (await routing.ResolveAsync(node)).Mode);
    }

    [Fact]
    public async Task ASelfSignedProxyIsUsedWhenInsecureModeIsOn()
    {
        var (routing, db) = Make();
        await new SettingsResolver(db).SetGlobalAsync("LiveView.AllowInsecureClientEndpoint", "True", "test");
        var proxy = SeedProxy(db, selfSigned: true);
        var node = SeedNode(db, primary: proxy.Id);

        var route = await routing.ResolveAsync(node);
        Assert.Equal(MediaStreamMode.Direct, route.Mode);
        Assert.True(route.Insecure);
    }

    [Fact]
    public async Task NoProxyAssignedUsesThePhaseOneDecision()
    {
        var (routing, db) = Make();
        var node = SeedNode(db);

        Assert.Equal(MediaStreamMode.Proxy, (await routing.ResolveAsync(node)).Mode);
    }
}

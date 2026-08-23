using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Middleware;

namespace LarisVMS.Tests;

public class IpAllowListMiddlewareTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<HttpContext> Run(ApplicationDbContext db, string path, string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp);

        var settings = new SettingsResolver(db);
        // SettingsResolver's cache is process-static (see its own doc comment — invalidated on every
        // write in real use). These tests seed rows directly on a fresh in-memory db per test, bypassing
        // that write path entirely, so a stale entry from an earlier test's differently-seeded db can
        // otherwise leak across tests under the same cache key. Invalidate first to start from a clean
        // slate, same as a real request would see after any real admin save.
        await settings.InvalidateAsync();
        var middleware = new IpAllowListMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, settings);
        return context;
    }

    [Fact]
    public async Task WithNoListsConfiguredEveryRequestPassesThrough()
    {
        using var db = NewDb();

        var context = await Run(db, "/Admin/Nodes", "203.0.113.9");

        Assert.NotEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task AManagementRequestOutsideTheAllowListIs404()
    {
        using var db = NewDb();
        db.Settings.Add(new Core.Entities.Setting { Id = Guid.NewGuid(), Key = IpAllowListMiddleware.ManagementSettingKey, Value = "10.0.0.0/24" });
        await db.SaveChangesAsync();

        var context = await Run(db, "/Admin/Nodes", "203.0.113.9");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task AManagementRequestInsideTheAllowListPassesThrough()
    {
        using var db = NewDb();
        db.Settings.Add(new Core.Entities.Setting { Id = Guid.NewGuid(), Key = IpAllowListMiddleware.ManagementSettingKey, Value = "10.0.0.0/24" });
        await db.SaveChangesAsync();

        var context = await Run(db, "/Admin/Nodes", "10.0.0.42");

        Assert.NotEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task AMediaPathIsCheckedAgainstTheLiveViewListNotTheManagementList()
    {
        using var db = NewDb();
        // Management list excludes this IP; LiveView list is left open — a /live request from this IP
        // must still pass, proving the two lists are genuinely independent, not one shared list.
        db.Settings.Add(new Core.Entities.Setting { Id = Guid.NewGuid(), Key = IpAllowListMiddleware.ManagementSettingKey, Value = "10.0.0.0/24" });
        await db.SaveChangesAsync();

        var context = await Run(db, "/live/some-camera-id", "203.0.113.9");

        Assert.NotEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }
}

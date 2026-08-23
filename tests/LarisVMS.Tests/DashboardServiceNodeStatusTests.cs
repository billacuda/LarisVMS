using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
// LarisVMS.Core.Entities.Node collides with the LarisVMS.Node project namespace (also referenced by
// this test project) — aliased rather than fully-qualified at every use site.
using NodeEntity = LarisVMS.Core.Entities.Node;

namespace LarisVMS.Tests;

/// <summary>
/// GetAllNodeStatusAsync backs the M20 monitoring endpoint (GET /api/v1/status) — unlike
/// GetHealthAsync's own node tally, it must include a node with zero cameras assigned, since a
/// Checkmk-style check cares whether the node itself is reachable regardless of what's on it.
/// </summary>
public class DashboardServiceNodeStatusTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    // GetAllNodeStatusAsync never touches cameraService/cameraAccess — only nodeService — so passing
    // null! for the other two constructor arguments is safe, same trick PermissionServiceGrantedTests
    // uses for HasPermissionAsync's unused UserManager parameter.
    private static DashboardService NewService(ApplicationDbContext db) => new(null!, null!, new NodeService(db, null!));

    [Fact]
    public async Task ANodeWithNoCamerasAssignedStillAppears()
    {
        using var db = NewDb();
        db.Nodes.Add(new NodeEntity { Id = Guid.NewGuid(), Name = "Empty Node", ApiKeyHash = "x" });
        await db.SaveChangesAsync();

        var result = await NewService(db).GetAllNodeStatusAsync();

        Assert.Single(result);
        Assert.Equal("Empty Node", result[0].NodeName);
    }

    [Fact]
    public async Task ANodeSeenRecentlyIsOnlineAndOneNotSeenIsNot()
    {
        using var db = NewDb();
        db.Nodes.Add(new NodeEntity { Id = Guid.NewGuid(), Name = "Recent", ApiKeyHash = "x", LastSeenAt = DateTime.UtcNow });
        db.Nodes.Add(new NodeEntity { Id = Guid.NewGuid(), Name = "Stale", ApiKeyHash = "x", LastSeenAt = DateTime.UtcNow.AddHours(-1) });
        db.Nodes.Add(new NodeEntity { Id = Guid.NewGuid(), Name = "NeverSeen", ApiKeyHash = "x" });
        await db.SaveChangesAsync();

        var result = await NewService(db).GetAllNodeStatusAsync();

        Assert.True(result.Single(n => n.NodeName == "Recent").Online);
        Assert.False(result.Single(n => n.NodeName == "Stale").Online);
        Assert.False(result.Single(n => n.NodeName == "NeverSeen").Online);
    }

    [Fact]
    public async Task StorageAndVersionFieldsPassThroughUnchanged()
    {
        using var db = NewDb();
        db.Nodes.Add(new NodeEntity
        {
            Id = Guid.NewGuid(), Name = "N1", ApiKeyHash = "x",
            StorageFreeBytes = 100, StorageTotalBytes = 1000, Version = "1.2.3", Platform = "win-x64"
        });
        await db.SaveChangesAsync();

        var row = (await NewService(db).GetAllNodeStatusAsync()).Single();

        Assert.Equal(100, row.StorageFreeBytes);
        Assert.Equal(1000, row.StorageTotalBytes);
        Assert.Equal("1.2.3", row.Version);
        Assert.Equal("win-x64", row.Platform);
    }
}

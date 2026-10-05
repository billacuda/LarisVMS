using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// NodeService.ListExpiredSegmentsAsync — the server-directed half of retention, for footage the
/// node's own sweep can't reach (an old storage root, a camera that moved to another node).
/// </summary>
public class NodeServiceExpiredSegmentsTests
{
    private sealed record Seeded(NodeService Service, SettingsResolver Settings, ApplicationDbContext Db, Guid NodeId, Guid OtherNodeId, Guid CameraId);

    private static async Task<Seeded> SeedAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "old-home", ApiKeyHash = "h", MediaSigningKey = "k" };
        var other = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "new-home", ApiKeyHash = "h", MediaSigningKey = "k" };
        // The camera has moved to the other node; its old footage stays on the first one.
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "driveway", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = other.Id, IsEnabled = true,
        };
        db.AddRange(node, other, camera);
        await db.SaveChangesAsync();

        var settings = new SettingsResolver(db);
        await settings.SetOverrideAsync(SettingScope.Node, node.Id, "Retention.Days", "14");
        return new Seeded(new NodeService(db, settings), settings, db, node.Id, other.Id, camera.Id);
    }

    private static Segment Seg(Seeded s, DateTime endUtc, string path, StorageTier tier = StorageTier.Primary, bool locked = false, Guid? nodeId = null) => new()
    {
        CameraId = s.CameraId, NodeId = nodeId ?? s.NodeId, StartUtc = endUtc.AddSeconds(-30), EndUtc = endUtc,
        DurationMs = 30000, FilePath = path, StorageTier = tier, IsLocked = locked,
    };

    [Fact]
    public async Task ListsFootagePastRetentionPlusGraceOnly()
    {
        var s = await SeedAsync();
        var now = DateTime.UtcNow;
        s.Db.Segments.AddRange(
            Seg(s, now.AddDays(-31), @"\\nas\old-root\cam-x\main\a.mp4"),
            Seg(s, now.AddDays(-14.5), @"\\nas\old-root\cam-x\main\within-grace.mp4"),
            Seg(s, now.AddDays(-2), @"\\nas\old-root\cam-x\main\fresh.mp4"));
        await s.Db.SaveChangesAsync();

        var expired = await s.Service.ListExpiredSegmentsAsync(s.NodeId, 100);

        Assert.Equal([@"\\nas\old-root\cam-x\main\a.mp4"], expired.Select(e => e.FilePath));
        Assert.All(expired, e => Assert.Equal(s.CameraId, e.CameraId));
    }

    [Fact]
    public async Task NeverListsLockedSegmentsOrOtherNodesFootage()
    {
        var s = await SeedAsync();
        var old = DateTime.UtcNow.AddDays(-40);
        s.Db.Segments.AddRange(
            Seg(s, old, @"D:\v\cam-x\main\locked.mp4", locked: true),
            Seg(s, old, @"D:\v\cam-x\main\other-node.mp4", nodeId: s.OtherNodeId));
        await s.Db.SaveChangesAsync();

        Assert.Empty(await s.Service.ListExpiredSegmentsAsync(s.NodeId, 100));
    }

    [Fact]
    public async Task KeepForeverRetentionListsNothing()
    {
        var s = await SeedAsync();
        await s.Settings.SetOverrideAsync(SettingScope.Node, s.NodeId, "Retention.Days", "0");
        s.Db.Segments.Add(Seg(s, DateTime.UtcNow.AddDays(-400), @"D:\v\cam-x\main\ancient.mp4"));
        await s.Db.SaveChangesAsync();

        Assert.Empty(await s.Service.ListExpiredSegmentsAsync(s.NodeId, 100));
    }

    [Fact]
    public async Task AnArchivingCamerasPrimaryFootageIsLeftForTheNodeToArchive()
    {
        var s = await SeedAsync();
        await s.Settings.SetOverrideAsync(SettingScope.Node, s.NodeId, "Archive.Enabled", "true");
        await s.Settings.SetOverrideAsync(SettingScope.Node, s.NodeId, "Archive.RetentionDays", "60");
        var now = DateTime.UtcNow;
        s.Db.Segments.AddRange(
            Seg(s, now.AddDays(-30), @"D:\v\cam-x\main\primary.mp4"),
            Seg(s, now.AddDays(-30), @"\\nas\archive\cam-x\main\recent-archive.mp4", StorageTier.Archive),
            Seg(s, now.AddDays(-90), @"\\nas\archive\cam-x\main\old-archive.mp4", StorageTier.Archive));
        await s.Db.SaveChangesAsync();

        var expired = await s.Service.ListExpiredSegmentsAsync(s.NodeId, 100);

        Assert.Equal([@"\\nas\archive\cam-x\main\old-archive.mp4"], expired.Select(e => e.FilePath));
    }

    [Fact]
    public async Task UsesThisNodesRetentionOverride()
    {
        var s = await SeedAsync();
        await s.Settings.SetOverrideAsync(SettingScope.Node, s.NodeId, "Retention.Days", "90");
        s.Db.Segments.Add(Seg(s, DateTime.UtcNow.AddDays(-31), @"D:\v\cam-x\main\a.mp4"));
        await s.Db.SaveChangesAsync();

        Assert.Empty(await s.Service.ListExpiredSegmentsAsync(s.NodeId, 100));
    }

    [Fact]
    public async Task RespectsTheLimit()
    {
        var s = await SeedAsync();
        var old = DateTime.UtcNow.AddDays(-40);
        for (var i = 0; i < 5; i++) s.Db.Segments.Add(Seg(s, old.AddMinutes(i), $@"D:\v\cam-x\main\{i}.mp4"));
        await s.Db.SaveChangesAsync();

        Assert.Equal(3, (await s.Service.ListExpiredSegmentsAsync(s.NodeId, 3)).Count);
    }
}

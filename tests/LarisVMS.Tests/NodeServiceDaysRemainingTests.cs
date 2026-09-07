using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The "~N day(s) left" retention-runway estimate shown on Admin → Nodes for each node's primary
/// and archive volumes: free bytes divided by the bytes that flowed in over the trailing 24h
/// (freshly recorded for primary, aged into archive for archive). Null — rendered as "runway not
/// yet estimated" — until there is enough activity to derive a rate.
/// </summary>
public class NodeServiceDaysRemainingTests
{
    [Fact]
    public void EstimateIsNullWhenFreeBytesAreUnknown()
        => Assert.Null(NodeService.EstimateDaysRemaining(null, 1_000_000));

    [Fact]
    public void EstimateIsNullWhenTheWriteRateIsZeroOrNegative()
    {
        Assert.Null(NodeService.EstimateDaysRemaining(500_000_000, 0));
        Assert.Null(NodeService.EstimateDaysRemaining(500_000_000, -10));
    }

    [Fact]
    public void EstimateIsFreeBytesDividedByBytesPerDay()
    {
        var days = NodeService.EstimateDaysRemaining(1_000_000_000, 100_000_000);
        Assert.NotNull(days);
        Assert.Equal(10.0, days!.Value, precision: 6);
    }

    private static (NodeService Service, ApplicationDbContext Db) NewService()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new NodeService(db, null!), db);
    }

    private static Segment Seg(Guid nodeId, Guid cameraId, StorageTier tier, DateTime startUtc, long size,
        DateTime? archivedAt = null) => new()
    {
        CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
        StartUtc = startUtc, EndUtc = startUtc.AddMinutes(1), DurationMs = 60_000,
        FilePath = $"cam-{cameraId}/main/{startUtc.Ticks}.mp4", SizeBytes = size,
        StorageTier = tier, ArchivedAt = archivedAt,
    };

    [Fact]
    public async Task PrimaryEstimateUsesFreshlyRecordedBytesInTheLast24h()
    {
        var (service, db) = NewService();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n1", ApiKeyHash = "h", MediaSigningKey = "k",
            StorageFreeBytes = 900_000_000 };
        var cam = new Camera { Id = Guid.NewGuid(), Name = "c1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id, IsEnabled = true };
        db.AddRange(node, cam);
        db.Segments.AddRange(
            Seg(node.Id, cam.Id, StorageTier.Primary, DateTime.UtcNow.AddHours(-2), 100_000_000),
            Seg(node.Id, cam.Id, StorageTier.Primary, DateTime.UtcNow.AddHours(-20), 200_000_000),
            // Outside the window — ignored.
            Seg(node.Id, cam.Id, StorageTier.Primary, DateTime.UtcNow.AddDays(-3), 999_000_000),
            // Archive tier — not part of the primary rate.
            Seg(node.Id, cam.Id, StorageTier.Archive, DateTime.UtcNow.AddHours(-1), 500_000_000,
                archivedAt: DateTime.UtcNow.AddHours(-1)));
        await db.SaveChangesAsync();

        var result = await service.GetEstimatedDaysRemainingAsync();

        // 900_000_000 free / 300_000_000 per day = 3 days
        Assert.Equal(3.0, result[node.Id]!.Value, precision: 6);
    }

    [Fact]
    public async Task ArchiveEstimateUsesBytesAgedIntoArchiveInTheLast24h()
    {
        var (service, db) = NewService();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n1", ApiKeyHash = "h", MediaSigningKey = "k",
            ArchiveFreeBytes = 800_000_000 };
        var cam = new Camera { Id = Guid.NewGuid(), Name = "c1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id, IsEnabled = true };
        db.AddRange(node, cam);
        db.Segments.AddRange(
            Seg(node.Id, cam.Id, StorageTier.Archive, DateTime.UtcNow.AddDays(-10), 200_000_000,
                archivedAt: DateTime.UtcNow.AddHours(-3)),
            Seg(node.Id, cam.Id, StorageTier.Archive, DateTime.UtcNow.AddDays(-12), 200_000_000,
                archivedAt: DateTime.UtcNow.AddHours(-10)),
            // Archived more than 24h ago — ignored.
            Seg(node.Id, cam.Id, StorageTier.Archive, DateTime.UtcNow.AddDays(-20), 900_000_000,
                archivedAt: DateTime.UtcNow.AddDays(-2)));
        await db.SaveChangesAsync();

        var result = await service.GetArchiveEstimatedDaysRemainingAsync();

        // 800_000_000 free / 400_000_000 per day = 2 days
        Assert.Equal(2.0, result[node.Id]!.Value, precision: 6);
    }

    [Fact]
    public async Task ArchiveEstimateIsNullWithoutRecentArchivingActivity()
    {
        var (service, db) = NewService();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n1", ApiKeyHash = "h", MediaSigningKey = "k",
            ArchiveFreeBytes = 800_000_000 };
        db.Add(node);
        await db.SaveChangesAsync();

        var result = await service.GetArchiveEstimatedDaysRemainingAsync();

        Assert.True(result.ContainsKey(node.Id));
        Assert.Null(result[node.Id]);
    }
}

using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Covers ExportService's delete/retry additions — creation and dispatch-candidate paths
/// are exercised indirectly through ExportJobDispatcher today, so this focuses on the newer
/// Exports-page actions (trash button, retry button) that don't have another home yet.</summary>
public class ExportServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static (ApplicationDbContext Db, Guid CameraId, Guid NodeId, Guid JobId) SeedJob(ExportJobStatus status = ExportJobStatus.Queued)
    {
        var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash",
            LastIpAddress = "10.0.0.5", LivePort = 8554, MediaSigningKey = "key"
        };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id
        };
        var job = new ExportJob
        {
            Id = Guid.NewGuid(), RequestedByUserId = "u1", CreatedUtc = DateTime.UtcNow,
            FromUtc = DateTime.UtcNow.AddHours(-1), ToUtc = DateTime.UtcNow, Status = status
        };
        db.AddRange(node, camera, job);
        db.SaveChanges();
        return (db, camera.Id, node.Id, job.Id);
    }

    private static ExportJobItem AddItem(ApplicationDbContext db, Guid jobId, Guid cameraId, Guid? nodeId, ExportItemStatus status,
        string? outputFilePath = null, string? errorMessage = null)
    {
        var item = new ExportJobItem
        {
            Id = Guid.NewGuid(), ExportJobId = jobId, CameraId = cameraId, NodeId = nodeId, Status = status,
            OutputFilePath = outputFilePath, ErrorMessage = errorMessage,
            StartedUtc = status != ExportItemStatus.Queued ? DateTime.UtcNow.AddMinutes(-1) : null,
            CompletedUtc = status is ExportItemStatus.Done or ExportItemStatus.Failed ? DateTime.UtcNow : null
        };
        db.ExportJobItems.Add(item);
        db.SaveChanges();
        return item;
    }

    [Fact]
    public async Task RetryResetsAFailedItemBackToQueuedAndClearsItsDispatchState()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob(ExportJobStatus.Failed);
        var item = AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Failed, errorMessage: "Node rejected the export request (HTTP 400).");

        var service = new ExportService(db);
        var ok = await service.RetryItemAsync(item.Id);

        Assert.True(ok);
        var reloaded = await db.ExportJobItems.AsNoTracking().FirstAsync(i => i.Id == item.Id);
        Assert.Equal(ExportItemStatus.Queued, reloaded.Status);
        Assert.Null(reloaded.NodeId);
        Assert.Null(reloaded.OutputFilePath);
        Assert.Null(reloaded.ErrorMessage);
        Assert.Null(reloaded.StartedUtc);
        Assert.Null(reloaded.CompletedUtc);
    }

    [Fact]
    public async Task RetryRollsTheParentJobBackToQueued()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob(ExportJobStatus.Failed);
        var item = AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Failed, errorMessage: "boom");

        var service = new ExportService(db);
        await service.RetryItemAsync(item.Id);

        var job = await db.ExportJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(ExportJobStatus.Queued, job.Status);
    }

    [Fact]
    public async Task RetryRefusesAnItemThatIsNotFailed()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob(ExportJobStatus.Done);
        var item = AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Done, outputFilePath: @"C:\exports\a.mp4");

        var service = new ExportService(db);
        var ok = await service.RetryItemAsync(item.Id);

        Assert.False(ok);
        var reloaded = await db.ExportJobItems.AsNoTracking().FirstAsync(i => i.Id == item.Id);
        Assert.Equal(ExportItemStatus.Done, reloaded.Status);
    }

    [Fact]
    public async Task RetryReturnsFalseForAnUnknownItemId()
    {
        var (db, _, _, _) = SeedJob();
        var service = new ExportService(db);

        Assert.False(await service.RetryItemAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetDeletionInfoReturnsNullForAnUnknownJob()
    {
        var (db, _, _, _) = SeedJob();
        var service = new ExportService(db);

        Assert.Null(await service.GetDeletionInfoAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetDeletionInfoRefusesAJobWithAQueuedOrRunningItem()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob(ExportJobStatus.Running);
        AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Running);

        var service = new ExportService(db);
        var info = await service.GetDeletionInfoAsync(jobId);

        Assert.NotNull(info);
        Assert.False(info!.CanDelete);
        Assert.NotNull(info.Reason);
        Assert.Empty(info.Files);
    }

    [Fact]
    public async Task GetDeletionInfoListsEachDoneItemsFileWithItsOwningNodesConnectionInfo()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob(ExportJobStatus.Done);
        var done = AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Done, outputFilePath: @"C:\exports\a.mp4");
        AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Failed, errorMessage: "boom");

        var service = new ExportService(db);
        var info = await service.GetDeletionInfoAsync(jobId);

        Assert.NotNull(info);
        Assert.True(info!.CanDelete);
        var file = Assert.Single(info.Files);
        Assert.Equal(done.Id, file.ExportItemId);
        Assert.Equal(@"C:\exports\a.mp4", file.FilePath);
        Assert.Equal("10.0.0.5", file.NodeIp);
        Assert.Equal(8554, file.NodeLivePort);
        Assert.Equal("key", file.NodeMediaSigningKey);
    }

    [Fact]
    public async Task DeleteJobRemovesTheJobAndCascadesItsItems()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob(ExportJobStatus.Done);
        AddItem(db, jobId, cameraId, nodeId, ExportItemStatus.Done, outputFilePath: @"C:\exports\a.mp4");

        var service = new ExportService(db);
        await service.DeleteJobAsync(jobId);

        Assert.Null(await db.ExportJobs.FindAsync(jobId));
        Assert.Empty(await db.ExportJobItems.Where(i => i.ExportJobId == jobId).ToListAsync());
    }

    [Fact]
    public async Task DeleteJobIsANoOpForAnUnknownJobId()
    {
        var (db, _, _, _) = SeedJob();
        var service = new ExportService(db);

        await service.DeleteJobAsync(Guid.NewGuid());
        // No exception is the assertion here — nothing else to observe.
    }

    [Fact]
    public async Task SplitReplacesTheOriginalItemWithOnePinnedQueuedItemPerNode()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob();
        var otherNodeId = Guid.NewGuid();
        var original = AddItem(db, jobId, cameraId, null, ExportItemStatus.Queued);

        var service = new ExportService(db);
        await service.SplitItemAcrossNodesAsync(original.Id, [nodeId, otherNodeId]);

        var remaining = await db.ExportJobItems.AsNoTracking().Where(i => i.ExportJobId == jobId).ToListAsync();
        Assert.DoesNotContain(remaining, i => i.Id == original.Id);
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, i => Assert.Equal(ExportItemStatus.Queued, i.Status));
        Assert.All(remaining, i => Assert.Equal(cameraId, i.CameraId));
        Assert.Contains(remaining, i => i.NodeId == nodeId);
        Assert.Contains(remaining, i => i.NodeId == otherNodeId);
    }

    [Fact]
    public async Task SplitIsANoOpForAnUnknownItemId()
    {
        var (db, _, nodeId, _) = SeedJob();
        var service = new ExportService(db);

        await service.SplitItemAcrossNodesAsync(Guid.NewGuid(), [nodeId]);
        // No exception is the assertion here — nothing else to observe.
    }

    [Fact]
    public async Task GetQueuedItemsResolvesANormalItemsNodeFromTheCamerasCurrentNodeAndReportsItUnpinned()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob();
        AddItem(db, jobId, cameraId, null, ExportItemStatus.Queued);

        var service = new ExportService(db);
        var candidate = Assert.Single(await service.GetQueuedItemsAsync());

        Assert.Equal(nodeId, candidate.NodeId);
        Assert.False(candidate.IsPinnedToNode);
    }

    [Fact]
    public async Task GetQueuedItemsResolvesASplitItemsNodeFromItsOwnPinnedNodeIdNotTheCamerasCurrentNode()
    {
        var (db, cameraId, nodeId, jobId) = SeedJob();
        var pinnedNodeId = Guid.NewGuid(); // deliberately not the camera's current node
        AddItem(db, jobId, cameraId, pinnedNodeId, ExportItemStatus.Queued);

        var service = new ExportService(db);
        var candidate = Assert.Single(await service.GetQueuedItemsAsync());

        Assert.Equal(pinnedNodeId, candidate.NodeId);
        Assert.NotEqual(nodeId, candidate.NodeId);
        Assert.True(candidate.IsPinnedToNode);
    }
}

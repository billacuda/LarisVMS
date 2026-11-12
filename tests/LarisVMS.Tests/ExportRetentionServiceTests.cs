using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// An export disappears once the node has deleted its files (7 days after finishing), so the
/// Exports page never offers a Download that can only fail. Unfinished exports always stay.
/// </summary>
public class ExportRetentionServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static ExportJob MakeJob(DateTime createdUtc, ExportItemStatus itemStatus)
    {
        var job = new ExportJob
        {
            Id = Guid.NewGuid(),
            RequestedByUserId = "user-1",
            CreatedUtc = createdUtc,
            FromUtc = createdUtc.AddHours(-1),
            ToUtc = createdUtc,
        };
        job.Items.Add(new ExportJobItem { Id = Guid.NewGuid(), ExportJobId = job.Id, CameraId = Guid.NewGuid(), Status = itemStatus });
        return job;
    }

    [Fact]
    public async Task RemovesFinishedExportsOlderThanTheNodesRetention()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        await using var db = NewDb();
        var old = MakeJob(now.AddDays(-30), ExportItemStatus.Done);
        var oldFailed = MakeJob(now.AddDays(-8), ExportItemStatus.Failed);
        var recent = MakeJob(now.AddDays(-2), ExportItemStatus.Done);
        db.ExportJobs.AddRange(old, oldFailed, recent);
        await db.SaveChangesAsync();

        var removed = await ExportRetentionService.SweepAsync(db, now, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.Equal([recent.Id], await db.ExportJobs.Select(j => j.Id).ToListAsync());
    }

    [Theory]
    [InlineData(ExportItemStatus.Queued)]
    [InlineData(ExportItemStatus.Running)]
    public async Task KeepsAnOldExportThatIsStillInProgress(ExportItemStatus status)
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        await using var db = NewDb();
        db.ExportJobs.Add(MakeJob(now.AddDays(-30), status));
        await db.SaveChangesAsync();

        Assert.Equal(0, await ExportRetentionService.SweepAsync(db, now, CancellationToken.None));
        Assert.Equal(1, await db.ExportJobs.CountAsync());
    }
}

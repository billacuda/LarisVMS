using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IExportService"/>
public class ExportService(ApplicationDbContext db) : IExportService
{
    public async Task<ExportJob> CreateJobAsync(IReadOnlyList<Guid> cameraIds, DateTime fromUtc, DateTime toUtc,
        string requestedByUserId, string? requestedByUserName, CancellationToken ct = default)
    {
        fromUtc = TimelineService.NormalizeToUtc(fromUtc);
        toUtc = TimelineService.NormalizeToUtc(toUtc);

        var job = new ExportJob
        {
            Id = Guid.NewGuid(),
            RequestedByUserId = requestedByUserId,
            RequestedByUserName = requestedByUserName,
            CreatedUtc = DateTime.UtcNow,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Status = ExportJobStatus.Queued
        };

        foreach (var cameraId in cameraIds.Distinct())
        {
            job.Items.Add(new ExportJobItem
            {
                Id = Guid.NewGuid(),
                ExportJobId = job.Id,
                CameraId = cameraId,
                Status = ExportItemStatus.Queued
            });
        }

        db.ExportJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<List<ExportJob>> ListJobsAsync(CancellationToken ct = default)
        => await db.ExportJobs.AsNoTracking()
            .Include(j => j.Items)
            .OrderByDescending(j => j.CreatedUtc)
            .ToListAsync(ct);

    public async Task<List<ExportDispatchCandidate>> GetQueuedItemsAsync(CancellationToken ct = default)
        => await (
            from item in db.ExportJobItems.AsNoTracking()
            where item.Status == ExportItemStatus.Queued
            join job in db.ExportJobs.AsNoTracking() on item.ExportJobId equals job.Id
            join camera in db.Cameras.AsNoTracking() on item.CameraId equals camera.Id into cameraJoin
            from camera in cameraJoin.DefaultIfEmpty()
            select new ExportDispatchCandidate(
                item.Id, item.CameraId, camera != null ? camera.Name : "(deleted camera)",
                camera != null ? camera.NodeId : null, job.FromUtc, job.ToUtc)
        ).ToListAsync(ct);

    public async Task MarkItemRunningAsync(Guid itemId, Guid nodeId, DateTime startedUtc, CancellationToken ct = default)
    {
        var item = await db.ExportJobItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;

        item.NodeId = nodeId;
        item.Status = ExportItemStatus.Running;
        item.StartedUtc = startedUtc;
        await db.SaveChangesAsync(ct);
        await RecomputeJobStatusAsync(item.ExportJobId, ct);
    }

    public async Task MarkItemFailedAsync(Guid itemId, string errorMessage, CancellationToken ct = default)
    {
        var item = await db.ExportJobItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;

        item.Status = ExportItemStatus.Failed;
        item.ErrorMessage = Truncate(errorMessage, 2000);
        item.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await RecomputeJobStatusAsync(item.ExportJobId, ct);
    }

    public async Task ApplyCompletionReportAsync(Guid nodeId, IReadOnlyList<ExportCompleteReportItem> items, CancellationToken ct = default)
    {
        var affectedJobIds = new HashSet<Guid>();
        foreach (var report in items)
        {
            // Scoped to nodeId: an item reported by a node other than the one it was actually
            // dispatched to (stale/rogue report) is silently ignored rather than trusted.
            var item = await db.ExportJobItems.FirstOrDefaultAsync(i => i.Id == report.ExportItemId && i.NodeId == nodeId, ct);
            if (item is null) continue;

            item.Status = report.Success ? ExportItemStatus.Done : ExportItemStatus.Failed;
            item.OutputFilePath = report.Success ? report.OutputFilePath : null;
            item.OutputSizeBytes = report.SizeBytes;
            item.ErrorMessage = report.Success ? null : Truncate(report.ErrorMessage ?? "Export failed.", 2000);
            item.CompletedUtc = DateTime.UtcNow;
            affectedJobIds.Add(item.ExportJobId);
        }

        await db.SaveChangesAsync(ct);
        foreach (var jobId in affectedJobIds) await RecomputeJobStatusAsync(jobId, ct);
    }

    public async Task<ExportDownloadInfo?> GetDownloadInfoAsync(Guid exportItemId, CancellationToken ct = default)
    {
        var item = await db.ExportJobItems.AsNoTracking()
            .Where(i => i.Id == exportItemId && i.Status == ExportItemStatus.Done)
            .Select(i => new { i.OutputFilePath, i.NodeId })
            .FirstOrDefaultAsync(ct);
        if (item?.OutputFilePath is null || item.NodeId is null) return null;

        var node = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == item.NodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);

        return new ExportDownloadInfo(item.OutputFilePath, node?.LastIpAddress, node?.LivePort, node?.MediaSigningKey);
    }

    /// <summary>See ExportJobStatus's doc comment for the exact rollup rule this implements.</summary>
    private async Task RecomputeJobStatusAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.ExportJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        var statuses = await db.ExportJobItems.AsNoTracking()
            .Where(i => i.ExportJobId == jobId)
            .Select(i => i.Status)
            .ToListAsync(ct);
        if (statuses.Count == 0) return;

        var rollup = statuses.Any(s => s == ExportItemStatus.Queued) ? ExportJobStatus.Queued
            : statuses.Any(s => s == ExportItemStatus.Running) ? ExportJobStatus.Running
            : statuses.Any(s => s == ExportItemStatus.Done) ? ExportJobStatus.Done
            : ExportJobStatus.Failed;

        if (job.Status != rollup)
        {
            job.Status = rollup;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string? Truncate(string? s, int maxLen) => s is null || s.Length <= maxLen ? s : s[..maxLen];
}

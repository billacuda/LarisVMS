using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Server-side (LarisVMS.Web) orchestration for multi-camera video export: creates
/// jobs/items, feeds ExportJobDispatcher the Queued work, applies dispatch/completion outcomes, and
/// resolves what the download proxy needs. Shared/global, not scoped to whoever created a job — see
/// ExportJob.RequestedByUserId's own doc comment.</summary>
public interface IExportService
{
    /// <summary>Creates the job and one Queued item per distinct camera id, all pointing at the
    /// same job.</summary>
    Task<ExportJob> CreateJobAsync(IReadOnlyList<Guid> cameraIds, DateTime fromUtc, DateTime toUtc,
        string requestedByUserId, string? requestedByUserName, CancellationToken ct = default);

    /// <summary>Newest first, with each job's Items eager-loaded — the Exports page's whole feed in
    /// one call.</summary>
    Task<List<ExportJob>> ListJobsAsync(CancellationToken ct = default);

    /// <summary>Every Queued item across every job, with everything ExportJobDispatcher needs to
    /// build one ExportRequest and know where to send it (the owning camera's current name/NodeId,
    /// plus the parent job's own range) in a single round trip.</summary>
    Task<List<ExportDispatchCandidate>> GetQueuedItemsAsync(CancellationToken ct = default);

    /// <summary>Flips an item to Running and snapshots nodeId (see ExportJobItem.NodeId's own doc
    /// comment for why this is captured now, not re-read later), then rolls the parent job's Status
    /// up. Called before the dispatch POST is even made — see ExportJobDispatcher for why.</summary>
    Task MarkItemRunningAsync(Guid itemId, Guid nodeId, DateTime startedUtc, CancellationToken ct = default);

    /// <summary>Flips an item straight to Failed — either it had no assigned node at dispatch time,
    /// or the dispatch POST itself threw/timed out, so it never reached Running on the node.</summary>
    Task MarkItemFailedAsync(Guid itemId, string errorMessage, CancellationToken ct = default);

    /// <summary>Applies a node's completion report (success or failure) to the matching item(s) —
    /// scoped to nodeId the same way NodeService.DeleteSegmentsAsync/UpdateStreamInfoAsync are, so a
    /// report can't claim an item it doesn't actually own — and rolls up the parent job(s)'
    /// Status.</summary>
    Task ApplyCompletionReportAsync(Guid nodeId, IReadOnlyList<ExportCompleteReportItem> items, CancellationToken ct = default);

    /// <summary>Resolves what the download proxy needs: the item's OutputFilePath and owning node's
    /// connection info, but only when the item is Done — everything else (still running, failed,
    /// unknown id) comes back null and the proxy treats that as 404, same shape as
    /// ITimelineService.GetSegmentForPlaybackAsync.</summary>
    Task<ExportDownloadInfo?> GetDownloadInfoAsync(Guid exportItemId, CancellationToken ct = default);
}

namespace LarisVMS.Core.Dtos;

// Wire DTOs for multi-camera video export: the browser's POST /api/exports trigger, the Web -> Node
// dispatch request, and the Node -> Web completion report. Shared via Core the same way
// NodeDtos.cs/TimelineDtos.cs are.

/// <summary>Body for POST /api/exports — the Playback page's Export form. FromUtc/ToUtc go through
/// the same "normalize to UTC" handling TimelineService.NormalizeToUtc already applies to playback's
/// own range params.</summary>
public record CreateExportRequest(List<Guid> CameraIds, DateTime FromUtc, DateTime ToUtc);

/// <summary>Web -&gt; Node: what ExportJobDispatcher POSTs to a node's /export/{cameraId} once it has
/// resolved that camera's segment file paths for the job's range. SegmentFilePaths is already
/// ordered by StartUtc — the node writes them into its concat list file in exactly this order, no
/// re-sorting on that side.</summary>
public record ExportRequest(Guid ExportItemId, Guid CameraId, List<string> SegmentFilePaths, string OutputFileName);

/// <summary>Node -&gt; Web: POST /api/nodes/exports/complete, one call per finished (or failed) export
/// item. Batched as a list the same shape SegmentReportItem's endpoint uses, even though in practice
/// a node only ever finishes one export at a time and sends a single-element list — kept a list
/// rather than a single-item body so a future batching reporter doesn't need a wire change.</summary>
public record ExportCompleteReportItem(Guid ExportItemId, bool Success, string? OutputFilePath, long? SizeBytes, string? ErrorMessage);

/// <summary>What ExportJobDispatcher needs to dispatch one Queued item, in one round trip: the
/// camera's current name plus the parent job's own [FromUtc, ToUtc). NodeId is the item's own
/// pre-pinned node if SplitItemAcrossNodesAsync created it that way (IsPinnedToNode true), else the
/// camera's current node — see ExportJobItem.NodeId's own doc comment for why an item can carry a
/// node id before ever having been dispatched.</summary>
public record ExportDispatchCandidate(Guid ExportItemId, Guid CameraId, string CameraName, Guid? NodeId, bool IsPinnedToNode, DateTime FromUtc, DateTime ToUtc);

/// <summary>What the Web-side /export-download proxy needs to reach a Done item's file on its owning
/// node — same shape as PlaybackSegmentInfo. Null node fields mean that node hasn't reported
/// live-view readiness yet, the same condition /playback-segment already checks for.</summary>
public record ExportDownloadInfo(string FilePath, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey);

/// <summary>One Done item's output file location, for the delete endpoint to best-effort ask the
/// owning node to remove before the DB row goes away — same node-connection shape
/// ExportDownloadInfo carries, plus the item id so the caller can build a per-item delete token.</summary>
public record ExportJobDeletionFile(Guid ExportItemId, string FilePath, string? NodeIp, int? NodeLivePort, string? NodeMediaSigningKey);

/// <summary>Result of GetDeletionInfoAsync's pre-check: CanDelete is false while any item is still
/// Queued or Running (a dispatch could be in flight, and there's nothing finished to clean up yet),
/// with Reason set to explain that to the caller. Files is only populated when CanDelete is true,
/// and only lists Done items with an actual output file — Queued/Failed items never had one.</summary>
public record ExportJobDeletionInfo(bool CanDelete, string? Reason, List<ExportJobDeletionFile> Files);

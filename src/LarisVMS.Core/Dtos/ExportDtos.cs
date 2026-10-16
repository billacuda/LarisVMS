namespace LarisVMS.Core.Dtos;

// Wire DTOs for multi-camera video export: the browser's POST /api/exports trigger, the Web -> Node
// dispatch request, and the Node -> Web completion report. Shared via Core the same way
// NodeDtos.cs/TimelineDtos.cs are.

/// <summary>Body for POST /api/exports — the Playback page's Export form. FromUtc/ToUtc go through
/// the same "normalize to UTC" handling TimelineService.NormalizeToUtc already applies to playback's
/// own range params.</summary>
/// <summary>Redactions: per camera, polygons to black out in that camera's export — each polygon a list
/// of [x, y] points in 0..1 frame coordinates (rectangles arrive as four points).</summary>
public record CreateExportRequest(List<Guid> CameraIds, DateTime FromUtc, DateTime ToUtc,
    Dictionary<Guid, List<List<double[]>>>? Redactions = null);

/// <summary>Web -&gt; Node: what ExportJobDispatcher POSTs to a node's /export/{cameraId} once it has
/// resolved that camera's segment file paths for the job's range. SegmentFilePaths is already
/// ordered by StartUtc — the node writes them into its concat list file in exactly this order, no
/// re-sorting on that side.</summary>
public record ExportRequest(Guid ExportItemId, Guid CameraId, List<string> SegmentFilePaths, string OutputFileName,
    /// <summary>Two-way audio presses to mix into the export's audio (see TalkRecording), each placed
    /// by its start relative to SegmentsStartUtc — when the first segment begins, which is where the
    /// exported file's own timeline starts. Null/empty keeps the plain remux.</summary>
    List<ExportTalkClip>? TalkClips = null,
    DateTime? SegmentsStartUtc = null,
    /// <summary>Whether the segments carry the camera's own audio track — talk is mixed over it, or
    /// over silence when there isn't one.</summary>
    bool SegmentsHaveAudio = false,
    /// <summary>Areas to black out (ExportJobItem.RedactionJson); null exports the footage as recorded.</summary>
    string? RedactionJson = null,
    /// <summary>Each segment's recorded content hash, parallel to SegmentFilePaths (null where none
    /// was recorded). The node re-hashes every file before exporting and reports whether it still
    /// matches — the export manifest's "unchanged since recording" check.</summary>
    List<string?>? SegmentSha256 = null);

/// <summary>One recorded push-to-talk WAV on the exporting node.</summary>
public record ExportTalkClip(string FilePath, DateTime StartUtc);

/// <summary>Node -&gt; Web: POST /api/nodes/exports/complete, one call per finished (or failed) export
/// item. Batched as a list the same shape SegmentReportItem's endpoint uses, even though in practice
/// a node only ever finishes one export at a time and sends a single-element list — kept a list
/// rather than a single-item body so a future batching reporter doesn't need a wire change.</summary>
public record ExportCompleteReportItem(Guid ExportItemId, bool Success, string? OutputFilePath, long? SizeBytes, string? ErrorMessage,
    /// <summary>SHA-256 of the exported file. Null from an older node.</summary>
    string? OutputSha256 = null,
    /// <summary>Each source segment as hashed just before exporting. Null from an older node.</summary>
    List<ExportSegmentCheck>? SegmentChecks = null);

/// <summary>One source segment of an export, re-hashed on the node at export time. Matches is null
/// when there was no recorded hash to compare against.</summary>
public record ExportSegmentCheck(string FilePath, string? Sha256, bool? Matches);

/// <summary>What ExportJobDispatcher needs to dispatch one Queued item, in one round trip: the
/// camera's current name plus the parent job's own [FromUtc, ToUtc). NodeId is the item's own
/// pre-pinned node if SplitItemAcrossNodesAsync created it that way (IsPinnedToNode true), else the
/// camera's current node — see ExportJobItem.NodeId's own doc comment for why an item can carry a
/// node id before ever having been dispatched.</summary>
public record ExportDispatchCandidate(Guid ExportItemId, Guid CameraId, string CameraName, Guid? NodeId, bool IsPinnedToNode, DateTime FromUtc, DateTime ToUtc,
    string? RedactionJson = null);

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

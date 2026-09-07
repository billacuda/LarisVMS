using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Interfaces;

/// <summary>Server-side (LarisVMS.Web) node control plane: registration, auth, config snapshot, and
/// ingest of segment/status reports from nodes.</summary>
public interface INodeService
{
    Task<List<Node>> ListAsync(CancellationToken ct = default);

    Task<NodeRegisterResponse> RegisterAsync(NodeRegisterRequest request, CancellationToken ct = default);

    /// <summary>Validates "{nodeId}:{secret}" and, if valid, stamps LastSeenAt/Status/LastIpAddress.
    /// remoteIp is captured server-side from the connection, not self-reported by the node. Returns
    /// null for an invalid or unknown node — callers (NodeAuthMiddleware) treat that as 401. Does
    /// NOT update Version/LivePort — middleware runs before the specific endpoint's body is bound,
    /// so it has nothing reported to stamp; see RecordHeartbeatAsync, called from the heartbeat
    /// handler which does have those.</summary>
    Task<Node?> AuthenticateAsync(string nodeId, string secret, string? remoteIp, CancellationToken ct = default);

    Task<NodeConfigResponse> GetConfigAsync(Guid nodeId, CancellationToken ct = default);

    Task RecordSegmentsAsync(Guid nodeId, IReadOnlyList<SegmentReportItem> segments, CancellationToken ct = default);

    /// <summary>M8: persists completed motion spans a node's substream pipeline reported. See
    /// MotionSpanReportItem's doc comment for why plain inserts, not SqlBulkCopy, are enough here.</summary>
    Task RecordMotionSpansAsync(Guid nodeId, IReadOnlyList<MotionSpanReportItem> spans, CancellationToken ct = default);

    /// <summary>M8 pass 6: persists raw ONVIF PullPoint notifications a node's event polling
    /// reported — every one, not just motion-classified ones (which separately also produced a
    /// MotionSpan the node reported through RecordMotionSpansAsync).</summary>
    Task RecordCameraEventsAsync(Guid nodeId, IReadOnlyList<CameraEventReportItem> events, CancellationToken ct = default);

    /// <summary>Removes the Segment rows for files the node's StorageManager has already deleted
    /// from disk (retention/quota/watermark eviction), scoped to this node so one node can't claim
    /// to have deleted another's files.</summary>
    Task DeleteSegmentsAsync(Guid nodeId, IReadOnlyList<string> filePaths, CancellationToken ct = default);

    /// <summary>Updates each Segment row this node reports as moved from the primary volume to the
    /// archive volume — FilePath, StorageTier, ArchivedAt, SizeBytes — scoped to this node.
    /// Idempotent: a report for a row already at the new path is a no-op.</summary>
    Task RelocateSegmentsAsync(Guid nodeId, IReadOnlyList<SegmentRelocateItem> items, CancellationToken ct = default);

    /// <summary>Every FilePath this node currently owns a Segment row for — used by the node's own
    /// periodic reconciliation sweep (StorageManager) to find rows whose file no longer exists on
    /// disk (most commonly a deletion that was made but never successfully reported, before the
    /// report-retry fix existed) and clean them up the same way a normal eviction is reported. Not
    /// paged: at the row counts one node's own footage produces this is a few MB at most, and this
    /// only runs on an hours-long cadence, not every reconcile.</summary>
    Task<List<string>> ListSegmentFilePathsAsync(Guid nodeId, CancellationToken ct = default);

    /// <summary>Every FilePath this node owns a Segment row for that is still StorageTier=Primary —
    /// used by the node's archive-tier reconciliation to spot rows whose file is actually on the
    /// archive volume (a repointed storage root) and flip them.</summary>
    Task<List<string>> ListPrimaryTieredSegmentFilePathsAsync(Guid nodeId, CancellationToken ct = default);

    /// <summary>Every MotionSpan id this node's cameras currently have a row for (pass 2c) — used by
    /// StorageManager's own snapshot-reconciliation sweep to find cached crop image files whose owning
    /// row was deleted independently (MotionSpanRetentionService), not just as a side effect of the
    /// segment it was cropped from being evicted. Joins through Camera.NodeId — MotionSpan itself
    /// carries no NodeId of its own, unlike Segment.</summary>
    Task<List<long>> ListMotionSpanIdsAsync(Guid nodeId, CancellationToken ct = default);

    /// <summary>Applies real resolution/codec parsed from the node's own ffmpeg output to the
    /// matching CameraStream row(s) — only for cameras this node currently owns, so a stale report
    /// from a node a camera has since been reassigned away from can't overwrite it.</summary>
    Task UpdateStreamInfoAsync(Guid nodeId, IReadOnlyList<StreamInfoReportItem> items, CancellationToken ct = default);

    /// <summary>Persists the node's self-reported disk usage, version, and live-media port from its
    /// latest heartbeat. A null version/livePort leaves the stored value unchanged rather than
    /// clearing it. nodeSentAtUtc (from NodeHeartbeatRequest.SentAtUtc) is compared against
    /// serverReceivedUtc to measure clock skew (see Node.ClockSkewSeconds); null (an older node
    /// build that doesn't send it yet) leaves the previous skew measurement in place rather than
    /// clearing it to unknown on every heartbeat. detectedEncoders (M17) is serialized to
    /// Node.DetectedEncodersJson the same coalesce-preserve way; null leaves the previous value
    /// alone rather than clearing a real prior probe result.</summary>
    Task RecordHeartbeatAsync(Guid nodeId, long? freeBytes, long? totalBytes, string? version, int? livePort,
        DateTime? nodeSentAtUtc, DateTime serverReceivedUtc, List<string>? detectedEncoders = null,
        List<string>? detectedAccelerators = null, long? archiveFreeBytes = null, long? archiveTotalBytes = null,
        bool storagePressureActive = false, CancellationToken ct = default);

    /// <summary>Rough "days of retention remaining" per node: free bytes divided by that node's
    /// measured write rate over the last 24h. Null for a node with no free-space report yet or no
    /// recent writes to estimate a rate from — an honest "unknown" rather than a misleading number
    /// from too little history. Not SMART/disk-health, just a bytes-in / bytes-free projection.</summary>
    Task<Dictionary<Guid, double?>> GetEstimatedDaysRemainingAsync(CancellationToken ct = default);

    /// <summary>Same estimate as GetEstimatedDaysRemainingAsync but for each node's archive volume
    /// (Node.ArchiveFreeBytes divided by bytes moved to archive over the last 24h). Null until there
    /// is archiving activity to estimate a rate from, or for a node with no archive volume.</summary>
    Task<Dictionary<Guid, double?>> GetArchiveEstimatedDaysRemainingAsync(CancellationToken ct = default);

    Task AssignCameraAsync(Guid cameraId, Guid? nodeId, CancellationToken ct = default);

    /// <summary>Batch version of AssignCameraAsync — re-points every camera in cameraIds to nodeId
    /// (null unassigns) in one statement. Moving a camera to a new node never touches its Segment
    /// rows; footage already recorded stays attached to whichever NodeId actually wrote it.</summary>
    Task ReassignCamerasAsync(IReadOnlyCollection<Guid> cameraIds, Guid? nodeId, CancellationToken ct = default);

    /// <summary>Updates the node's name, its (required) storage root and optional archive root, and
    /// (object detection plan decision 2) hardware accelerator choice. Storage config is per-node with
    /// no global fallback — the Admin/Nodes handler rejects a blank storageRootPath. aiAccelerator
    /// null resolves as Auto — see NodeConfigResponse.AiAccelerator's own doc comment.</summary>
    Task UpdateAsync(Guid nodeId, string name, string? storageRootPath, AiAccelerator? aiAccelerator = null,
        string? archiveRootPath = null, CancellationToken ct = default);

    /// <summary>Removes a node. Cameras assigned to it are unassigned (NodeId set null via
    /// DeleteBehavior.SetNull), not deleted — their recording just stops until reassigned.
    /// Segment rows already written by this node are untouched; they're historical recordings, not
    /// node state.</summary>
    Task DeleteAsync(Guid nodeId, CancellationToken ct = default);
}

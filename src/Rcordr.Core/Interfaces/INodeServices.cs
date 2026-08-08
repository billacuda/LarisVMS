using Rcordr.Core.Dtos;
using Rcordr.Core.Entities;

namespace Rcordr.Core.Interfaces;

/// <summary>Server-side (Rcordr.Web) node control plane: registration, auth, config snapshot, and
/// ingest of segment/status reports from nodes.</summary>
public interface INodeService
{
    Task<List<Node>> ListAsync(CancellationToken ct = default);

    Task<NodeRegisterResponse> RegisterAsync(NodeRegisterRequest request, CancellationToken ct = default);

    /// <summary>Validates "{nodeId}:{secret}" and, if valid, stamps LastSeenAt/Status/LastIpAddress.
    /// remoteIp is captured server-side from the connection, not self-reported by the node. Returns
    /// null for an invalid or unknown node — callers (NodeAuthMiddleware) treat that as 401. Does
    /// NOT update Version — middleware runs before the specific endpoint's body is bound, so it has
    /// no reported version to stamp; see UpdateStorageStatsAsync, called from the heartbeat handler
    /// which does have one.</summary>
    Task<Node?> AuthenticateAsync(string nodeId, string secret, string? remoteIp, CancellationToken ct = default);

    Task<NodeConfigResponse> GetConfigAsync(Guid nodeId, CancellationToken ct = default);

    Task RecordSegmentsAsync(Guid nodeId, IReadOnlyList<SegmentReportItem> segments, CancellationToken ct = default);

    /// <summary>Removes the Segment rows for files the node's StorageManager has already deleted
    /// from disk (retention/quota/watermark eviction), scoped to this node so one node can't claim
    /// to have deleted another's files.</summary>
    Task DeleteSegmentsAsync(Guid nodeId, IReadOnlyList<string> filePaths, CancellationToken ct = default);

    /// <summary>Applies real resolution/codec parsed from the node's own ffmpeg output to the
    /// matching CameraStream row(s) — only for cameras this node currently owns, so a stale report
    /// from a node a camera has since been reassigned away from can't overwrite it.</summary>
    Task UpdateStreamInfoAsync(Guid nodeId, IReadOnlyList<StreamInfoReportItem> items, CancellationToken ct = default);

    /// <summary>Persists the node's self-reported disk usage and version from its latest heartbeat.
    /// A null version leaves the stored value unchanged rather than clearing it.</summary>
    Task UpdateStorageStatsAsync(Guid nodeId, long? freeBytes, long? totalBytes, string? version, CancellationToken ct = default);

    /// <summary>Rough "days of retention remaining" per node: free bytes divided by that node's
    /// measured write rate over the last 24h. Null for a node with no free-space report yet or no
    /// recent writes to estimate a rate from — an honest "unknown" rather than a misleading number
    /// from too little history. Not SMART/disk-health, just a bytes-in / bytes-free projection.</summary>
    Task<Dictionary<Guid, double?>> GetEstimatedDaysRemainingAsync(CancellationToken ct = default);

    Task AssignCameraAsync(Guid cameraId, Guid? nodeId, CancellationToken ct = default);

    /// <summary>Updates the node's name and per-node storage root override. A blank
    /// storageRootPath clears the override, falling back to the global Storage.RootPath setting.</summary>
    Task UpdateAsync(Guid nodeId, string name, string? storageRootPath, CancellationToken ct = default);

    /// <summary>Removes a node. Cameras assigned to it are unassigned (NodeId set null via
    /// DeleteBehavior.SetNull), not deleted — their recording just stops until reassigned.
    /// Segment rows already written by this node are untouched; they're historical recordings, not
    /// node state.</summary>
    Task DeleteAsync(Guid nodeId, CancellationToken ct = default);
}

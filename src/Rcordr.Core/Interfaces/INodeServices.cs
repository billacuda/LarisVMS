using Rcordr.Core.Dtos;
using Rcordr.Core.Entities;

namespace Rcordr.Core.Interfaces;

/// <summary>Server-side (Rcordr.Web) node control plane: registration, auth, config snapshot, and
/// ingest of segment/status reports from nodes.</summary>
public interface INodeService
{
    Task<List<Node>> ListAsync(CancellationToken ct = default);

    Task<NodeRegisterResponse> RegisterAsync(NodeRegisterRequest request, CancellationToken ct = default);

    /// <summary>Validates "{nodeId}:{secret}" and, if valid, stamps LastSeenAt/Version. Returns
    /// null for an invalid or unknown node — callers (NodeAuthMiddleware) treat that as 401.</summary>
    Task<Node?> AuthenticateAsync(string nodeId, string secret, string? version, CancellationToken ct = default);

    Task<NodeConfigResponse> GetConfigAsync(Guid nodeId, CancellationToken ct = default);

    Task RecordSegmentsAsync(Guid nodeId, IReadOnlyList<SegmentReportItem> segments, CancellationToken ct = default);

    Task AssignCameraAsync(Guid cameraId, Guid? nodeId, CancellationToken ct = default);
}

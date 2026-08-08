using Rcordr.Core.Enums;

namespace Rcordr.Core.Entities;

/// <summary>
/// A recorder node — a separate Windows Service process (Rcordr.Node) that owns FFmpeg-based
/// recording for whichever cameras are assigned to it (Camera.NodeId). Auth is a bearer secret
/// `{nodeId}:{secret}`, SHA-256 hashed and compared with fixed-time equality by NodeAuthMiddleware —
/// the same shape as dploid's AgentAuthMiddleware. Secret rotation (PreviousApiKeyHash) and the
/// nonce/replay hardening dploid's agent protocol has are deliberately not wired up yet; this first
/// cut is a single long-lived secret per node, which is enough to get 24/7 recording working and
/// doesn't block adding rotation later without a wire-protocol change.
/// </summary>
public class Node
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;
    public string? PreviousApiKeyHash { get; set; }
    public string? Version { get; set; }
    public string? Platform { get; set; }
    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Camera> Cameras { get; set; } = [];
}

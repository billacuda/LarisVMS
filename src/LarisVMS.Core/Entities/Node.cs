using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// A recorder node — a separate Windows Service process (LarisVMS.Node) that owns FFmpeg-based
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

    /// <summary>Per-node override of the global Storage.RootPath setting — a node writing to its
    /// own local disk instead of the shared SMB target, for example. Null means "use the global
    /// default"; see NodeService.GetConfigAsync for the resolution order.</summary>
    public string? StorageRootPath { get; set; }

    public string ApiKeyHash { get; set; } = string.Empty;
    public string? PreviousApiKeyHash { get; set; }
    public string? Version { get; set; }
    public string? Platform { get; set; }
    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The remote IP the node last authenticated from (register or heartbeat), captured
    /// server-side from the connection rather than self-reported — a node can't spoof what it
    /// doesn't get to say. Useful for spotting a node on the wrong subnet/VLAN or one whose IP
    /// changed unexpectedly.</summary>
    public string? LastIpAddress { get; set; }

    /// <summary>Free/total bytes on the storage root's volume, self-reported by the node on every
    /// heartbeat (it's the only side that can actually measure its own disk/SMB share). Drives the
    /// Admin/Nodes usage bar and the M4 "days of retention remaining" estimate.</summary>
    public long? StorageFreeBytes { get; set; }
    public long? StorageTotalBytes { get; set; }
    public DateTime? StorageStatsUpdatedAt { get; set; }

    /// <summary>Port the node's own Kestrel host listens on for media (M5) — plain HTTP, LAN-only,
    /// reachable only from LarisVMS.Web (see "Media path" in the plan: every browser request is
    /// proxied through IIS, nothing ever connects to a node directly, so the node never needs its
    /// own TLS certificate). Combined with LastIpAddress, this is the address LarisVMS.Web dials to
    /// proxy a live view request through.</summary>
    public int? LivePort { get; set; }

    /// <summary>HMAC key used to sign the short-lived media tokens LarisVMS.Web issues for a live
    /// view request — generated once at registration, encrypted at rest like every other sensitive
    /// column. The node validates a token locally against this key with no DB round trip.</summary>
    public string? MediaSigningKey { get; set; }

    public ICollection<Camera> Cameras { get; set; } = [];
}

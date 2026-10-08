namespace LarisVMS.Core.Enums;

public enum NodeStatus
{
    Pending = 0,
    Active = 1,
    Offline = 2,
    Disabled = 3
}

/// <summary>Approval gate on an uploaded/registered NodeBuildVersion — only an Approved build is
/// ever offered to a node's heartbeat (see NodeBuildService.GetLatestForPlatformAsync). A build
/// lands as Pending, whether registered by deploy.ps1 or (legacy) the admin upload page, and stays
/// inert until an admin approves it on Admin/NodeBuilds.</summary>
public enum NodeBuildStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    /// <summary>Was Pending, but a newer build of the same platform is pending or already approved,
    /// so approving this one would only roll nodes back. Set automatically
    /// (NodeBuildService.SupersedeOutdatedPendingAsync); never offered to a node.</summary>
    Superseded = 3
}

/// <summary>Recording-failover state for a node (LarisVMS failover plan, phase 3). Written only by
/// LarisVMS.Web's <c>RecordingFailoverService</c> off the quorum tally (or an admin maintenance
/// toggle) and read in the recording-node resolver — never a live health check in the hot path.</summary>
public enum NodeFailoverState
{
    /// <summary>This node records its own cameras; no failover in effect either direction.</summary>
    Normal = 0,

    /// <summary>This node is considered down — a quorum of voters agreed, or an admin put it in
    /// maintenance — so its cameras are being recorded by its backup node until it returns.</summary>
    FailedOverAway = 1,

    /// <summary>This node is healthy and is currently carrying one or more <em>other</em> nodes'
    /// cameras because those nodes are <see cref="FailedOverAway"/> and this node is their backup.</summary>
    HostingFailover = 2
}

/// <summary>Why a node is <see cref="NodeFailoverState.FailedOverAway"/> — drives the Admin badge
/// wording ("offline" vs "in maintenance") and whether failback is under quorum control or held
/// until an operator clears maintenance.</summary>
public enum NodeFailoverReason
{
    /// <summary>A majority of the responding voters (partner node, central, assigned proxy) agreed
    /// the node's service was not running. Failback happens automatically once the inverted majority
    /// agrees it is back.</summary>
    QuorumOffline = 0,

    /// <summary>An admin toggled the node into maintenance mode. The quorum probe is skipped and the
    /// cameras do not fail back until maintenance is turned off.</summary>
    Maintenance = 1
}

/// <summary>Per-camera-stream recording state on a node. Not persisted to the database — this is
/// in-memory supervisor state, reported to the control plane via the status heartbeat.</summary>
public enum StreamRecordingState
{
    Idle = 0,
    Connecting = 1,
    Recording = 2,
    Failed = 3,
    Backoff = 4
}

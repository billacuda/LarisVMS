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
    Rejected = 2
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

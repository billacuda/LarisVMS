namespace NidusVMS.Core.Enums;

public enum NodeStatus
{
    Pending = 0,
    Active = 1,
    Offline = 2,
    Disabled = 3
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

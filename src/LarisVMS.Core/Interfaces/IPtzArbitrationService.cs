namespace LarisVMS.Core.Interfaces;

/// <summary>
/// Roles/permissions overhaul, pass 4: in-memory PTZ priority arbitration. A camera's PTZ control
/// used to be pure first-come (whoever sends a command wins, no contention handling at all); this
/// adds priority-based pre-emption — a higher-priority holder (RoleProfile.PtzPriorityLevel) locks
/// out lower-priority commands on that camera until PtzLockoutSeconds after their own last command.
/// Deliberately not persisted (a live hold has no meaning across an app restart — every camera simply
/// reverts to unheld, matching today's behavior on every restart already) and deliberately in-memory
/// singleton state, not per-camera DB rows: this is a live contention lock, not configuration.
/// </summary>
public interface IPtzArbitrationService
{
    /// <summary>Read-only decision: would a command from this requester be granted right now? Never
    /// mutates the current hold — safe to call from both /move's pre-check and /stop, which must
    /// never itself refresh the lockout timer.</summary>
    PtzAcquireResult TryAcquire(Guid cameraId, string userId, int priorityLevel, DateTime nowUtc);

    /// <summary>Establishes or refreshes this requester as the current holder of a camera's PTZ
    /// control. Call only after a real PTZ move has actually succeeded against the camera — never on
    /// a mere view, and never from /stop (lockout is strictly time-based, not released early by
    /// stopping).</summary>
    void RecordCommand(Guid cameraId, string userId, int priorityLevel, int lockoutSeconds, DateTime nowUtc);
}

public readonly record struct PtzAcquireResult(bool Granted, int? RetryAfterSeconds);

using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>A camera's current PTZ hold, as PtzArbitrationService stores it — an immutable snapshot
/// so the pure decision function below never touches the live ConcurrentDictionary directly.</summary>
public readonly record struct PtzHoldSnapshot(string UserId, int PriorityLevel, int LockoutSeconds, DateTime LastCommandUtc);

/// <summary>
/// Pure decision logic behind PtzArbitrationService.TryAcquire — extracted so the actual arbitration
/// rule is unit-testable without a real ConcurrentDictionary or wall-clock time.
/// </summary>
public static class PtzArbitrationPolicy
{
    /// <summary>Grant when: nobody currently holds the camera, the requester already holds it
    /// themselves, the requester's priority is strictly higher than the current holder's (equal
    /// priority does not pre-empt), or the current holder's own lockout window — LockoutSeconds after
    /// their own LastCommandUtc, never the requester's window — has elapsed. Otherwise denied, with
    /// how many more seconds remain on the current holder's lockout.</summary>
    public static PtzAcquireResult CanAcquire(PtzHoldSnapshot? currentHold, string requesterId, int requesterPriority, DateTime nowUtc)
    {
        if (currentHold is not { } hold) return new PtzAcquireResult(true, null);
        if (hold.UserId == requesterId) return new PtzAcquireResult(true, null);
        if (requesterPriority > hold.PriorityLevel) return new PtzAcquireResult(true, null);

        var remainingSeconds = hold.LockoutSeconds - (nowUtc - hold.LastCommandUtc).TotalSeconds;
        return remainingSeconds <= 0
            ? new PtzAcquireResult(true, null)
            : new PtzAcquireResult(false, (int)Math.Ceiling(remainingSeconds));
    }
}

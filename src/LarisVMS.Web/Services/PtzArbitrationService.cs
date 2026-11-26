using System.Collections.Concurrent;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <inheritdoc cref="IPtzArbitrationService" />
/// <remarks>Registered as a singleton (Program.cs) — one shared in-process hold table for the whole
/// app, not per-request state. Safe under concurrency: ConcurrentDictionary's own thread safety is
/// all this needs, since a lost race between two RecordCommand calls for the same camera is a benign
/// "last write wins" on who's recorded as the current holder, not a correctness issue.</remarks>
public sealed class PtzArbitrationService : IPtzArbitrationService
{
    private readonly ConcurrentDictionary<Guid, PtzHoldSnapshot> _holds = new();

    public PtzAcquireResult TryAcquire(Guid cameraId, string userId, int priorityLevel, DateTime nowUtc)
    {
        var currentHold = _holds.TryGetValue(cameraId, out var hold) ? hold : (PtzHoldSnapshot?)null;
        return PtzArbitrationPolicy.CanAcquire(currentHold, userId, priorityLevel, nowUtc);
    }

    public void RecordCommand(Guid cameraId, string userId, int priorityLevel, int lockoutSeconds, DateTime nowUtc)
    {
        // Checked again: the camera was commanded in between, and someone with a higher priority may have
        // taken it meanwhile. Their hold stays.
        lock (_recordGate)
        {
            if (TryAcquire(cameraId, userId, priorityLevel, nowUtc).Granted)
                _holds[cameraId] = new PtzHoldSnapshot(userId, priorityLevel, lockoutSeconds, nowUtc);
        }
    }

    private readonly Lock _recordGate = new();
}

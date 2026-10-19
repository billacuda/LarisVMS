using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

/// <summary>
/// Evidence locks: footage retention must not delete, until a chosen date or indefinitely. Removal
/// is allowed for the lock's creator or a Super Admin, and leaves the footage protected for
/// EvidenceLockRules.RemovalGrace (restorable in that window). Every change is audited here.
/// </summary>
public interface IEvidenceLockService
{
    /// <summary>One lock per camera. Throws <see cref="EvidenceLockValidationException"/> for a bad
    /// range or duration. Camera access is the caller's job.</summary>
    Task<IReadOnlyList<EvidenceLockDto>> CreateAsync(CreateEvidenceLockRequest request, EvidenceLockActor actor, CancellationToken ct = default);

    /// <summary>Throws <see cref="EvidenceLockForbiddenException"/> unless the actor created the lock
    /// or is a Super Admin; <see cref="KeyNotFoundException"/> for an unknown id.</summary>
    Task<EvidenceLockDto> RemoveAsync(Guid id, string reason, EvidenceLockActor actor, CancellationToken ct = default);

    /// <summary>Undoes a removal while the grace period lasts. Same permission rule as removal.</summary>
    Task<EvidenceLockDto> RestoreAsync(Guid id, EvidenceLockActor actor, CancellationToken ct = default);

    /// <summary>Locks on the given cameras (null = all), optionally only those overlapping a time
    /// window, and optionally including released and expired ones.</summary>
    Task<IReadOnlyList<EvidenceLockDto>> ListAsync(IReadOnlyCollection<Guid>? cameraIds, DateTime? fromUtc, DateTime? toUtc,
        bool includeInactive, EvidenceLockActor? viewer, CancellationToken ct = default);

    /// <summary>Every range still protected right now (active, or removed within the grace period).</summary>
    Task<List<NodeConfigProtectedRangeDto>> GetProtectedRangesAsync(IReadOnlyCollection<Guid>? cameraIds, CancellationToken ct = default);
}

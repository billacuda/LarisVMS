namespace LarisVMS.Core.Dtos;

/// <summary>Placing a lock on one or more cameras' footage. Duration is one of the
/// EvidenceLockRules.Durations keys ("1d" … "30d"), "custom" (with CustomUntilUtc), or "indefinite".</summary>
public record CreateEvidenceLockRequest(
    IReadOnlyList<Guid> CameraIds, DateTime FromUtc, DateTime ToUtc, string Duration, DateTime? CustomUntilUtc, string Reason);

public record RemoveEvidenceLockRequest(string Reason);

public record EvidenceLockDto(
    Guid Id, Guid CameraId, string CameraName, DateTime FromUtc, DateTime ToUtc, string Reason,
    DateTime? ExpiresAtUtc, string CreatedByUserId, string? CreatedByUserName, DateTime CreatedAtUtc,
    DateTime? RemovedAtUtc, string? RemovedByUserName, string? RemovalReason,
    /// <summary>Active, RemovedInGrace, Expired, or Released.</summary>
    string Status,
    /// <summary>When protection ends; null for an active indefinite lock.</summary>
    DateTime? ProtectedUntilUtc,
    /// <summary>Recorded footage inside the range, in bytes.</summary>
    long SizeBytes,
    /// <summary>Whether the signed-in user may remove (or restore) it: its creator, or a Super Admin.</summary>
    bool CanManage);

/// <summary>The acting user for a lock change — enough to check "creator or Super Admin" and to audit it.</summary>
public record EvidenceLockActor(string UserId, string? UserName, bool IsSuperAdmin, string? IpAddress);

public class EvidenceLockValidationException(string message) : Exception(message);

public class EvidenceLockForbiddenException(string message) : Exception(message);

using System.Security.Claims;

namespace LarisVMS.Core.Interfaces;

/// <summary>Roles/permissions overhaul, pass 4: resolves the acting principal's PTZ arbitration
/// priority from their held roles' RoleProfile.PtzPriorityLevel/PtzLockoutSeconds — a small, DB-backed
/// scoped service, separate from the in-memory singleton IPtzArbitrationService, which holds only the
/// live per-camera contention state and has no database access of its own.</summary>
public interface IPtzPriorityResolver
{
    /// <summary>The highest PtzPriorityLevel across every role the user holds, paired with that same
    /// role's PtzLockoutSeconds (the two are never resolved from different roles) — null if every held
    /// role has no priority configured (every custom role by default, or Investigator/Auditor,
    /// Guest/Viewer, API/Integration, which never participate in PTZ arbitration at all). A null
    /// result means "skip arbitration entirely," not "lowest priority" — see PtzArbitrationService's
    /// own doc comment for why that distinction matters for migration safety.</summary>
    Task<PtzPriorityInfo?> GetPtzPriorityAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

public readonly record struct PtzPriorityInfo(int PriorityLevel, int LockoutSeconds);

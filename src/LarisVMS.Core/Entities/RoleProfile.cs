using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// Metadata for the roles/permissions overhaul, layered onto an AspNetRoles row the same way
/// Permission.RoleId already is — a parallel table, not a subclassed IdentityRole, so every existing
/// RoleManager&lt;IdentityRole&gt; injection site is untouched. A role with no RoleProfile row (any
/// admin-created custom role predating this overhaul, or one created without opting in) is not an
/// error — every consumer must default it to the safe values documented on each property below,
/// exactly matching that role's behavior before this overhaul existed.
/// </summary>
public class RoleProfile
{
    /// <summary>FK-by-convention to AspNetRoles.Id — no navigation property, matching
    /// Permission.RoleId's own convention.</summary>
    public string RoleId { get; set; } = string.Empty;

    /// <summary>Stable, install-independent identifier for the 8 built-in roles ("SUPER", "ADMIN",
    /// "SEC-MGR", "OPERATOR", "AUDIT", "TECH", "VIEWER", "API") — used wherever code needs to
    /// recognize a built-in role's identity without depending on its (renamable) display Name.
    /// Null/absent for a custom role that never got a RoleProfile row.</summary>
    public string Tag { get; set; } = string.Empty;

    public RoleScopeTier MaxScopeTier { get; set; } = RoleScopeTier.Group;

    public bool AutoExpires { get; set; }

    /// <summary>Default lifetime for a new assignment of this role, in minutes (e.g. 480 = 8h shift,
    /// 43200 = 30d, 1440 = 24h). Null when AutoExpires is false. An admin can still override any
    /// individual assignment's own expiry regardless of this default — see RoleAssignmentExpiry.</summary>
    public int? DefaultExpiryMinutes { get; set; }

    /// <summary>PTZ arbitration priority (1 lowest – 9999 highest). Null means this role never
    /// participates in PTZ priority arbitration at all — not "lowest priority," but "opts out of
    /// arbitration entirely," preserving today's unrestricted first-come PTZ behavior for any role
    /// with no configured priority (every custom role, until an admin opts it in).</summary>
    public int? PtzPriorityLevel { get; set; }

    /// <summary>Seconds a lower-priority principal is locked out of PTZ control after this role's
    /// last command, once pre-empted. Null iff PtzPriorityLevel is null.</summary>
    public int? PtzLockoutSeconds { get; set; }

    /// <summary>True for the 8 built-in roles this overhaul seeds. False for anything an admin
    /// created themselves — informational only today (nothing yet blocks editing a system role's
    /// profile), but keeps built-in and custom roles distinguishable in the UI.</summary>
    public bool IsSystemRole { get; set; }
}

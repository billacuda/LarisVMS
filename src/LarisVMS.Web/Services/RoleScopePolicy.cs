using LarisVMS.Core.Enums;

namespace LarisVMS.Web.Services;

/// <summary>
/// Pure decision logic behind Admin → Settings → Camera Access's scope-tier enforcement (roles/
/// permissions overhaul, pass 2). A role's RoleProfile.MaxScopeTier caps how wide a CameraAccess
/// grant it can be given: only an Org-tier role can be granted every camera; any role can be granted
/// any camera group (a site or a group inside one) or single cameras.
/// </summary>
public static class RoleScopePolicy
{
    /// <param name="maxScopeTier">The granted role's RoleProfile.MaxScopeTier — pass RoleScopeTier.Group
    /// (the safe default) for a role with no RoleProfile row.</param>
    /// <param name="scopeType">The scope type of the grant being added.</param>
    /// <param name="targetIsSite">For a Group-scoped grant, whether the target CameraGroup is a site
    /// (ParentId == null). Ignored for All/Camera scopes.</param>
    public static bool CanGrant(RoleScopeTier maxScopeTier, CameraAccessScopeType scopeType, bool targetIsSite) =>
        scopeType switch
        {
            CameraAccessScopeType.All => maxScopeTier == RoleScopeTier.Org,
            CameraAccessScopeType.Group => true,
            _ => true
        };

    /// <summary>User-facing reason a denied grant was denied, for the admin page's error message.
    /// Only meaningful when CanGrant returned false for the same arguments.</summary>
    public static string DenialReason(RoleScopeTier maxScopeTier, CameraAccessScopeType scopeType) =>
        $"This role is limited to {maxScopeTier} scope and can't be granted every camera — choose a camera group or single cameras, " +
        "or raise the role's scope tier under Settings → Permissions → Roles.";

    /// <summary>Pass 3's Roles.Assign tier cap (permission_matrix.txt row "Assign roles &amp;
    /// permissions"): an Org-tier actor can assign any role; anyone else can assign any role except
    /// an Org-tier one — an Org-tier role reaches the entire deployment, so only someone who already
    /// holds Org-tier reach themselves can hand more of it out.</summary>
    /// <param name="actingUserHighestTier">The MIN (most-permissive) MaxScopeTier across every role
    /// the acting user already holds — pass RoleScopeTier.Group if they hold no RoleProfile-backed
    /// role at all.</param>
    /// <param name="targetRoleTier">The MaxScopeTier of the role being added to someone.</param>
    public static bool CanAssignRole(RoleScopeTier actingUserHighestTier, RoleScopeTier targetRoleTier) =>
        actingUserHighestTier == RoleScopeTier.Org || targetRoleTier != RoleScopeTier.Org;
}

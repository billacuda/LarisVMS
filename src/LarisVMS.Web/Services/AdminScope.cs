using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// What an admin who isn't a Super Admin may hand out, so admin tools can't be used to climb: no
/// editing a role you hold yourself (its permissions, tier, or camera access), no raising a role above
/// your own reach, no granting a permission you don't have, and no binding Super Admin — or a role you
/// couldn't assign to a person — to an API key or an AD group. A Super Admin may do all of it.
/// </summary>
public sealed class AdminScope
{
    private readonly HashSet<string> _heldRoleIds;
    private readonly HashSet<string> _superRoleIds;
    private readonly Dictionary<string, RoleScopeTier> _tiers;
    private readonly PermissionSet _granted;

    private AdminScope(bool isSuperAdmin, RoleScopeTier highestTier, HashSet<string> heldRoleIds,
        HashSet<string> superRoleIds, Dictionary<string, RoleScopeTier> tiers, PermissionSet granted)
    {
        IsSuperAdmin = isSuperAdmin;
        HighestTier = highestTier;
        _heldRoleIds = heldRoleIds;
        _superRoleIds = superRoleIds;
        _tiers = tiers;
        _granted = granted;
    }

    public bool IsSuperAdmin { get; }

    /// <summary>The widest tier among the admin's own roles.</summary>
    public RoleScopeTier HighestTier { get; }

    public static async Task<AdminScope> ForAsync(ApplicationDbContext db, IPermissionService permissions,
        ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var held = userId is null ? [] : (await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct)).ToHashSet();
        var profiles = await db.RoleProfiles.AsNoTracking().Select(p => new { p.RoleId, p.Tag, p.MaxScopeTier }).ToListAsync(ct);
        var superRoleIds = profiles.Where(p => p.Tag == RoleTags.SuperAdmin).Select(p => p.RoleId).ToHashSet();
        var tiers = profiles.ToDictionary(p => p.RoleId, p => p.MaxScopeTier);
        var heldTiers = held.Where(tiers.ContainsKey).Select(r => tiers[r]).ToList();
        return new AdminScope(held.Overlaps(superRoleIds), heldTiers.Count == 0 ? RoleScopeTier.Group : heldTiers.Min(),
            held, superRoleIds, tiers, await permissions.GetGrantedAsync(user, ct));
    }

    public bool HoldsRole(string roleId) => _heldRoleIds.Contains(roleId);

    /// <summary>May bind <paramref name="roleId"/> to an API key or AD group.</summary>
    public bool CanBindRole(string roleId) =>
        IsSuperAdmin || (!_superRoleIds.Contains(roleId)
            && RoleScopePolicy.CanAssignRole(HighestTier, _tiers.GetValueOrDefault(roleId, RoleScopeTier.Group)));

    /// <summary>May change a role's permissions, tier, or camera access.</summary>
    public bool CanEditRole(string roleId) => IsSuperAdmin || (!_heldRoleIds.Contains(roleId) && !_superRoleIds.Contains(roleId));

    public bool CanSetTier(RoleScopeTier tier) => IsSuperAdmin || tier >= HighestTier;

    public bool CanGrant(string resource, string action) => IsSuperAdmin || _granted.Has(resource, action);

    public const string OwnRoleMessage = "You can't change a role you hold yourself, or the Super Admin role.";
}

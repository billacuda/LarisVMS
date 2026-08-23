using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class PermissionService(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    : IPermissionService
{
    public async Task<bool> HasPermissionAsync(string userId, string resource, string action, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return false;

        var roles = await userManager.GetRolesAsync(user);
        if (roles.Count == 0) return false;

        var roleIds = await db.Roles
            .Where(r => roles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        // Super Admin implicitly has every permission — it's the role that grants the matrix, so it
        // can't depend on rows in the matrix itself without risking a self-referential lockout.
        // Resolved by RoleProfile.Tag rather than the role's (renamable) Name — see RoleSeedService.
        if (await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == RoleTags.SuperAdmin, ct))
            return true;

        return await db.Permissions.AnyAsync(p =>
            roleIds.Contains(p.RoleId) && p.Resource == resource && p.Action == action, ct);
    }

    public async Task<PermissionSet> GetGrantedAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.Identity?.IsAuthenticated != true) return PermissionSet.None;

        // Role names come from the principal's own claims, not a fresh userManager.GetRolesAsync
        // call — ASP.NET Core Identity's default UserClaimsPrincipalFactory<TUser, TRole> (the one
        // AddRoles<IdentityRole>() registers in Program.cs) already embeds every role as a
        // ClaimTypes.Role claim on the authentication cookie's principal, refreshed periodically by
        // the framework's own SecurityStampValidator — reading it here costs nothing, where
        // re-querying it would be exactly the kind of per-call database cost this method exists to
        // avoid on a view that renders on every single page in the app.
        var roleNames = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (roleNames.Count == 0) return PermissionSet.None;

        var roleIds = await db.Roles
            .Where(r => roleNames.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == RoleTags.SuperAdmin, ct))
            return new PermissionSet(true, new HashSet<(string, string)>());

        var granted = await db.Permissions
            .Where(p => roleIds.Contains(p.RoleId))
            .Select(p => new { p.Resource, p.Action })
            .ToListAsync(ct);

        return new PermissionSet(false, granted.Select(g => (g.Resource, g.Action)).ToHashSet());
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

        // Administrator implicitly has every permission — it's the role that grants the matrix, so
        // it can't depend on rows in the matrix itself without risking a self-referential lockout.
        if (roles.Contains("Administrator")) return true;

        var roleIds = await db.Roles
            .Where(r => roles.Contains(r.Name!))
            .Select(r => r.Id)
            .ToListAsync(ct);

        return await db.Permissions.AnyAsync(p =>
            roleIds.Contains(p.RoleId) && p.Resource == resource && p.Action == action, ct);
    }
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IPtzPriorityResolver" />
public class PtzPriorityResolver(ApplicationDbContext db) : IPtzPriorityResolver
{
    public async Task<PtzPriorityInfo?> GetPtzPriorityAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var roleNames = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        if (roleNames.Count == 0) return null;

        var roleIds = await db.Roles.Where(r => roleNames.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        if (roleIds.Count == 0) return null;

        // PriorityLevel and LockoutSeconds are always resolved together from the same RoleProfile
        // row (the one with the highest priority among the user's held roles) — never mixed across
        // two different roles.
        var best = await db.RoleProfiles
            .Where(p => roleIds.Contains(p.RoleId) && p.PtzPriorityLevel != null)
            .OrderByDescending(p => p.PtzPriorityLevel)
            .Select(p => new { p.PtzPriorityLevel, p.PtzLockoutSeconds })
            .FirstOrDefaultAsync(ct);

        return best is null ? null : new PtzPriorityInfo(best.PtzPriorityLevel!.Value, best.PtzLockoutSeconds!.Value);
    }
}

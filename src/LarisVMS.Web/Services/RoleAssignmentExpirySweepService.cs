using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Roles/permissions overhaul, pass 5: ticks every minute (same interval as AlertEvaluatorService —
/// promptly revoking access matters more than log-cleanup-style timing), finds every
/// RoleAssignmentExpiry row past its ExpiresAtUtc, removes that role from the user, deletes the row,
/// and audit-logs it. Deliberately kept separate from SessionLifetimePolicy — that ends a cookie;
/// this revokes the grant itself from AspNetUserRoles, which matters even for a session that never
/// expires on its own. See Program.cs's OnValidatePrincipal chain for the immediate-check half of
/// this (narrows the worst case from "up to one sweep interval" to "the very next request" for a user
/// who re-logs in with a not-yet-swept expired assignment still on file).
/// </summary>
public class RoleAssignmentExpirySweepService(IServiceScopeFactory scopeFactory, ILogger<RoleAssignmentExpirySweepService> logger)
    : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Role assignment expiry sweep tick failed — will retry on the next tick.");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var now = DateTime.UtcNow;
        var expired = await db.RoleAssignmentExpiries.Where(e => e.ExpiresAtUtc <= now).ToListAsync(ct);
        if (expired.Count == 0) return;

        foreach (var expiry in expired)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessOneAsync(db, userManager, auditService, expiry, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to process expired role assignment {ExpiryId} (user {UserId}, role {RoleId}) — will retry on the next tick.",
                    expiry.Id, expiry.UserId, expiry.RoleId);
            }
        }
    }

    private async Task ProcessOneAsync(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
        IAuditService auditService, RoleAssignmentExpiry expiry, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(expiry.UserId);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == expiry.RoleId, ct);
        if (user is null || role?.Name is null)
        {
            // The user or role was deleted out from under this row by some other path (Roles page's
            // own delete handler is supposed to clean these up, but a direct DB edit or a missed spot
            // could leave one dangling) — nothing left to revoke, just clear the now-meaningless row.
            db.RoleAssignmentExpiries.Remove(expiry);
            await db.SaveChangesAsync(ct);
            return;
        }

        var tag = (await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == expiry.RoleId, ct))?.Tag;
        if (tag == RoleTags.SuperAdmin)
        {
            var otherEnabledSuperAdmins = await CountOtherEnabledSuperAdminsAsync(db, expiry.RoleId, user.Id, ct);
            if (otherEnabledSuperAdmins == 0)
            {
                // Defensive guard, not expected to trigger in normal operation — Super Admin never
                // auto-expires by default (see RoleSeedService), so this row can only exist here via a
                // direct DB edit or an admin deliberately overriding a specific assignment's expiry.
                // Leave both the role and this row untouched rather than silently stripping the last
                // Super Admin; keep warning every tick until an admin resolves it (assign another
                // Super Admin, or clear this row's expiry).
                logger.LogWarning("Role assignment expiry for user {UserId}'s Super Admin role was due, but " +
                    "they're the last enabled Super Admin — leaving the role and this expiry row in place.", user.Id);
                return;
            }
        }

        await userManager.RemoveFromRoleAsync(user, role.Name);
        db.RoleAssignmentExpiries.Remove(expiry);
        await db.SaveChangesAsync(ct);
        await auditService.LogAsync("Role.Expired", null, "System", null, $"{user.Email}: \"{role.Name}\" expired and was removed.", ct);

        var remainingRoles = await userManager.GetRolesAsync(user);
        if (remainingRoles.Count == 0)
        {
            await userManager.SetLockoutEnabledAsync(user, true);
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await auditService.LogAsync("User.Disable", null, "System", null,
                $"{user.Email}: disabled automatically — their last role expired.", ct);
        }
    }

    private static async Task<int> CountOtherEnabledSuperAdminsAsync(ApplicationDbContext db, string superAdminRoleId,
        string excludingUserId, CancellationToken ct)
    {
        var adminUserIds = await db.UserRoles.Where(ur => ur.RoleId == superAdminRoleId).Select(ur => ur.UserId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.Users.CountAsync(u =>
            adminUserIds.Contains(u.Id) && u.Id != excludingUserId && (u.LockoutEnd == null || u.LockoutEnd < now), ct);
    }
}

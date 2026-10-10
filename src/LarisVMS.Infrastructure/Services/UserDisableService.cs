using Microsoft.AspNetCore.Identity;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// The one way to disable or re-enable an account, so every path (Users page, role expiry, AD sync)
/// also ends the user's sessions. Disabling locks the account out, rotates the security stamp, and
/// closes any open live-view relays. OnValidatePrincipal rejects a locked-out user's cookie on their
/// next request.
/// </summary>
public sealed class UserDisableService(
    UserManager<ApplicationUser> userManager,
    UserConnectionRegistry connections,
    IAuditService auditService)
{
    public async Task DisableAsync(ApplicationUser user, bool byDirectorySync,
        string? actorUserId, string? actorName, string? ip, string? details, CancellationToken ct = default)
    {
        // Set before the Identity calls below — each of them saves the whole user row.
        user.DisabledByDirectorySync = byDirectorySync;
        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        await userManager.UpdateSecurityStampAsync(user);
        connections.RevokeAll(user.Id);
        await auditService.LogAsync("User.Disable", actorUserId, actorName, ip, details ?? user.Email ?? user.UserName, ct);
    }

    public async Task EnableAsync(ApplicationUser user,
        string? actorUserId, string? actorName, string? ip, string? details, CancellationToken ct = default)
    {
        user.DisabledByDirectorySync = false;
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        await auditService.LogAsync("User.Enable", actorUserId, actorName, ip, details ?? user.Email ?? user.UserName, ct);
    }

    public static bool IsEnabled(ApplicationUser user) =>
        user.LockoutEnd is null || user.LockoutEnd < DateTimeOffset.UtcNow;
}

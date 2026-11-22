using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LarisVMS.Web.Security;

/// <summary>
/// What a wrong password does to an account. Everyone but a Super Admin gets Identity's normal
/// lockout. A Super Admin is never locked out for good, or the last one could be locked out of their
/// own system:
/// <list type="bullet">
/// <item>With two-factor on, a guessed password isn't enough to sign in, so nothing happens.</item>
/// <item>Without it, each failure past the first few makes the account wait before the next try, a
/// little longer each time (1, 5, 15, then 60 minutes), so guessing from many addresses at once
/// still gets nowhere. A successful sign-in resets it.</item>
/// </list>
/// </summary>
public static class AdminLockout
{
    /// <summary>Wrong passwords allowed before a Super Admin without two-factor has to wait.</summary>
    public const int FreeAttempts = 5;

    /// <summary>How long a Super Admin without two-factor waits after this many failures in a row.</summary>
    public static TimeSpan? DelayAfter(int failures) => failures switch
    {
        < FreeAttempts => null,
        < 10 => TimeSpan.FromMinutes(1),
        < 15 => TimeSpan.FromMinutes(5),
        < 20 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),
    };

    public static async Task<bool> IsSuperAdminAsync(ApplicationDbContext db, ApplicationUser user)
    {
        var roleIds = await db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.RoleId).ToListAsync();
        return roleIds.Count > 0 && await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == RoleTags.SuperAdmin);
    }

    /// <summary>Records a wrong password (or code) for <paramref name="user"/>. Returns the wait
    /// imposed on a Super Admin without two-factor, if one started.</summary>
    public static async Task<TimeSpan?> RecordFailureAsync(UserManager<ApplicationUser> users, ApplicationDbContext db, ApplicationUser user)
    {
        if (!await IsSuperAdminAsync(db, user))
        {
            await users.AccessFailedAsync(user);
            return null;
        }
        if (await users.GetTwoFactorEnabledAsync(user)) return null;

        // Counted here rather than with AccessFailedAsync, which would lock the account for the
        // standard time and start the count over at the standard limit.
        user.AccessFailedCount++;
        var delay = DelayAfter(user.AccessFailedCount);
        if (delay is { } wait)
        {
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.UtcNow + wait;
        }
        await users.UpdateAsync(user);
        return delay;
    }
}

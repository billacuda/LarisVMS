using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LarisVMS.Core;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Infrastructure.ActiveDirectory;

public sealed record AdSyncResult(int Users, int Created, int Updated, int Disabled, int Enabled, int Failed, int MissingGroups)
{
    public string Summary =>
        $"{Users} AD user(s): {Created} created, {Updated} updated, {Disabled} disabled, {Enabled} re-enabled" +
        (Failed > 0 ? $", {Failed} failed" : "") +
        (MissingGroups > 0 ? $"; {MissingGroups} linked group(s) missing in AD" : "") + ".";
}

/// <summary>
/// Applies AdSyncPlanner's decisions to LarisVMS accounts — for the scheduled full sync and for one
/// user at sign-in. Accounts are linked to AD by objectSid through Identity's own AspNetUserLogins
/// (provider "ActiveDirectory"), so a renamed AD account is still the same LarisVMS account.
/// </summary>
public sealed class ActiveDirectoryUserSync(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    UserDisableService disabler,
    IAuditService auditService,
    ILogger<ActiveDirectoryUserSync> logger)
{
    public const string LoginProvider = "ActiveDirectory";
    private const string SystemActor = "System";

    /// <summary>Reads the whole directory snapshot first; any LDAP failure throws before a single
    /// account is touched, so an AD outage can't disable everyone.</summary>
    public async Task<AdSyncResult> RunFullSyncAsync(ActiveDirectorySettings settings, IActiveDirectoryService directory,
        CancellationToken ct)
    {
        var links = await db.AdGroupRoleLinks.ToListAsync(ct);
        var snapshot = await directory.ReadSnapshotAsync(settings, links.Select(l => l.GroupSid).ToList(), ct);

        foreach (var link in links)
        {
            var group = snapshot.Groups.GetValueOrDefault(link.GroupSid);
            if (group is null)
            {
                if (!link.IsMissing) logger.LogWarning("Linked AD group {GroupName} ({Sid}) no longer exists in AD.", link.GroupName, link.GroupSid);
                link.IsMissing = true;
                continue;
            }
            link.IsMissing = false;
            link.GroupName = group.Name;
        }
        await db.SaveChangesAsync(ct);

        var activeLinks = links.Where(l => !l.IsMissing).Select(l => new AdLink(l.GroupSid, l.RoleId)).ToList();
        var existing = await LoadLinkedUsersAsync(null, ct);
        var actions = AdSyncPlanner.Plan(snapshot.Users, existing, activeLinks, fullSync: true);

        int created = 0, updated = 0, disabled = 0, enabled = 0, failed = 0;
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ApplyAsync(action, ct);
                if (action.Change == AdAccountChange.Create && outcome is not null) created++;
                if (action.Change == AdAccountChange.Disable) disabled++;
                if (action.Change == AdAccountChange.Enable) enabled++;
                if (action.IdentityChanged && action.Change != AdAccountChange.Create) updated++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex, "AD sync couldn't update the account for {Sid}; will retry next sync.", action.Sid);
            }
        }

        await WarnIfNoEnabledSuperAdminAsync(ct);

        return new AdSyncResult(actions.Count(a => a.Entry is not null), created, updated, disabled, enabled, failed,
            links.Count(l => l.IsMissing));
    }

    /// <summary>Syncs the one user signing in. Returns the account when they may sign in, or null when
    /// they're in no linked group or the account is disabled.</summary>
    public async Task<ApplicationUser?> SyncSignInAsync(AdUserEntry entry, CancellationToken ct)
    {
        var links = await db.AdGroupRoleLinks.AsNoTracking().Where(l => !l.IsMissing)
            .Select(l => new AdLink(l.GroupSid, l.RoleId)).ToListAsync(ct);
        var existing = await LoadLinkedUsersAsync(entry.Sid, ct);
        var action = AdSyncPlanner.Plan([entry], existing, links, fullSync: false).SingleOrDefault();
        if (action is null) return null;

        var user = await ApplyAsync(action, ct);
        return user is not null && UserDisableService.IsEnabled(user) ? user : null;
    }

    public async Task<IReadOnlyList<string>> GetLinkedGroupSidsAsync(CancellationToken ct) =>
        await db.AdGroupRoleLinks.AsNoTracking().Where(l => !l.IsMissing).Select(l => l.GroupSid).Distinct().ToListAsync(ct);

    private async Task<List<AdLinkedUserState>> LoadLinkedUsersAsync(string? onlySid, CancellationToken ct)
    {
        var loginQuery = db.UserLogins.AsNoTracking().Where(l => l.LoginProvider == LoginProvider);
        if (onlySid is not null) loginQuery = loginQuery.Where(l => l.ProviderKey == onlySid);
        var logins = await loginQuery.ToListAsync(ct);
        if (logins.Count == 0) return [];

        var userIds = logins.Select(l => l.UserId).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var roles = await db.UserRoles.AsNoTracking().Where(ur => userIds.Contains(ur.UserId)).ToListAsync(ct);

        return logins.Where(l => users.ContainsKey(l.UserId)).Select(l =>
        {
            var u = users[l.UserId];
            return new AdLinkedUserState(u.Id, l.ProviderKey, u.UserName, u.Email, u.DisplayName,
                !IsPermanentlyDisabled(u), u.DisabledByDirectorySync,
                roles.Where(r => r.UserId == u.Id).Select(r => r.RoleId).ToHashSet(StringComparer.Ordinal));
        }).ToList();
    }

    /// <summary>A short failed-password lockout isn't "disabled" for sync purposes — only the
    /// indefinite lockout the Users page and sync both use.</summary>
    internal static bool IsPermanentlyDisabled(ApplicationUser user) =>
        user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow.AddYears(100);

    private async Task<ApplicationUser?> ApplyAsync(AdUserAction action, CancellationToken ct)
    {
        ApplicationUser? user;
        var entry = action.Entry;

        if (action.Change == AdAccountChange.Create)
        {
            user = new ApplicationUser
            {
                UserName = entry!.SamAccountName,
                Email = entry.Email,
                EmailConfirmed = true,
                DisplayName = entry.DisplayName
            };
            var create = await userManager.CreateAsync(user);
            if (!create.Succeeded)
                throw new InvalidOperationException($"Couldn't create \"{entry.SamAccountName}\": {Describe(create)}");
            var link = await userManager.AddLoginAsync(user, new UserLoginInfo(LoginProvider, entry.Sid, "Active Directory"));
            if (!link.Succeeded)
            {
                await userManager.DeleteAsync(user);
                throw new InvalidOperationException($"Couldn't link \"{entry.SamAccountName}\" to its AD account: {Describe(link)}");
            }
            await auditService.LogAsync("User.Create", null, SystemActor, null,
                $"{entry.SamAccountName}: created from Active Directory.", ct);
        }
        else
        {
            user = await userManager.FindByIdAsync(action.UserId!);
            if (user is null) return null;

            if (action.IdentityChanged && entry is not null)
            {
                var details = AuditDiff.Build(
                    AuditDiff.Of("Username", user.UserName ?? "", entry.SamAccountName),
                    AuditDiff.Of("Email", user.Email ?? "", entry.Email ?? ""),
                    AuditDiff.Of("Display name", user.DisplayName ?? "", entry.DisplayName ?? ""));
                user.UserName = entry.SamAccountName;
                user.Email = entry.Email;
                user.DisplayName = entry.DisplayName;
                var update = await userManager.UpdateAsync(user);
                if (!update.Succeeded)
                    throw new InvalidOperationException($"Couldn't update \"{entry.SamAccountName}\": {Describe(update)}");
                if (details is not null)
                    await auditService.LogAsync("User.Update", null, SystemActor, null, $"{entry.SamAccountName} (from AD): {details}", ct);
            }
        }

        var name = user.UserName ?? user.Id;
        if (action.Change == AdAccountChange.Disable)
            await disabler.DisableAsync(user, byDirectorySync: true, null, SystemActor, null,
                $"{name}: disabled automatically — {action.Reason}.", ct);

        await SetRolesAsync(user, action.TargetRoleIds, ct);

        if (action.Change == AdAccountChange.Enable)
            await disabler.EnableAsync(user, null, SystemActor, null, $"{name}: re-enabled by Active Directory sync.", ct);

        return user;
    }

    /// <summary>AD owns an AD user's roles: make them exactly the target set, and drop any per-role
    /// expiry rows, which only make sense for hand-assigned roles.</summary>
    private async Task SetRolesAsync(ApplicationUser user, IReadOnlySet<string> targetRoleIds, CancellationToken ct)
    {
        var roleNamesById = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name!, ct);
        var current = (await userManager.GetRolesAsync(user)).ToHashSet(StringComparer.Ordinal);
        var target = targetRoleIds.Where(roleNamesById.ContainsKey).Select(id => roleNamesById[id]).ToHashSet(StringComparer.Ordinal);

        var toAdd = target.Except(current).ToList();
        var toRemove = current.Except(target).ToList();
        if (toAdd.Count > 0) await userManager.AddToRolesAsync(user, toAdd);
        if (toRemove.Count > 0) await userManager.RemoveFromRolesAsync(user, toRemove);

        var expiries = await db.RoleAssignmentExpiries.Where(e => e.UserId == user.Id).ToListAsync(ct);
        if (expiries.Count > 0)
        {
            db.RoleAssignmentExpiries.RemoveRange(expiries);
            await db.SaveChangesAsync(ct);
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            var details = AuditDiff.Build(AuditDiff.Of("Roles",
                string.Join(", ", current.OrderBy(r => r)), string.Join(", ", target.OrderBy(r => r))));
            if (details is not null)
                await auditService.LogAsync("User.Update", null, SystemActor, null, $"{user.UserName} (from AD): {details}", ct);
            // Role claims live in the cookie — rotate the stamp so the change applies on the next
            // security-stamp check rather than at the next sign-in.
            if (UserDisableService.IsEnabled(user)) await userManager.UpdateSecurityStampAsync(user);
        }
    }

    private async Task WarnIfNoEnabledSuperAdminAsync(CancellationToken ct)
    {
        var superAdminRoleId = await db.RoleProfiles.Where(p => p.Tag == RoleTags.SuperAdmin).Select(p => p.RoleId).FirstOrDefaultAsync(ct);
        if (superAdminRoleId is null) return;
        var adminIds = await db.UserRoles.Where(ur => ur.RoleId == superAdminRoleId).Select(ur => ur.UserId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (!await db.Users.AnyAsync(u => adminIds.Contains(u.Id) && (u.LockoutEnd == null || u.LockoutEnd < now), ct))
            logger.LogWarning("After AD sync there is no enabled Super Admin. Re-enable local sign-in and use the setup Super Admin to recover.");
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}

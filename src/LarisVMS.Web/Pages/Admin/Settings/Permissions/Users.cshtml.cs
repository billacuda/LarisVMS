using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings.Permissions;

/// <summary>
/// Create users, assign role(s), enable/disable, and reset passwords — the only admin surface for any
/// of this. Self-registration is disabled (<see cref="LarisVMS.Web.Middleware.RegistrationDisabledMiddleware"/>),
/// so this page is the sole way a new account gets provisioned. "Disabled" reuses ASP.NET Core
/// Identity's own lockout mechanism (<c>LockoutEnd = DateTimeOffset.MaxValue</c>) rather than a new
/// column — it already fails sign-in at the framework level, and re-enabling is just clearing the
/// same field. The last-Super-Admin guards below resolve the protected role by RoleProfile.Tag
/// rather than by name — see RoleManagementPolicy.
///
/// Roles/permissions overhaul, pass 3: the page itself only requires Users.Edit (create/edit/
/// deactivate/reset-password) — actually changing which roles a user holds additionally requires
/// Roles.Assign, checked inline in OnPostCreateAsync/OnPostSaveRolesAsync since Razor Pages
/// [Authorize] only applies at the page level, not per-handler. A caller with Users.Edit but not
/// Roles.Assign can fully manage accounts but any role selection they submit is silently ignored.
/// Roles.Assign itself is tier-capped (RoleScopePolicy.CanAssignRole): only an Org-tier holder can
/// assign an Org-tier role to someone.
/// </summary>
[Authorize("Users.Edit")]
public class UsersModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuthorizationService authorizationService,
    IAuditService auditService) : PageModel
{
    public record UserRow(string Id, string Email, string? DisplayName, bool IsEnabled, IReadOnlyList<string> RoleNames,
        IReadOnlyList<ExpiryRow> ActiveExpiries);

    public record ExpiryRow(Guid Id, string RoleName, DateTime ExpiresAtUtc);

    public List<UserRow> Users { get; set; } = [];
    public List<IdentityRole> Roles { get; set; } = [];

    [BindProperty] public string NewEmail { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;
    [BindProperty] public string? NewDisplayName { get; set; }
    [BindProperty] public List<string> NewRoleIds { get; set; } = [];

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        var email = NewEmail.Trim();
        if (string.IsNullOrEmpty(email))
        {
            ErrorMessage = "Enter an email address.";
            await LoadAsync(ct);
            return Page();
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(NewDisplayName) ? null : NewDisplayName.Trim()
        };

        var result = await userManager.CreateAsync(user, NewPassword);
        if (!result.Succeeded)
        {
            ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
            await LoadAsync(ct);
            return Page();
        }

        var assignableRoleIds = await FilterAssignableRoleIdsAsync(NewRoleIds, ct);
        var roleNames = await db.Roles.Where(r => assignableRoleIds.Contains(r.Id)).Select(r => r.Name!).ToListAsync(ct);
        if (roleNames.Count > 0) await userManager.AddToRolesAsync(user, roleNames);
        await SeedExpiriesForNewlyAssignedRolesAsync(user.Id, assignableRoleIds, ct);

        await auditService.LogAsync("User.Create", CurrentUserId, CurrentUserName, RemoteIp, email, ct);
        SavedMessage = assignableRoleIds.Count < NewRoleIds.Count
            ? $"User \"{email}\" created. Some role selections weren't applied — you don't have permission to assign them."
            : $"User \"{email}\" created.";
        NewEmail = string.Empty;
        NewPassword = string.Empty;
        NewDisplayName = null;
        NewRoleIds = [];
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveRolesAsync(string userId, [FromForm] List<string> roleIds, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            ErrorMessage = "That user no longer exists.";
            await LoadAsync(ct);
            return Page();
        }

        var currentRoles = await userManager.GetRolesAsync(user);
        var assignableRoleIds = await FilterAssignableRoleIdsAsync(roleIds, ct);
        var targetRoleNames = await db.Roles.Where(r => assignableRoleIds.Contains(r.Id)).Select(r => r.Name!).ToListAsync(ct);

        var wasAdmin = await IncludesSuperAdminAsync(currentRoles, ct);
        var staysAdmin = await IncludesSuperAdminAsync(targetRoleNames, ct);
        if (UserManagementPolicy.WouldRemoveLastAdministrator(wasAdmin, staysAdmin, await CountOtherEnabledSuperAdminsAsync(user.Id, ct)))
        {
            ErrorMessage = "Can't remove the last Super Admin's Super Admin role.";
            await LoadAsync(ct);
            return Page();
        }

        var toAdd = targetRoleNames.Except(currentRoles).ToList();
        var toRemove = currentRoles.Except(targetRoleNames).ToList();
        if (toAdd.Count > 0) await userManager.AddToRolesAsync(user, toAdd);
        if (toRemove.Count > 0) await userManager.RemoveFromRolesAsync(user, toRemove);

        // Pass 5: a newly-added auto-expiring role gets a fresh RoleAssignmentExpiry row (default
        // now + RoleProfile.DefaultExpiryMinutes — see OnPostExtendExpiryAsync for the
        // admin-override path); a role that's no longer held has its expiry row cleaned up alongside
        // it, mirroring the same cleanup Roles.cshtml.cs's own delete handler already does.
        var addedRoleIds = await db.Roles.Where(r => toAdd.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        await SeedExpiriesForNewlyAssignedRolesAsync(user.Id, addedRoleIds, ct);
        var removedRoleIds = await db.Roles.Where(r => toRemove.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        if (removedRoleIds.Count > 0)
        {
            db.RoleAssignmentExpiries.RemoveRange(
                db.RoleAssignmentExpiries.Where(e => e.UserId == user.Id && removedRoleIds.Contains(e.RoleId)));
            await db.SaveChangesAsync(ct);
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            var details = AuditDiff.Build(AuditDiff.Of("Roles",
                string.Join(", ", currentRoles.OrderBy(r => r)), string.Join(", ", targetRoleNames.OrderBy(r => r))));
            if (details is not null)
                await auditService.LogAsync("User.Update", CurrentUserId, CurrentUserName, RemoteIp, details, ct);
        }

        SavedMessage = assignableRoleIds.Count < roleIds.Count
            ? "Saved. Some role selections weren't applied — you don't have permission to assign them."
            : "Saved.";
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Drops any requested role id the caller isn't allowed to assign: all of them if the
    /// caller lacks Roles.Assign entirely, or just the Org-tier ones if they hold Roles.Assign but
    /// aren't Org-tier themselves (RoleScopePolicy.CanAssignRole).</summary>
    private async Task<List<string>> FilterAssignableRoleIdsAsync(List<string> requestedRoleIds, CancellationToken ct)
    {
        if (requestedRoleIds.Count == 0) return [];
        if (!(await authorizationService.AuthorizeAsync(User, "Roles.Assign")).Succeeded) return [];

        var actingHighestTier = await HighestScopeTierAsync(CurrentUserId, ct);
        var requestedTiers = await db.RoleProfiles
            .Where(p => requestedRoleIds.Contains(p.RoleId))
            .ToDictionaryAsync(p => p.RoleId, p => p.MaxScopeTier, ct);

        return requestedRoleIds
            .Where(id => RoleScopePolicy.CanAssignRole(actingHighestTier, requestedTiers.GetValueOrDefault(id, RoleScopeTier.Group)))
            .ToList();
    }

    /// <summary>The most-permissive (numerically lowest) MaxScopeTier across every role the given
    /// user holds — RoleScopeTier.Group (least permissive) if they hold none, or hold only roles with
    /// no RoleProfile row.</summary>
    private async Task<RoleScopeTier> HighestScopeTierAsync(string? userId, CancellationToken ct)
    {
        if (userId is null) return RoleScopeTier.Group;
        var roleIds = await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct);
        if (roleIds.Count == 0) return RoleScopeTier.Group;

        var tiers = await db.RoleProfiles.Where(p => roleIds.Contains(p.RoleId)).Select(p => p.MaxScopeTier).ToListAsync(ct);
        return tiers.Count == 0 ? RoleScopeTier.Group : tiers.Min();
    }

    /// <summary>For each of the given role ids that's both AutoExpires and not already tracked for
    /// this user (a role re-checked after already having an expiry row is left alone, not reset), adds
    /// a RoleAssignmentExpiry defaulting to now + DefaultExpiryMinutes. A role with no RoleProfile row,
    /// or AutoExpires=false, is left with no row at all (never expires) — same default as every other
    /// role assignment in this app before this pass existed.</summary>
    private async Task SeedExpiriesForNewlyAssignedRolesAsync(string userId, List<string> roleIds, CancellationToken ct)
    {
        if (roleIds.Count == 0) return;

        var autoExpiring = await db.RoleProfiles
            .Where(p => roleIds.Contains(p.RoleId) && p.AutoExpires)
            .ToListAsync(ct);
        if (autoExpiring.Count == 0) return;

        var alreadyTracked = await db.RoleAssignmentExpiries
            .Where(e => e.UserId == userId && roleIds.Contains(e.RoleId))
            .Select(e => e.RoleId).ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var profile in autoExpiring.Where(p => !alreadyTracked.Contains(p.RoleId)))
        {
            db.RoleAssignmentExpiries.Add(new RoleAssignmentExpiry
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = profile.RoleId,
                ExpiresAtUtc = now.AddMinutes(profile.DefaultExpiryMinutes ?? 0),
                AssignedAtUtc = now,
                AssignedByUserId = CurrentUserId
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Pushes one assignment's expiry back out to now + its role's own DefaultExpiryMinutes
    /// — the admin-override path for "the default wasn't long enough," simpler than picking an exact
    /// arbitrary timestamp (which would need client-timezone-aware input this compact per-row UI
    /// doesn't have room for). Requires Roles.Assign, same as every other expiry-affecting action.</summary>
    public async Task<IActionResult> OnPostExtendExpiryAsync(Guid expiryId, CancellationToken ct)
    {
        if (!(await authorizationService.AuthorizeAsync(User, "Roles.Assign")).Succeeded) return Forbid();

        var expiry = await db.RoleAssignmentExpiries.FirstOrDefaultAsync(e => e.Id == expiryId, ct);
        if (expiry is null)
        {
            await LoadAsync(ct);
            return Page();
        }

        var defaultMinutes = await db.RoleProfiles.Where(p => p.RoleId == expiry.RoleId)
            .Select(p => p.DefaultExpiryMinutes).FirstOrDefaultAsync(ct);
        expiry.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(defaultMinutes ?? 0);
        await db.SaveChangesAsync(ct);

        await auditService.LogAsync("Role.ExpiryExtended", CurrentUserId, CurrentUserName, RemoteIp,
            $"Extended to {expiry.ExpiresAtUtc:u}", ct);
        SavedMessage = "Expiry extended.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(string userId, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            await LoadAsync(ct);
            return Page();
        }

        var isEnabled = IsEnabled(user);
        if (isEnabled)
        {
            var roles = await userManager.GetRolesAsync(user);
            var isAdmin = await IncludesSuperAdminAsync(roles, ct);
            if (UserManagementPolicy.WouldDisableLastAdministrator(isAdmin, await CountOtherEnabledSuperAdminsAsync(user.Id, ct)))
            {
                ErrorMessage = "Can't disable the last enabled Super Admin.";
                await LoadAsync(ct);
                return Page();
            }

            await userManager.SetLockoutEnabledAsync(user, true);
            await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await auditService.LogAsync("User.Disable", CurrentUserId, CurrentUserName, RemoteIp, user.Email, ct);
            SavedMessage = $"\"{user.Email}\" disabled.";
        }
        else
        {
            await userManager.SetLockoutEndDateAsync(user, null);
            await auditService.LogAsync("User.Enable", CurrentUserId, CurrentUserName, RemoteIp, user.Email, ct);
            SavedMessage = $"\"{user.Email}\" enabled.";
        }

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(string userId, string newPassword, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            await LoadAsync(ct);
            return Page();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
            await LoadAsync(ct);
            return Page();
        }

        await auditService.LogAsync("User.PasswordReset", CurrentUserId, CurrentUserName, RemoteIp, user.Email, ct);
        SavedMessage = $"Password reset for \"{user.Email}\".";
        await LoadAsync(ct);
        return Page();
    }

    private static bool IsEnabled(ApplicationUser user) =>
        user.LockoutEnd is null || user.LockoutEnd < DateTimeOffset.UtcNow;

    private async Task<bool> IncludesSuperAdminAsync(IEnumerable<string> roleNames, CancellationToken ct)
    {
        var names = roleNames as ICollection<string> ?? roleNames.ToList();
        if (names.Count == 0) return false;
        var roleIds = await db.Roles.Where(r => names.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);
        return roleIds.Count > 0 && await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == RoleTags.SuperAdmin, ct);
    }

    private async Task<int> CountOtherEnabledSuperAdminsAsync(string excludingUserId, CancellationToken ct)
    {
        var superAdminRoleId = await db.RoleProfiles
            .Where(p => p.Tag == RoleTags.SuperAdmin).Select(p => p.RoleId).FirstOrDefaultAsync(ct);
        if (superAdminRoleId is null) return 0;

        var adminUserIds = await db.UserRoles.Where(ur => ur.RoleId == superAdminRoleId).Select(ur => ur.UserId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.Users.CountAsync(u =>
            adminUserIds.Contains(u.Id) && u.Id != excludingUserId && (u.LockoutEnd == null || u.LockoutEnd < now), ct);
    }

    private string? CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    private string? CurrentUserName => User.Identity?.Name;
    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(CancellationToken ct)
    {
        Roles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        var users = await db.Users.OrderBy(u => u.Email).ToListAsync(ct);
        var userRoles = await db.UserRoles.ToListAsync(ct);
        var expiries = await db.RoleAssignmentExpiries.ToListAsync(ct);
        var roleNamesById = Roles.ToDictionary(r => r.Id, r => r.Name ?? r.Id);

        Users = users.Select(u => new UserRow(
            u.Id,
            u.Email ?? u.UserName ?? u.Id,
            u.DisplayName,
            IsEnabled(u),
            userRoles.Where(ur => ur.UserId == u.Id).Select(ur => roleNamesById.GetValueOrDefault(ur.RoleId, "?")).OrderBy(n => n).ToList(),
            expiries.Where(e => e.UserId == u.Id)
                .Select(e => new ExpiryRow(e.Id, roleNamesById.GetValueOrDefault(e.RoleId, "?"), e.ExpiresAtUtc))
                .OrderBy(e => e.ExpiresAtUtc).ToList()
        )).ToList();
    }
}

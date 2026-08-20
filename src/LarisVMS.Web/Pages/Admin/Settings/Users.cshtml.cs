using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// Create users, assign role(s), enable/disable, and reset passwords — the only admin surface for any
/// of this. Self-registration is disabled (<see cref="LarisVMS.Web.Middleware.RegistrationDisabledMiddleware"/>),
/// so this page is the sole way a new account gets provisioned. "Disabled" reuses ASP.NET Core
/// Identity's own lockout mechanism (<c>LockoutEnd = DateTimeOffset.MaxValue</c>) rather than a new
/// column — it already fails sign-in at the framework level, and re-enabling is just clearing the
/// same field.
/// </summary>
[Authorize("Settings.Edit")]
public class UsersModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuditService auditService) : PageModel
{
    public record UserRow(string Id, string Email, string? DisplayName, bool IsEnabled, IReadOnlyList<string> RoleNames);

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

        var roleNames = await db.Roles.Where(r => NewRoleIds.Contains(r.Id)).Select(r => r.Name!).ToListAsync(ct);
        if (roleNames.Count > 0) await userManager.AddToRolesAsync(user, roleNames);

        await auditService.LogAsync("User.Create", CurrentUserId, CurrentUserName, RemoteIp, email, ct);
        SavedMessage = $"User \"{email}\" created.";
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
        var targetRoleNames = await db.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => r.Name!).ToListAsync(ct);

        var wasAdmin = currentRoles.Contains(RoleManagementPolicy.ProtectedRoleName);
        var staysAdmin = targetRoleNames.Contains(RoleManagementPolicy.ProtectedRoleName);
        if (UserManagementPolicy.WouldRemoveLastAdministrator(wasAdmin, staysAdmin, await CountOtherEnabledAdministratorsAsync(user.Id, ct)))
        {
            ErrorMessage = "Can't remove the last Administrator's Administrator role.";
            await LoadAsync(ct);
            return Page();
        }

        var toAdd = targetRoleNames.Except(currentRoles).ToList();
        var toRemove = currentRoles.Except(targetRoleNames).ToList();
        if (toAdd.Count > 0) await userManager.AddToRolesAsync(user, toAdd);
        if (toRemove.Count > 0) await userManager.RemoveFromRolesAsync(user, toRemove);

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            var details = AuditDiff.Build(AuditDiff.Of("Roles",
                string.Join(", ", currentRoles.OrderBy(r => r)), string.Join(", ", targetRoleNames.OrderBy(r => r))));
            if (details is not null)
                await auditService.LogAsync("User.Update", CurrentUserId, CurrentUserName, RemoteIp, details, ct);
        }

        SavedMessage = "Saved.";
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
            var isAdmin = roles.Contains(RoleManagementPolicy.ProtectedRoleName);
            if (UserManagementPolicy.WouldDisableLastAdministrator(isAdmin, await CountOtherEnabledAdministratorsAsync(user.Id, ct)))
            {
                ErrorMessage = "Can't disable the last enabled Administrator.";
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

    private async Task<int> CountOtherEnabledAdministratorsAsync(string excludingUserId, CancellationToken ct)
    {
        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == RoleManagementPolicy.ProtectedRoleName, ct);
        if (adminRole is null) return 0;

        var adminUserIds = await db.UserRoles.Where(ur => ur.RoleId == adminRole.Id).Select(ur => ur.UserId).ToListAsync(ct);
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
        var roleNamesById = Roles.ToDictionary(r => r.Id, r => r.Name ?? r.Id);

        Users = users.Select(u => new UserRow(
            u.Id,
            u.Email ?? u.UserName ?? u.Id,
            u.DisplayName,
            IsEnabled(u),
            userRoles.Where(ur => ur.UserId == u.Id).Select(ur => roleNamesById.GetValueOrDefault(ur.RoleId, "?")).OrderBy(n => n).ToList()
        )).ToList();
    }
}

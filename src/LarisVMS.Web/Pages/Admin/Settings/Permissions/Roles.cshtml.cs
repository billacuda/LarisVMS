using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings.Permissions;

/// <summary>
/// Create/rename/delete roles and edit each one's scope tier, auto-expiry default, PTZ priority/
/// lockout, and per-role session lifetime (relocated here from the retired Security tab — it's
/// genuinely role configuration). The Resource×Action permission grid itself lives on the sibling
/// Matrix sub-tab now, not here. The role tagged SUPER (see RoleManagementPolicy) can't be renamed,
/// deleted, or have its profile edited — it always holds every permission and is always org-scoped.
/// </summary>
[Authorize("Settings.Edit")]
public class RolesModel(
    ApplicationDbContext db,
    RoleManager<IdentityRole> roleManager,
    ISettingsResolver settings,
    IAuditService auditService) : PageModel
{
    public record RoleRow(
        string Id, string Name, string? Tag, bool IsProtected, bool IsSystemRole, int UserCount,
        RoleScopeTier MaxScopeTier, bool AutoExpires, int? DefaultExpiryMinutes,
        int? PtzPriorityLevel, int? PtzLockoutSeconds, int SessionLifetimeHours);

    public List<RoleRow> Roles { get; set; } = [];

    [BindProperty] public string NewRoleName { get; set; } = string.Empty;

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        var name = NewRoleName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorMessage = "Enter a role name.";
            await LoadAsync(ct);
            return Page();
        }
        if (await roleManager.RoleExistsAsync(name))
        {
            ErrorMessage = $"A role named \"{name}\" already exists.";
            await LoadAsync(ct);
            return Page();
        }

        var result = await roleManager.CreateAsync(new IdentityRole(name));
        if (!result.Succeeded)
        {
            ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
            await LoadAsync(ct);
            return Page();
        }

        await auditService.LogAsync("Role.Create", CurrentUserId, CurrentUserName, RemoteIp, name, ct);
        SavedMessage = $"Role \"{name}\" created.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string roleId, string name,
        RoleScopeTier maxScopeTier, bool autoExpires, int? defaultExpiryMinutes,
        int? ptzPriorityLevel, int? ptzLockoutSeconds, int sessionLifetimeHours,
        CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(roleId);
        if (role is null)
        {
            ErrorMessage = "That role no longer exists.";
            await LoadAsync(ct);
            return Page();
        }

        var profile = await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == role.Id, ct);
        var isProtected = RoleManagementPolicy.IsProtected(profile?.Tag);
        var fields = new List<AuditDiff.Field>();

        var newName = (name ?? string.Empty).Trim();
        if (!string.Equals(role.Name, newName, StringComparison.Ordinal))
        {
            if (!RoleManagementPolicy.CanRename(profile?.Tag))
            {
                ErrorMessage = "The Super Admin role can't be renamed.";
                await LoadAsync(ct);
                return Page();
            }
            if (string.IsNullOrEmpty(newName))
            {
                ErrorMessage = "Role name can't be blank.";
                await LoadAsync(ct);
                return Page();
            }
            if (await roleManager.RoleExistsAsync(newName))
            {
                ErrorMessage = $"A role named \"{newName}\" already exists.";
                await LoadAsync(ct);
                return Page();
            }
            fields.Add(AuditDiff.Of("Name", role.Name, newName));
            role.Name = newName;
            await roleManager.UpdateAsync(role);
        }

        // Scope tier / auto-expiry / PTZ aren't editable for the protected role — it's always
        // Org-scoped, never expires, and its PTZ priority is fixed at the top of the range.
        if (!isProtected)
        {
            var isNewProfile = profile is null;
            profile ??= new RoleProfile { RoleId = role.Id, IsSystemRole = false };

            var clampedExpiry = autoExpires ? Math.Max(1, defaultExpiryMinutes ?? 0) : (int?)null;
            var clampedPriority = ptzPriorityLevel is { } pr ? Math.Clamp(pr, 1, 9999) : (int?)null;
            var clampedLockout = clampedPriority is not null ? Math.Max(0, ptzLockoutSeconds ?? 0) : (int?)null;

            if (!isNewProfile)
            {
                if (profile.MaxScopeTier != maxScopeTier)
                    fields.Add(AuditDiff.Of("Max scope tier", profile.MaxScopeTier.ToString(), maxScopeTier.ToString()));
                if (profile.AutoExpires != autoExpires || profile.DefaultExpiryMinutes != clampedExpiry)
                    fields.Add(AuditDiff.Of("Auto-expiry",
                        profile.AutoExpires ? $"{profile.DefaultExpiryMinutes}m" : "never",
                        autoExpires ? $"{clampedExpiry}m" : "never"));
                if (profile.PtzPriorityLevel != clampedPriority || profile.PtzLockoutSeconds != clampedLockout)
                    fields.Add(AuditDiff.Of("PTZ priority/lockout",
                        profile.PtzPriorityLevel is { } op ? $"{op}/{profile.PtzLockoutSeconds}s" : "none",
                        clampedPriority is { } np ? $"{np}/{clampedLockout}s" : "none"));
            }

            profile.MaxScopeTier = maxScopeTier;
            profile.AutoExpires = autoExpires;
            profile.DefaultExpiryMinutes = clampedExpiry;
            profile.PtzPriorityLevel = clampedPriority;
            profile.PtzLockoutSeconds = clampedLockout;

            if (isNewProfile) db.RoleProfiles.Add(profile);
            await db.SaveChangesAsync(ct);
        }

        var oldHours = await settings.GetAsync(SessionLifetimePolicy.SettingKey(role.Id), SessionLifetimePolicy.DefaultHours, ct: ct);
        var clampedHours = Math.Max(0, sessionLifetimeHours);
        if (clampedHours != oldHours)
        {
            await settings.SetGlobalAsync(SessionLifetimePolicy.SettingKey(role.Id), clampedHours.ToString(), User.Identity?.Name);
            fields.Add(AuditDiff.Of("Session lifetime (hours)", oldHours.ToString(), clampedHours.ToString()));
        }

        var details = AuditDiff.Build([.. fields]);
        if (details is not null)
            await auditService.LogAsync("Role.Update", CurrentUserId, CurrentUserName, RemoteIp, details, ct);

        SavedMessage = "Saved.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string roleId, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(roleId);
        if (role is null)
        {
            await LoadAsync(ct);
            return Page();
        }

        var profile = await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == role.Id, ct);
        var userCount = await db.UserRoles.CountAsync(ur => ur.RoleId == role.Id, ct);
        if (!RoleManagementPolicy.CanDelete(profile?.Tag, userCount))
        {
            ErrorMessage = RoleManagementPolicy.IsProtected(profile?.Tag)
                ? "The Super Admin role can't be deleted."
                : $"\"{role.Name}\" still has {userCount} user(s) assigned — move them to another role first.";
            await LoadAsync(ct);
            return Page();
        }

        db.Permissions.RemoveRange(db.Permissions.Where(p => p.RoleId == role.Id));
        db.CameraAccesses.RemoveRange(db.CameraAccesses.Where(a =>
            a.PrincipalType == CameraAccessPrincipalType.Role && a.PrincipalId == role.Id));
        db.RoleAssignmentExpiries.RemoveRange(db.RoleAssignmentExpiries.Where(a => a.RoleId == role.Id));
        if (profile is not null) db.RoleProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);

        var name = role.Name;
        await roleManager.DeleteAsync(role);
        await auditService.LogAsync("Role.Delete", CurrentUserId, CurrentUserName, RemoteIp, name, ct);

        SavedMessage = $"Role \"{name}\" deleted.";
        await LoadAsync(ct);
        return Page();
    }

    private string? CurrentUserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    private string? CurrentUserName => User.Identity?.Name;
    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(CancellationToken ct)
    {
        var roles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        var profiles = await db.RoleProfiles.ToDictionaryAsync(p => p.RoleId, ct);
        var userCounts = await db.UserRoles
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        Roles = [];
        foreach (var r in roles)
        {
            var profile = profiles.GetValueOrDefault(r.Id);
            var hours = await settings.GetAsync(SessionLifetimePolicy.SettingKey(r.Id), SessionLifetimePolicy.DefaultHours, ct: ct);
            Roles.Add(new RoleRow(
                r.Id,
                r.Name ?? r.Id,
                profile?.Tag,
                RoleManagementPolicy.IsProtected(profile?.Tag),
                profile?.IsSystemRole ?? false,
                userCounts.FirstOrDefault(c => c.RoleId == r.Id)?.Count ?? 0,
                profile?.MaxScopeTier ?? RoleScopeTier.Group,
                profile?.AutoExpires ?? false,
                profile?.DefaultExpiryMinutes,
                profile?.PtzPriorityLevel,
                profile?.PtzLockoutSeconds,
                hours));
        }
    }
}

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

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// Create/rename/delete roles and edit each role's Permission matrix (Resource×Action rows —
/// PermissionCatalog is the fixed, known set of pairs this app actually checks anywhere). Replaces
/// the one-time, admin-surface-less SetupServiceViewerPermissionSeedingTests seed as the only way
/// permissions ever got set. Administrator is protected from rename/delete and its matrix isn't
/// editable here — PermissionService bypasses the Permission table for it entirely by name, so
/// checkboxes here would silently do nothing.
/// </summary>
[Authorize("Settings.Edit")]
public class RolesModel(
    ApplicationDbContext db,
    RoleManager<IdentityRole> roleManager,
    IAuditService auditService) : PageModel
{
    public record RoleRow(string Id, string Name, bool IsProtected, int UserCount, HashSet<(string Resource, string Action)> Granted);

    public List<RoleRow> Roles { get; set; } = [];
    public IReadOnlyList<PermissionCatalog.Entry> Catalog => PermissionCatalog.All;

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

    public async Task<IActionResult> OnPostSaveAsync(string roleId, string name, [FromForm] List<string> permissions, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(roleId);
        if (role is null)
        {
            ErrorMessage = "That role no longer exists.";
            await LoadAsync(ct);
            return Page();
        }

        var newName = (name ?? string.Empty).Trim();
        var fields = new List<AuditDiff.Field>();
        var isProtected = string.Equals(role.Name, RoleManagementPolicy.ProtectedRoleName, StringComparison.Ordinal);

        if (!string.Equals(role.Name, newName, StringComparison.Ordinal))
        {
            if (!RoleManagementPolicy.CanRename(role.Name))
            {
                ErrorMessage = $"The {RoleManagementPolicy.ProtectedRoleName} role can't be renamed.";
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

        // Administrator implicitly holds every permission regardless of table rows (see
        // PermissionService) -- its matrix isn't posted from the view, so there's nothing to diff.
        if (!isProtected)
        {
            var existingRows = await db.Permissions.Where(p => p.RoleId == role.Id).ToListAsync(ct);
            var existingPairs = existingRows.Select(p => (p.Resource, p.Action));
            var submittedPairs = (permissions ?? [])
                .Select(p => p.Split('|', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => (Resource: parts[0], Action: parts[1]))
                .Where(pair => Catalog.Any(e => e.Resource == pair.Resource && e.Action == pair.Action))
                .ToList();

            var (toAdd, toRemove) = RoleManagementPolicy.DiffPermissions(existingPairs, submittedPairs);

            foreach (var (resource, action) in toRemove)
            {
                var row = existingRows.First(p => p.Resource == resource && p.Action == action);
                db.Permissions.Remove(row);
            }
            foreach (var (resource, action) in toAdd)
            {
                db.Permissions.Add(new Permission
                {
                    Id = Guid.NewGuid(), RoleId = role.Id, Resource = resource, Action = action, IsSystemPermission = false
                });
            }
            if (toAdd.Count > 0 || toRemove.Count > 0)
            {
                var beforeList = string.Join(", ", existingPairs.Select(p => $"{p.Resource}.{p.Action}").OrderBy(s => s));
                var afterList = string.Join(", ", submittedPairs.Select(p => $"{p.Resource}.{p.Action}").OrderBy(s => s));
                fields.Add(AuditDiff.Of("Permissions", beforeList, afterList));
            }
            await db.SaveChangesAsync(ct);
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

        var userCount = await db.UserRoles.CountAsync(ur => ur.RoleId == role.Id, ct);
        if (!RoleManagementPolicy.CanDelete(role.Name, userCount))
        {
            ErrorMessage = string.Equals(role.Name, RoleManagementPolicy.ProtectedRoleName, StringComparison.Ordinal)
                ? $"The {RoleManagementPolicy.ProtectedRoleName} role can't be deleted."
                : $"\"{role.Name}\" still has {userCount} user(s) assigned — move them to another role first.";
            await LoadAsync(ct);
            return Page();
        }

        db.Permissions.RemoveRange(db.Permissions.Where(p => p.RoleId == role.Id));
        db.CameraAccesses.RemoveRange(db.CameraAccesses.Where(a =>
            a.PrincipalType == CameraAccessPrincipalType.Role && a.PrincipalId == role.Id));
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
        var permissionRows = await db.Permissions.ToListAsync(ct);
        var userCounts = await db.UserRoles
            .GroupBy(ur => ur.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        Roles = roles.Select(r => new RoleRow(
            r.Id,
            r.Name ?? r.Id,
            string.Equals(r.Name, RoleManagementPolicy.ProtectedRoleName, StringComparison.Ordinal),
            userCounts.FirstOrDefault(c => c.RoleId == r.Id)?.Count ?? 0,
            permissionRows.Where(p => p.RoleId == r.Id).Select(p => (p.Resource, p.Action)).ToHashSet()
        )).ToList();
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings.Permissions;

/// <summary>
/// Role × permission grid — every role (built-in and custom) as a column, every
/// PermissionCatalog entry as a row. Replaces the old Roles page's one-role-at-a-time checkbox
/// table. Still binary full/none this pass (matching today's Permission table exactly); becomes a
/// true full/limited/none matrix once the roles/permissions overhaul's Pass 3 extends the catalog
/// and adds scope-aware "limited" semantics. Each role column saves independently via its own
/// (visually invisible) &lt;form&gt;, referenced by its checkboxes' HTML `form` attribute rather than
/// nested markup, so one wide table can hold every role's own submit without nested &lt;form&gt; tags.
/// </summary>
[Authorize("Settings.Edit")]
public class MatrixModel(ApplicationDbContext db, IAuditService auditService) : PageModel
{
    public record RoleColumn(string Id, string Name, bool IsProtected);

    public IReadOnlyList<PermissionCatalog.Entry> Catalog => PermissionCatalog.All;
    public List<RoleColumn> RoleColumns { get; set; } = [];
    public Dictionary<string, HashSet<(string Resource, string Action)>> GrantedByRoleId { get; set; } = [];

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostSaveAsync(string roleId, [FromForm] List<string> permissions, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            ErrorMessage = "That role no longer exists.";
            await LoadAsync(ct);
            return Page();
        }

        var profile = await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == roleId, ct);
        if (RoleManagementPolicy.IsProtected(profile?.Tag))
        {
            ErrorMessage = "The Super Admin role always holds every permission and its matrix can't be edited here.";
            await LoadAsync(ct);
            return Page();
        }

        var existingRows = await db.Permissions.Where(p => p.RoleId == roleId).ToListAsync(ct);
        var existingPairs = existingRows.Select(p => (p.Resource, p.Action));
        var submittedPairs = (permissions ?? [])
            .Select(p => p.Split('|', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Resource: parts[0], Action: parts[1]))
            .Where(pair => Catalog.Any(e => e.Resource == pair.Resource && e.Action == pair.Action))
            .ToList();

        var (toAdd, toRemove) = RoleManagementPolicy.DiffPermissions(existingPairs, submittedPairs);
        foreach (var (resource, action) in toRemove)
            db.Permissions.Remove(existingRows.First(p => p.Resource == resource && p.Action == action));
        foreach (var (resource, action) in toAdd)
            db.Permissions.Add(new Permission
            {
                Id = Guid.NewGuid(), RoleId = roleId, Resource = resource, Action = action, IsSystemPermission = false
            });

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            var beforeList = string.Join(", ", existingPairs.Select(p => $"{p.Resource}.{p.Action}").OrderBy(s => s));
            var afterList = string.Join(", ", submittedPairs.Select(p => $"{p.Resource}.{p.Action}").OrderBy(s => s));
            await db.SaveChangesAsync(ct);

            var details = AuditDiff.Build(AuditDiff.Of($"{role.Name} permissions", beforeList, afterList));
            if (details is not null)
                await auditService.LogAsync("Role.Update", CurrentUserId, CurrentUserName, RemoteIp, details, ct);
        }

        SavedMessage = $"Saved \"{role.Name}\".";
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
        var permissionRows = await db.Permissions.ToListAsync(ct);

        RoleColumns = roles
            .Select(r => new RoleColumn(r.Id, r.Name ?? r.Id, RoleManagementPolicy.IsProtected(profiles.GetValueOrDefault(r.Id)?.Tag)))
            .ToList();

        GrantedByRoleId = roles.ToDictionary(
            r => r.Id,
            r => permissionRows.Where(p => p.RoleId == r.Id).Select(p => (p.Resource, p.Action)).ToHashSet());
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// Grants a role narrowed access to specific cameras or camera groups — the admin surface for the
/// CameraAccess table M1 defined and nothing wrote to or enforced until M14. Role-scoped only: this
/// app has no user-management page yet to pick an individual user from (RBAC's per-user grant path,
/// CameraAccessPrincipalType.User, is schema-ready and enforced identically by
/// CameraAccessService — it just has no admin surface here until that page exists).
///
/// A role with zero rows here is unrestricted (sees every camera) — see
/// ICameraAccessService.GetAccessibleCameraIdsAsync's own doc comment for why that default matters:
/// every existing deployment's every existing role has zero rows today, so adding this page changes
/// nothing until an admin deliberately adds a grant.
/// </summary>
[Authorize("Settings.Edit")]
public class CameraAccessModel(ApplicationDbContext db, IAuditService auditService) : PageModel
{
    public record GrantRow(Guid Id, string RoleName, string ScopeDescription, string ActionsDescription);

    [BindProperty] public string RoleId { get; set; } = string.Empty;
    [BindProperty] public CameraAccessScopeType ScopeType { get; set; } = CameraAccessScopeType.All;
    [BindProperty] public Guid? ScopeId { get; set; }
    [BindProperty] public List<string> Actions { get; set; } = [];

    public List<GrantRow> Grants { get; set; } = [];
    public List<Microsoft.AspNetCore.Identity.IdentityRole> Roles { get; set; } = [];
    public List<CameraGroup> Groups { get; set; } = [];
    public List<Camera> Cameras { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? SavedMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        var role = Roles.FirstOrDefault(r => r.Id == RoleId);
        if (role is null) { ErrorMessage = "Choose a role."; return Page(); }

        if (ScopeType != CameraAccessScopeType.All && ScopeId is null)
        {
            ErrorMessage = ScopeType == CameraAccessScopeType.Group
                ? "Choose a camera group." : "Choose a camera.";
            return Page();
        }

        var actions = ParseActions(Actions);
        if (actions == CameraAccessActions.None) { ErrorMessage = "Choose at least one action to grant."; return Page(); }

        db.CameraAccesses.Add(new CameraAccess
        {
            Id = Guid.NewGuid(),
            PrincipalType = CameraAccessPrincipalType.Role,
            PrincipalId = role.Id,
            ScopeType = ScopeType,
            ScopeId = ScopeType == CameraAccessScopeType.All ? null : ScopeId,
            Actions = actions
        });
        await db.SaveChangesAsync(ct);

        var scopeText = ScopeType switch
        {
            CameraAccessScopeType.All => "every camera",
            CameraAccessScopeType.Group => Groups.FirstOrDefault(g => g.Id == ScopeId)?.Name ?? ScopeId.ToString(),
            _ => Cameras.FirstOrDefault(c => c.Id == ScopeId)?.Name ?? ScopeId.ToString()
        };
        await auditService.LogAsync("CameraAccess.Grant",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"{role.Name}: {actions} on {scopeText}", ct);

        SavedMessage = "Grant added.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken ct)
    {
        var row = await db.CameraAccesses.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is not null)
        {
            var roleName = (await db.Roles.FirstOrDefaultAsync(r => r.Id == row.PrincipalId, ct))?.Name ?? row.PrincipalId;
            db.CameraAccesses.Remove(row);
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("CameraAccess.Revoke",
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), $"{roleName}: {row.Actions} on scope {row.ScopeType}", ct);
        }

        SavedMessage = "Grant removed.";
        await LoadAsync(ct);
        return Page();
    }

    private static CameraAccessActions ParseActions(List<string> selected)
    {
        var result = CameraAccessActions.None;
        foreach (var s in selected)
            if (Enum.TryParse<CameraAccessActions>(s, out var a)) result |= a;
        return result;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Roles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        Groups = (await db.CameraGroups.OrderBy(g => g.MaterializedPath).ToListAsync(ct));
        Cameras = await db.Cameras.OrderBy(c => c.Name).ToListAsync(ct);

        var rows = await db.CameraAccesses
            .Where(a => a.PrincipalType == CameraAccessPrincipalType.Role)
            .ToListAsync(ct);

        Grants = rows.Select(r => new GrantRow(
            r.Id,
            Roles.FirstOrDefault(role => role.Id == r.PrincipalId)?.Name ?? r.PrincipalId,
            r.ScopeType switch
            {
                CameraAccessScopeType.All => "Every camera",
                CameraAccessScopeType.Group => Groups.FirstOrDefault(g => g.Id == r.ScopeId)?.Name ?? "(deleted group)",
                _ => Cameras.FirstOrDefault(c => c.Id == r.ScopeId)?.Name ?? "(deleted camera)"
            },
            r.Actions.ToString()))
            .OrderBy(g => g.RoleName).ThenBy(g => g.ScopeDescription).ToList();
    }
}

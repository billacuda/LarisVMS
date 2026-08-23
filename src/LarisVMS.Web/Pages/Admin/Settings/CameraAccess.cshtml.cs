using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

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
    public record GrantRow(Guid Id, string RoleId, string RoleName, CameraAccessScopeType ScopeType, Guid? ScopeId,
        string ScopeDescription, CameraAccessActions Actions, string ActionsDescription);

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

        var maxScopeTier = (await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == role.Id, ct))
            ?.MaxScopeTier ?? RoleScopeTier.Group;
        var targetIsSite = ScopeType == CameraAccessScopeType.Group &&
            Groups.FirstOrDefault(g => g.Id == ScopeId)?.IsSite == true;
        if (!RoleScopePolicy.CanGrant(maxScopeTier, ScopeType, targetIsSite))
        {
            ErrorMessage = RoleScopePolicy.DenialReason(maxScopeTier, ScopeType);
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

        var scopeText = ScopeText(ScopeType, ScopeId);
        await auditService.LogAsync("CameraAccess.Grant",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"{role.Name}: {actions} on {scopeText}", ct);

        SavedMessage = "Grant added.";
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Changes an existing grant's scope and/or actions in place — same validation as Add
    /// (scope-tier cap, at least one action), but the role a grant belongs to isn't editable here:
    /// moving a grant to a different role is exactly what Remove + Add already does, and keeping Edit
    /// to "narrow or widen what this role already has" keeps the form (and its scope-tier checks)
    /// identical in shape to Add's.</summary>
    public async Task<IActionResult> OnPostEditAsync(Guid id, CameraAccessScopeType scopeType, Guid? scopeId,
        [FromForm] List<string> actions, CancellationToken ct)
    {
        await LoadAsync(ct);

        var row = await db.CameraAccesses.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is null) { ErrorMessage = "That grant no longer exists."; return Page(); }

        var role = Roles.FirstOrDefault(r => r.Id == row.PrincipalId);
        var roleName = role?.Name ?? row.PrincipalId;

        if (scopeType != CameraAccessScopeType.All && scopeId is null)
        {
            ErrorMessage = scopeType == CameraAccessScopeType.Group
                ? "Choose a camera group." : "Choose a camera.";
            return Page();
        }

        var maxScopeTier = (await db.RoleProfiles.FirstOrDefaultAsync(p => p.RoleId == row.PrincipalId, ct))
            ?.MaxScopeTier ?? RoleScopeTier.Group;
        var targetIsSite = scopeType == CameraAccessScopeType.Group &&
            Groups.FirstOrDefault(g => g.Id == scopeId)?.IsSite == true;
        if (!RoleScopePolicy.CanGrant(maxScopeTier, scopeType, targetIsSite))
        {
            ErrorMessage = RoleScopePolicy.DenialReason(maxScopeTier, scopeType);
            return Page();
        }

        var parsedActions = ParseActions(actions);
        if (parsedActions == CameraAccessActions.None) { ErrorMessage = "Choose at least one action to grant."; return Page(); }

        var oldScopeText = ScopeText(row.ScopeType, row.ScopeId);
        var oldActions = row.Actions;
        var newScopeText = ScopeText(scopeType, scopeId);

        row.ScopeType = scopeType;
        row.ScopeId = scopeType == CameraAccessScopeType.All ? null : scopeId;
        row.Actions = parsedActions;
        await db.SaveChangesAsync(ct);

        var details = AuditDiff.Build(
            AuditDiff.Of("Scope", oldScopeText, newScopeText),
            AuditDiff.Of("Actions", oldActions.ToString(), parsedActions.ToString()));
        await auditService.LogAsync("CameraAccess.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            details is null ? $"{roleName}: {newScopeText}" : $"{roleName}: {details}", ct);

        SavedMessage = "Grant updated.";
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

    /// <summary>Human-readable scope description, e.g. for the grants table and audit log entries —
    /// "every camera" for All, the group/camera name (or a "(deleted ...)" fallback) otherwise.</summary>
    private string ScopeText(CameraAccessScopeType scopeType, Guid? scopeId) => scopeType switch
    {
        CameraAccessScopeType.All => "every camera",
        CameraAccessScopeType.Group => Groups.FirstOrDefault(g => g.Id == scopeId)?.Name ?? "(deleted group)",
        _ => Cameras.FirstOrDefault(c => c.Id == scopeId)?.Name ?? "(deleted camera)"
    };

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
            r.PrincipalId,
            Roles.FirstOrDefault(role => role.Id == r.PrincipalId)?.Name ?? r.PrincipalId,
            r.ScopeType,
            r.ScopeId,
            ScopeText(r.ScopeType, r.ScopeId),
            r.Actions,
            r.Actions.ToString()))
            .OrderBy(g => g.RoleName).ThenBy(g => g.ScopeDescription).ToList();
    }
}

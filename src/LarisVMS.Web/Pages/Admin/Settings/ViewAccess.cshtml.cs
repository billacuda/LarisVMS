using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// Which roles may open which other roles' views (ViewTreeGrant). Each role's members see their own
/// role's view folder; here an admin lets a whole role open another role's folder too, open-only.
/// To change another role's views, a person is added to that role instead. Roles can't share their
/// own views, so nobody inside a role can show them to a role that shouldn't see them.
/// </summary>
[Authorize("Settings.Edit")]
public class ViewAccessModel(IViewAccessService viewAccess, ApplicationDbContext db, IAuditService audit) : PageModel
{
    public record GrantRow(string OwnerRoleId, string OwnerRoleName, string GrantedRoleId, string GrantedRoleName);
    public record RoleOption(string Id, string Name);

    public List<GrantRow> Grants { get; set; } = [];
    public List<RoleOption> Roles { get; set; } = [];
    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAddAsync(string grantedRoleId, string ownerRoleId)
    {
        if (string.IsNullOrEmpty(grantedRoleId) || string.IsNullOrEmpty(ownerRoleId) || grantedRoleId == ownerRoleId)
        {
            ErrorMessage = Loc.T("settings.viewAccess.pickTwoDifferentRoles");
            await LoadAsync();
            return Page();
        }
        try
        {
            await viewAccess.GrantAsync(ownerRoleId, grantedRoleId, User.Identity?.Name);
        }
        catch (ViewTreeException ex)
        {
            ErrorMessage = ex.Message;
            await LoadAsync();
            return Page();
        }
        await AuditAsync("ViewAccess.Granted", grantedRoleId, ownerRoleId);
        await LoadAsync();
        SavedMessage = Loc.T("common.saved");
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string grantedRoleId, string ownerRoleId)
    {
        await viewAccess.RevokeAsync(ownerRoleId, grantedRoleId);
        await AuditAsync("ViewAccess.Revoked", grantedRoleId, ownerRoleId);
        await LoadAsync();
        SavedMessage = Loc.T("common.saved");
        return Page();
    }

    private async Task AuditAsync(string action, string grantedRoleId, string ownerRoleId)
    {
        var names = await db.Roles.Where(r => r.Id == grantedRoleId || r.Id == ownerRoleId).ToDictionaryAsync(r => r.Id, r => r.Name);
        await audit.LogAsync(action, User.FindFirst(ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            $"{names.GetValueOrDefault(grantedRoleId) ?? grantedRoleId} → views of {names.GetValueOrDefault(ownerRoleId) ?? ownerRoleId}");
    }

    private async Task LoadAsync()
    {
        Roles = (await db.Roles.Select(r => new { r.Id, r.Name }).ToListAsync())
            .Select(r => new RoleOption(r.Id, r.Name ?? r.Id))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var names = Roles.ToDictionary(r => r.Id, r => r.Name);
        Grants = (await viewAccess.ListAsync())
            .Where(g => names.ContainsKey(g.OwnerRoleId) && names.ContainsKey(g.GrantedRoleId))
            .Select(g => new GrantRow(g.OwnerRoleId, names[g.OwnerRoleId], g.GrantedRoleId, names[g.GrantedRoleId]))
            .OrderBy(g => g.GrantedRoleName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.OwnerRoleName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

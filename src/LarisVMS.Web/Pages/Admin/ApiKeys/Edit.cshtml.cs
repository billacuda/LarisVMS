using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Admin.ApiKeys;

/// <summary>Creates a key, or edits an existing key's name and limits. A key's role, expiry, and value
/// can't change: those need a new key. Index's Revoke is the only other lifecycle action.</summary>
[Authorize("ApiKeys.Edit")]
public class EditModel(ApplicationDbContext db, IApiKeyService apiKeyService, IAuditService auditService,
    IPermissionService permissions) : PageModel
{
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string RoleId { get; set; } = string.Empty;
    /// <summary>Days until the key stops working; 0 = never.</summary>
    [BindProperty] public int ExpiresInDays { get; set; } = 365;
    /// <summary>On narrows the key to <see cref="SelectedPermissions"/>; off gives it its whole role.</summary>
    [BindProperty] public bool LimitPermissions { get; set; }
    /// <summary>Values are "Resource.Action".</summary>
    [BindProperty] public List<string> SelectedPermissions { get; set; } = [];

    public static readonly int[] ExpiryChoices = [30, 90, 365, 0];

    public List<IdentityRole> Roles { get; set; } = [];
    /// <summary>The key being edited; null when creating one.</summary>
    public ApiKey? Existing { get; set; }
    public string? ExistingRoleName { get; set; }
    /// <summary>The API permissions offered, with their catalog labels.</summary>
    public List<(string Value, string Label)> ApiPermissions { get; set; } = [];
    /// <summary>Which API permissions each role has, so the page only offers what a role can grant.</summary>
    public Dictionary<string, List<string>> PermissionsByRole { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        if (id is { } keyId)
        {
            if (await apiKeyService.GetAsync(keyId, ct) is not { } found || found.Key.RevokedAtUtc is not null) return NotFound();
            Name = found.Key.Name;
            LimitPermissions = found.Limits is not null;
            SelectedPermissions = (found.Limits ?? new HashSet<(string, string)>()).Select(p => ApiKeyScope.Format(p.Resource, p.Action)).ToList();
        }
        await LoadAsync(id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        if (id is not null && Existing is null) return NotFound();
        var roleId = Existing?.RoleId ?? RoleId;

        Name = Name.Trim();
        if (string.IsNullOrEmpty(Name)) return Fail(Loc.T("admin.apiKeys.edit.enterANameForThis"));
        if (string.IsNullOrEmpty(roleId) || !await db.Roles.AnyAsync(r => r.Id == roleId, ct))
            return Fail(Loc.T("admin.apiKeys.edit.chooseARole2"));
        // A key acts with its role's access, so it can't reach further than a role you could assign.
        if (!(await LarisVMS.Web.Services.AdminScope.ForAsync(db, permissions, User, ct)).CanBindRole(roleId))
            return Fail(Loc.T("admin.apiKeys.edit.youCantCreateAKey"));

        // Checked here as well as hidden on the page: a limit the role doesn't have grants nothing, but
        // it would make the key look more capable than it is.
        List<(string Resource, string Action)>? limits = null;
        if (LimitPermissions)
        {
            var allowed = PermissionsByRole.GetValueOrDefault(roleId) ?? [];
            if (SelectedPermissions.Except(allowed).Any()) return Fail(Loc.T("admin.apiKeys.edit.roleDoesntHaveThatPermission"));
            limits = ApiKeyScope.ApiPermissions.Where(p => SelectedPermissions.Contains(ApiKeyScope.Format(p.Resource, p.Action))).ToList();
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var limitNote = limits is null ? "" : $", limited to {(limits.Count == 0 ? "nothing" : string.Join(", ", SelectedPermissions))}";

        if (Existing is not null)
        {
            await apiKeyService.UpdateAsync(Existing.Id, Name, limits, ct);
            await auditService.LogAsync("ApiKey.Update", userId, User.Identity?.Name, ip, Name + limitNote, ct);
            return RedirectToPage("Index");
        }

        DateTime? expires = ExpiryChoices.Contains(ExpiresInDays) && ExpiresInDays > 0
            ? DateTime.UtcNow.AddDays(ExpiresInDays) : null;
        var (key, rawValue) = await apiKeyService.GenerateAsync(Name, roleId, userId, ct, expires, limits);
        await auditService.LogAsync("ApiKey.Create", userId, User.Identity?.Name, ip,
            (expires is { } e ? $"{key.Name} (expires {e:yyyy-MM-dd} UTC)" : key.Name) + limitNote, ct);

        TempData["NewRawKey"] = rawValue;
        TempData["NewKeyName"] = key.Name;
        return RedirectToPage("Index");
    }

    private PageResult Fail(string message)
    {
        ErrorMessage = message;
        return Page();
    }

    private async Task LoadAsync(Guid? id, CancellationToken ct)
    {
        var scope = await LarisVMS.Web.Services.AdminScope.ForAsync(db, permissions, User, ct);
        var allRoles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        Roles = allRoles.Where(r => scope.CanBindRole(r.Id)).ToList();

        if (id is { } keyId && await apiKeyService.GetAsync(keyId, ct) is { } found && found.Key.RevokedAtUtc is null)
        {
            Existing = found.Key;
            ExistingRoleName = allRoles.FirstOrDefault(r => r.Id == found.Key.RoleId)?.Name;
        }

        ApiPermissions = ApiKeyScope.ApiPermissions
            .Select(p => (ApiKeyScope.Format(p.Resource, p.Action),
                PermissionCatalog.All.FirstOrDefault(e => e.Resource == p.Resource && e.Action == p.Action).Label
                    ?? ApiKeyScope.Format(p.Resource, p.Action)))
            .ToList();

        PermissionsByRole = [];
        foreach (var role in allRoles.Where(r => r.Name is not null))
        {
            var held = new List<string>();
            foreach (var (resource, action) in ApiKeyScope.ApiPermissions)
                if (await permissions.HasPermissionForRoleNameAsync(role.Name!, resource, action, ct))
                    held.Add(ApiKeyScope.Format(resource, action));
            PermissionsByRole[role.Id] = held;
        }
    }
}

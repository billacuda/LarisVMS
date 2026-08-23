using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Admin.ApiKeys;

/// <summary>Create-only — an ApiKey has nothing worth re-editing after creation (see ApiKey's own doc
/// comment); Index's Revoke is the only other lifecycle action.</summary>
[Authorize("ApiKeys.Edit")]
public class EditModel(ApplicationDbContext db, IApiKeyService apiKeyService, IAuditService auditService) : PageModel
{
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string RoleId { get; set; } = string.Empty;

    public List<IdentityRole> Roles { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadRolesAsync(ct);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        Name = Name.Trim();
        if (string.IsNullOrEmpty(Name))
        {
            ErrorMessage = "Enter a name for this key.";
            await LoadRolesAsync(ct);
            return Page();
        }
        if (string.IsNullOrEmpty(RoleId) || !await db.Roles.AnyAsync(r => r.Id == RoleId, ct))
        {
            ErrorMessage = "Choose a role.";
            await LoadRolesAsync(ct);
            return Page();
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var (key, rawValue) = await apiKeyService.GenerateAsync(Name, RoleId, userId, ct);
        await auditService.LogAsync("ApiKey.Create", userId, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), key.Name, ct);

        TempData["NewRawKey"] = rawValue;
        TempData["NewKeyName"] = key.Name;
        return RedirectToPage("Index");
    }

    private async Task LoadRolesAsync(CancellationToken ct)
        => Roles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
}

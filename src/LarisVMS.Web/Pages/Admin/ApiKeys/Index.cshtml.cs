using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Admin.ApiKeys;

[Authorize("ApiKeys.Edit")]
public class IndexModel(ApplicationDbContext db, IApiKeyService apiKeyService, IAuditService auditService) : PageModel
{
    public record KeyRow(Guid Id, string Name, string RoleName, string KeyPrefix,
        DateTime CreatedAtUtc, DateTime? LastUsedAtUtc, bool Revoked);

    public List<KeyRow> Keys { get; set; } = [];

    // Set only immediately after OnPostSaveAsync's own redirect (Edit.cshtml.cs), via TempData — the
    // raw key is never persisted anywhere, so this is the only moment it's ever readable again. Reading
    // it here (RazorPages' TempData is a one-read-then-gone Keep()-less peek by default) means a page
    // refresh loses it, matching the "shown once" contract rather than accidentally persisting it in
    // the session.
    public string? NewRawKey { get; set; }
    public string? NewKeyName { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        NewRawKey = TempData["NewRawKey"] as string;
        NewKeyName = TempData["NewKeyName"] as string;

        var keys = await apiKeyService.ListAsync(ct);
        var roleNames = await db.Roles.ToDictionaryAsync(r => r.Id, r => r.Name ?? "(unknown role)", ct);

        Keys = keys.Select(k => new KeyRow(k.Id, k.Name,
            roleNames.GetValueOrDefault(k.RoleId, "(deleted role)"), k.KeyPrefix,
            k.CreatedAtUtc, k.LastUsedAtUtc, k.RevokedAtUtc is not null)).ToList();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        await apiKeyService.RevokeAsync(id, ct);
        await auditService.LogAsync("ApiKey.Revoke",
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), id.ToString(), ct);
        return RedirectToPage();
    }
}

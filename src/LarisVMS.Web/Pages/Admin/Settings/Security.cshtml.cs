using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// How long a signed-in session stays good before it's forced to log in again, per role — see
/// SessionLifetimePolicy for the enforcement logic (Program.cs's cookie OnValidatePrincipal handler)
/// and why a user holding several roles is bound by whichever is most restrictive. There's no
/// dedicated role-management page yet (RBAC is schema-ready but role CRUD isn't built — see the
/// roadmap), so this lists whatever roles already exist in the database rather than offering to
/// create new ones.
///
/// Also carries the live/playback custom-port setting (PortSegmentationMiddleware) — grouped here
/// rather than its own tab since both are "how tightly is this deployment locked down", not because
/// they share any code.
/// </summary>
[Authorize("Settings.Edit")]
public class SecurityModel(ApplicationDbContext db, ISettingsResolver settings, IAuditService auditService) : PageModel
{
    public record RoleLifetimeRow(string RoleId, string RoleName, int Hours);

    public List<RoleLifetimeRow> Roles { get; set; } = [];

    /// <summary>Null/0 (the default) means every route stays reachable on whatever port(s) IIS
    /// already binds — the feature only starts separating traffic once an admin sets a real port
    /// here <em>and</em> adds a matching IIS site binding for it (this setting alone can't open a
    /// new listening socket under IIS in-process hosting).</summary>
    [BindProperty] public int? CustomPort { get; set; }

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Roles = await LoadRowsAsync(ct);
        CustomPort = await settings.GetAsync<int?>(PortSegmentationMiddleware.SettingKey, null, ct: ct);
    }

    public async Task<IActionResult> OnPostAsync([FromForm] Dictionary<string, int> hours, CancellationToken ct)
    {
        if (CustomPort is { } p && (p is < 1 or > 65535))
        {
            ErrorMessage = "The custom port must be between 1 and 65535, or blank to disable it.";
            Roles = await LoadRowsAsync(ct);
            return Page();
        }

        var by = User.Identity?.Name;
        var before = await LoadRowsAsync(ct);
        var beforeByRole = before.ToDictionary(r => r.RoleId, r => r.Hours);
        var oldPort = await settings.GetAsync<int?>(PortSegmentationMiddleware.SettingKey, null, ct: ct);

        var fields = new List<AuditDiff.Field>();
        foreach (var (roleId, newHours) in hours)
        {
            var role = before.FirstOrDefault(r => r.RoleId == roleId);
            if (role is null) continue; // a role deleted between page load and submit

            var clamped = Math.Max(0, newHours);
            await settings.SetGlobalAsync(SessionLifetimePolicy.SettingKey(roleId), clamped.ToString(), by);
            fields.Add(AuditDiff.Of($"{role.RoleName} session lifetime (hours)",
                beforeByRole.GetValueOrDefault(roleId).ToString(), clamped.ToString()));
        }

        await settings.SetGlobalAsync(PortSegmentationMiddleware.SettingKey, CustomPort?.ToString() ?? "", by);
        fields.Add(AuditDiff.Of("Live/playback custom port", oldPort?.ToString() ?? "(none)", CustomPort?.ToString() ?? "(none)"));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), AuditDiff.Build([.. fields]));

        Roles = await LoadRowsAsync(ct);
        SavedMessage = "Saved.";
        return Page();
    }

    private async Task<List<RoleLifetimeRow>> LoadRowsAsync(CancellationToken ct)
    {
        var roles = await db.Roles.OrderBy(r => r.Name).ToListAsync(ct);
        var rows = new List<RoleLifetimeRow>();
        foreach (var role in roles)
        {
            var hours = await settings.GetAsync(SessionLifetimePolicy.SettingKey(role.Id), SessionLifetimePolicy.DefaultHours, ct: ct);
            rows.Add(new RoleLifetimeRow(role.Id, role.Name ?? role.Id, hours));
        }
        return rows;
    }
}

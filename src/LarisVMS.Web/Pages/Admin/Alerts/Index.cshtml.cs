using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Admin.Alerts;

[Authorize("Alerts.Edit")]
public class IndexModel(ApplicationDbContext db, ICameraService cameraService, INodeService nodeService, IAuditService auditService) : PageModel
{
    public record RuleRow(Guid Id, string Name, bool IsEnabled, string ConditionSummary,
        DateTime? LastFiredAt, IReadOnlyList<string> ChannelLabels);

    public List<RuleRow> Rules { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var rules = await db.AlertRules.Include(r => r.Deliveries).OrderBy(r => r.Name).ToListAsync(ct);
        var cameraNames = (await cameraService.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var nodeNames = (await nodeService.ListAsync(ct)).ToDictionary(n => n.Id, n => n.Name);

        Rules = rules.Select(r => new RuleRow(
            r.Id, r.Name, r.IsEnabled,
            Summarize(r, cameraNames, nodeNames),
            r.LastFiredAt,
            r.Deliveries.Where(d => d.IsEnabled).Select(d => d.Channel.ToString()).OrderBy(s => s).ToList()
        )).ToList();
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FindAsync([id], ct);
        if (rule is not null)
        {
            rule.IsEnabled = !rule.IsEnabled;
            rule.LastModifiedAt = DateTime.UtcNow;
            rule.LastModifiedBy = User.Identity?.Name;
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync(rule.IsEnabled ? "AlertRule.Enable" : "AlertRule.Disable",
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FindAsync([id], ct);
        if (rule is not null)
        {
            db.AlertRules.Remove(rule); // cascades to Deliveries
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("AlertRule.Delete",
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        }
        return RedirectToPage();
    }

    private static string Summarize(Core.Entities.AlertRule rule, Dictionary<Guid, string> cameraNames, Dictionary<Guid, string> nodeNames) =>
        rule.ConditionType switch
        {
            AlertConditionType.CameraNotReporting =>
                $"Camera \"{(rule.CameraId is { } cid ? cameraNames.GetValueOrDefault(cid, "(deleted camera)") : "?")}\" not reporting",
            AlertConditionType.NodeOffline =>
                $"Node \"{(rule.NodeId is { } nid ? nodeNames.GetValueOrDefault(nid, "(deleted node)") : "?")}\" offline",
            AlertConditionType.NodeStorageLow =>
                $"Node \"{(rule.NodeId is { } snid ? nodeNames.GetValueOrDefault(snid, "(deleted node)") : "?")}\" storage below {rule.ThresholdPercent}%",
            _ => rule.ConditionType.ToString()
        };
}

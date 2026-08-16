using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin;

// Razor Pages [Authorize] only applies at the page/model level, not per-handler (MVC1001) — this
// page mixes viewing and editing inline (unlike Cameras, which splits Index/Edit into separate
// pages+policies), so the whole page requires edit access rather than only the POST handlers.
[Authorize("Nodes.Edit")]
public class NodesModel(INodeService nodeService, ICameraService cameraService, ISettingsResolver settings, IAuditService auditService) : PageModel
{
    public List<Node> Nodes { get; set; } = [];
    public Dictionary<Guid, int> CameraCountByNode { get; set; } = [];
    public Dictionary<Guid, int> EffectiveRetentionDays { get; set; } = [];
    public Dictionary<Guid, int?> RetentionOverride { get; set; } = [];
    public Dictionary<Guid, double?> DaysRemaining { get; set; } = [];
    public Dictionary<Guid, int> StaleCameraCountByNode { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Nodes = await nodeService.ListAsync();
        var cameras = await cameraService.ListAsync();
        CameraCountByNode = cameras
            .Where(c => c.NodeId is not null)
            .GroupBy(c => c.NodeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Cameras/Index shows the same underlying data per-camera (which node(s) still hold its
        // stale footage); here it's inverted to "how many cameras have footage stranded on this
        // node" — a live query, no stored flag, same reasoning as the per-camera badge.
        StaleCameraCountByNode = (await cameraService.GetStaleSegmentNodeIdsAsync())
            .SelectMany(kv => kv.Value.Select(nodeId => nodeId))
            .GroupBy(nodeId => nodeId)
            .ToDictionary(g => g.Key, g => g.Count());

        DaysRemaining = await nodeService.GetEstimatedDaysRemainingAsync();

        foreach (var n in Nodes)
        {
            EffectiveRetentionDays[n.Id] = await settings.GetAsync("Retention.Days", 30, nodeId: n.Id);
            var ownOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Retention.Days");
            RetentionOverride[n.Id] = int.TryParse(ownOverride, out var days) ? days : null;
        }
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid id, string name, string? storageRootPath, int? retentionDaysOverride)
    {
        try
        {
            // Pre-edit state first, so the entry reports what actually changed — including the
            // per-node retention override, which previously rode along inside this same generic
            // "Node.Update" entry with nothing to indicate it had been touched at all. No secret
            // fields here: MediaSigningKey is generated internally and never edited through this form.
            var before = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
            var oldRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Retention.Days");

            await nodeService.UpdateAsync(id, name, storageRootPath);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Retention.Days",
                retentionDaysOverride?.ToString(), User.Identity?.Name);

            var details = AuditDiff.Build(
                AuditDiff.Of("Name", before?.Name, name),
                AuditDiff.Of("Storage root", before?.StorageRootPath, storageRootPath),
                AuditDiff.Of("Retention override", oldRetentionOverride, retentionDaysOverride?.ToString()));

            await LogAsync("Node.Update", details is null ? $"{name} ({id})" : $"{name} ({id}) — {details}");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await OnGetAsync();
            return Page();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        await nodeService.DeleteAsync(id);
        await LogAsync("Node.Delete", $"{node?.Name ?? "?"} ({id})");
        return RedirectToPage();
    }

    private Task LogAsync(string action, string details) =>
        auditService.LogAsync(action, User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), details);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;

namespace LarisVMS.Web.Pages.Admin;

// Razor Pages [Authorize] only applies at the page/model level, not per-handler (MVC1001) — this
// page mixes viewing and editing inline (unlike Cameras, which splits Index/Edit into separate
// pages+policies), so the whole page requires edit access rather than only the POST handlers.
[Authorize("Nodes.Edit")]
public class NodesModel(INodeService nodeService, ICameraService cameraService, ISettingsResolver settings,
    IAuditService auditService, IHttpClientFactory httpFactory) : PageModel
{
    public List<Node> Nodes { get; set; } = [];

    /// <summary>Survives the redirect after a restart POST so the result is visible on the reloaded
    /// page — TempData rather than a property, since RedirectToPage builds a fresh model.</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public bool StatusIsError { get; set; }
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

    /// <summary>Restarts a node's own Windows Service — not the machine. Proxied through this tier the
    /// same way every other Web→Node call is (the browser never talks to a node directly), authorized
    /// by a short-lived token signed with that node's own MediaSigningKey.
    ///
    /// Recording on this node's cameras stops for the few seconds the service is down, which is why
    /// this is behind Nodes.Edit and audited like any other node change.</summary>
    public async Task<IActionResult> OnPostRestartAsync(Guid id)
    {
        var node = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
        if (node is null)
        {
            StatusMessage = "That node no longer exists.";
            StatusIsError = true;
            return RedirectToPage();
        }

        // Same readiness requirement as the live/playback proxies: without a reported IP, port and
        // signing key there is no authenticated way to reach this node at all.
        if (node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
        {
            StatusMessage = $"{node.Name} hasn't reported its address yet — it needs at least one heartbeat before it can be restarted from here.";
            StatusIsError = true;
            return RedirectToPage();
        }

        var token = MediaToken.IssueForNodeControl("restart", key, TimeSpan.FromSeconds(30));
        var client = httpFactory.CreateClient();
        // Short: the node answers before it actually stops (see its own /restart comment), so a slow
        // response here means it's unreachable, not that it's busy restarting.
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            var response = await client.PostAsync(
                $"http://{ip}:{port}/restart?token={Uri.EscapeDataString(token)}", content: null);

            if (response.IsSuccessStatusCode)
            {
                StatusMessage = $"{node.Name} is restarting. It should reappear as online within a minute.";
                StatusIsError = false;
                await LogAsync("Node.Restart", $"{node.Name} ({id})");
            }
            else
            {
                // The node's own message is more specific than anything this tier could infer (e.g. a
                // missing updater binary), so it's surfaced verbatim rather than replaced.
                var body = (await response.Content.ReadAsStringAsync()).Trim();
                StatusMessage = $"{node.Name} refused the restart ({(int)response.StatusCode})" +
                    (string.IsNullOrEmpty(body) ? "." : $": {body}");
                StatusIsError = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = $"Could not reach {node.Name}: {ex.Message}";
            StatusIsError = true;
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

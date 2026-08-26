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
    /// <summary>"" (blank) means inherit the global default — same convention RetentionOverride's
    /// own blank-means-null uses, just expressed as a select option instead of a blank number
    /// input. See NodeConfigResponse.DetectionModelFamily's own doc comment for why this is a
    /// per-node override, not per-camera.</summary>
    public Dictionary<Guid, string> ModelFamilyOverride { get; set; } = [];
    public Dictionary<Guid, string> DFineWeightsOverride { get; set; } = [];
    public Dictionary<Guid, string> EffectiveModelFamily { get; set; } = [];
    public Dictionary<Guid, double?> DaysRemaining { get; set; } = [];
    public Dictionary<Guid, int> StaleCameraCountByNode { get; set; } = [];
    public Dictionary<Guid, List<StaleCameraRow>> StaleCamerasByNode { get; set; } = [];
    public string? ErrorMessage { get; set; }

    /// <summary>One row of the warning-emoji tooltip's detail list — ClearsAtUtc is when this
    /// camera's *last* remaining stale segment on this node ages past retention (see
    /// StaleSegmentDetail's own doc comment for why it's the newest segment, not the oldest, that
    /// determines this), which is the same moment the camera drops out of the warning on its own.</summary>
    public record StaleCameraRow(string CameraName, DateTime ClearsAtUtc);

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
        var staleDetails = await cameraService.GetStaleSegmentDetailsAsync();
        StaleCameraCountByNode = staleDetails
            .GroupBy(d => d.NodeId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Same resolution NodeService.GetConfigAsync uses to hand a retention value to this exact
        // node for this exact camera's orphaned footage (camera+node override -> node default ->
        // 30-day hardcoded fallback, matching StorageManager.SweepOrphanedCameraFolders' own
        // fallback constant) — so "falls off <date>" in the tooltip is the same date the node's own
        // sweep will actually act on, not a separate guess at it.
        StaleCamerasByNode = [];
        foreach (var d in staleDetails)
        {
            var retentionDays = await settings.GetAsync<int?>("Retention.Days", 30, cameraId: d.CameraId, nodeId: d.NodeId);
            var clearsAtUtc = d.NewestSegmentEndUtc.AddDays(retentionDays ?? 30);
            if (!StaleCamerasByNode.TryGetValue(d.NodeId, out var rows))
                StaleCamerasByNode[d.NodeId] = rows = [];
            rows.Add(new StaleCameraRow(d.CameraName, clearsAtUtc));
        }

        DaysRemaining = await nodeService.GetEstimatedDaysRemainingAsync();

        foreach (var n in Nodes)
        {
            EffectiveRetentionDays[n.Id] = await settings.GetAsync("Retention.Days", 30, nodeId: n.Id);
            var ownOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Retention.Days");
            RetentionOverride[n.Id] = int.TryParse(ownOverride, out var days) ? days : null;

            ModelFamilyOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.ModelFamily") ?? "";
            DFineWeightsOverride[n.Id] = await settings.GetOwnOverrideAsync(SettingScope.Node, n.Id, "Detection.DFineWeights") ?? "";
            EffectiveModelFamily[n.Id] = await settings.GetAsync("Detection.ModelFamily", "Auto", nodeId: n.Id);
        }
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid id, string name, string? storageRootPath, int? retentionDaysOverride,
        string? aiAccelerator, string? modelFamilyOverride, string? dfineWeightsOverride)
    {
        try
        {
            // Pre-edit state first, so the entry reports what actually changed — including the
            // per-node retention override, which previously rode along inside this same generic
            // "Node.Update" entry with nothing to indicate it had been touched at all. No secret
            // fields here: MediaSigningKey is generated internally and never edited through this form.
            var before = (await nodeService.ListAsync()).FirstOrDefault(n => n.Id == id);
            var oldRetentionOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Retention.Days");
            var oldModelFamilyOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.ModelFamily");
            var oldDFineWeightsOverride = await settings.GetOwnOverrideAsync(SettingScope.Node, id, "Detection.DFineWeights");
            // "" (blank/Auto) resolves as null — see NodeConfigResponse.AiAccelerator's own doc
            // comment for why Auto (not an explicit choice) is the safe default.
            var accelerator = Enum.TryParse<AiAccelerator>(aiAccelerator, out var acc) ? acc : (AiAccelerator?)null;

            await nodeService.UpdateAsync(id, name, storageRootPath, accelerator);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Retention.Days",
                retentionDaysOverride?.ToString(), User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.ModelFamily",
                string.IsNullOrEmpty(modelFamilyOverride) ? null : modelFamilyOverride, User.Identity?.Name);
            await settings.SetOverrideAsync(SettingScope.Node, id, "Detection.DFineWeights",
                string.IsNullOrEmpty(dfineWeightsOverride) ? null : dfineWeightsOverride, User.Identity?.Name);

            var details = AuditDiff.Build(
                AuditDiff.Of("Name", before?.Name, name),
                AuditDiff.Of("Storage root", before?.StorageRootPath, storageRootPath),
                AuditDiff.Of("Retention override", oldRetentionOverride, retentionDaysOverride?.ToString()),
                AuditDiff.Of("AI accelerator", before?.AiAccelerator?.ToString(), accelerator?.ToString()),
                AuditDiff.Of("Detection model override", oldModelFamilyOverride, modelFamilyOverride),
                AuditDiff.Of("D-FINE weights override", oldDFineWeightsOverride, dfineWeightsOverride));

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
            // See MediaTokenRequest's own doc comment: also sent as ?token= below for a node that
            // hasn't updated yet.
            var response = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Post,
                $"http://{ip}:{port}/restart?token={Uri.EscapeDataString(token)}", token));

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

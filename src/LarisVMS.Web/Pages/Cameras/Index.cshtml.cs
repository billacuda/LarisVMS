using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

[Authorize("Cameras.View")]
public class IndexModel(ICameraService cameraService, INodeService nodeService, ISettingsResolver settings) : PageModel
{
    public List<Camera> Cameras { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public Dictionary<Guid, long> StorageUsedBytes { get; set; } = [];
    public Dictionary<Guid, int> EffectiveRetentionDays { get; set; } = [];
    public Dictionary<Guid, List<Guid>> StaleSegmentNodeIds { get; set; } = [];

    public async Task OnGetAsync()
    {
        Cameras = await cameraService.ListAsync();
        Nodes = await nodeService.ListAsync();
        StorageUsedBytes = await cameraService.GetStorageUsageAsync();
        StaleSegmentNodeIds = await cameraService.GetStaleSegmentNodeIdsAsync();

        foreach (var c in Cameras)
            EffectiveRetentionDays[c.Id] = await settings.GetAsync("Retention.Days", 30, cameraId: c.Id, nodeId: c.NodeId);
    }

    public async Task<IActionResult> OnPostProbeAsync(Guid id)
    {
        await cameraService.ProbeAsync(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(Guid id)
    {
        await cameraService.ToggleEnabledAsync(id);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReassignAsync(List<Guid> ids, Guid? nodeId)
    {
        await nodeService.ReassignCamerasAsync(ids, nodeId);
        return RedirectToPage();
    }
}

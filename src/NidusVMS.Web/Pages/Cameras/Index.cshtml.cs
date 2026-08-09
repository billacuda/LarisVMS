using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Cameras;

[Authorize("Cameras.View")]
public class IndexModel(ICameraService cameraService, ISettingsResolver settings) : PageModel
{
    public List<Camera> Cameras { get; set; } = [];
    public Dictionary<Guid, long> StorageUsedBytes { get; set; } = [];
    public Dictionary<Guid, int> EffectiveRetentionDays { get; set; } = [];

    public async Task OnGetAsync()
    {
        Cameras = await cameraService.ListAsync();
        StorageUsedBytes = await cameraService.GetStorageUsageAsync();

        foreach (var c in Cameras)
            EffectiveRetentionDays[c.Id] = await settings.GetAsync("Retention.Days", 30, cameraId: c.Id, nodeId: c.NodeId);
    }

    public async Task<IActionResult> OnPostProbeAsync(Guid id)
    {
        await cameraService.ProbeAsync(id);
        return RedirectToPage();
    }
}

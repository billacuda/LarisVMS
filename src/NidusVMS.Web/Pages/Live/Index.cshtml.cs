using Microsoft.AspNetCore.Authorization;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace NidusVMS.Web.Pages.Live;

[Authorize("Cameras.View")]
public class IndexModel(ICameraService cameraService) : PageModel
{
    public List<Camera> Cameras { get; set; } = [];

    public async Task OnGetAsync()
    {
        // Only cameras that could actually answer a /live request — enabled and assigned to a node.
        // Camera.Streams.Main also needs to be enabled specifically (per-stream enable/disable, M4),
        // but that's a per-stream state the list projection doesn't carry here; a camera missing it
        // just gets a 503 from the proxy when "Watch" is clicked, same as a node that hasn't
        // heartbeat-reported readiness yet — both are rare/transient enough not to need a separate
        // grayed-out state on this first pass.
        var all = await cameraService.ListAsync();
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).OrderBy(c => c.Name).ToList();
    }
}

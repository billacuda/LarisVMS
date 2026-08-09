using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Playback;

/// <summary>Playback is driven by saved Views (M6), the same as Pages/Views/Play — pick a view,
/// its cell layout (positions, aspect ratios, cameras) renders with playback video and a shared
/// timeline instead of live video. No standalone camera picker here; a one-off arrangement is what
/// Views/Editor is for.</summary>
[Authorize("Playback.View")]
public class IndexModel(ICameraService cameraService, IViewService viewService) : PageModel
{
    public List<Camera> Cameras { get; set; } = [];
    public List<View> Views { get; set; } = [];

    public async Task OnGetAsync()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        // Same filter as Live/Views: only cameras that could actually answer a playback request.
        var all = await cameraService.ListAsync();
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).OrderBy(c => c.Name).ToList();

        Views = await viewService.ListVisibleToAsync(userId);
    }
}

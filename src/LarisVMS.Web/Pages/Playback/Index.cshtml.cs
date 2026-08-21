using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Playback;

/// <summary>Playback is driven by saved Views (M6), the same as Pages/Views/Play — pick a view,
/// its cell layout (positions, aspect ratios, cameras) renders with playback video and a shared
/// timeline instead of live video. No standalone camera picker here; a one-off arrangement is what
/// Views/Editor is for.</summary>
[Authorize("Playback.View")]
public class IndexModel(ICameraService cameraService, IViewService viewService, ICameraAccessService cameraAccess) : PageModel
{
    public List<Camera> Cameras { get; set; } = [];
    public List<View> Views { get; set; } = [];

    /// <summary>A Bookmark or Snapshot "▶ Play" link (Pages/Bookmarks/Index, Pages/Snapshots/Index) —
    /// jumps straight to this camera at this instant instead of the usual "pick a view, wait for it to
    /// land on 'now'" start. Both null for an ordinary visit. The client renders just this one camera
    /// directly (playback-player.js's resolveDeepLink) rather than searching for a View that happens
    /// to contain it — no saved View is used or required, so this page doesn't need to know about
    /// Views to support the deep link.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? CameraId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? AtUtc { get; set; }

    public async Task OnGetAsync()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        // Same filter as Live/Views: only cameras that could actually answer a playback request.
        var all = await cameraService.ListAsync();
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).OrderBy(c => c.Name).ToList();

        // Narrows to whatever CameraAccess grants this viewer for Playback — same reasoning as
        // Views/Play's own filter, one action lower in the CameraAccessActions flags: a camera
        // missing from this feed can't be selected into any tile client-side, since view/playback
        // both drive their tiles from cameraById built off exactly this list.
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(User, CameraAccessActions.Playback);
        if (accessible is not null) Cameras = Cameras.Where(c => accessible.Contains(c.Id)).ToList();

        Views = await viewService.ListVisibleToAsync(userId);
    }
}

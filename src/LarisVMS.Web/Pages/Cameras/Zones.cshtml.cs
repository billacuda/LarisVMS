using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

/// <summary>M8 zone editor. Server side only resolves the camera (name for the header, id for the
/// JS module); the zone list itself and every create/update/delete round-trips through
/// zones-editor.js against the /api/cameras/{id}/zones and /api/zones/{id} endpoints, same "load and
/// mutate client-side, page model just seeds the shell" shape as Pages/Views/Editor.
///
/// Codec/HasAudio (pass 3c-1) are the Main stream's own reported values, the same fields
/// view-play.js's own cam.codec/cam.hasAudio already read for live-view.js's start() call on every
/// other page that plays live video — null/false on a camera that's never connected yet, which
/// pickMimeType (live-view.js) already handles the same way those other pages do.</summary>
[Authorize("Cameras.Edit")]
public class ZonesModel(ICameraService cameraService) : PageModel
{
    public Guid CameraId { get; set; }
    public string CameraName { get; set; } = string.Empty;
    public bool CameraFound { get; private set; }
    public string? Codec { get; set; }
    public bool HasAudio { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var camera = await cameraService.GetAsync(id);
        if (camera is null) return Redirect("/Cameras");

        CameraId = id;
        CameraName = camera.Name;
        CameraFound = true;

        var mainStream = camera.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
        Codec = mainStream?.Codec;
        HasAudio = mainStream?.HasAudio ?? false;

        return Page();
    }
}

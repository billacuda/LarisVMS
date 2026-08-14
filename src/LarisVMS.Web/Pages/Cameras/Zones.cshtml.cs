using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

/// <summary>M8 zone editor. Server side only resolves the camera (name for the header, id for the
/// JS module); the zone list itself and every create/update/delete round-trips through
/// zones-editor.js against the /api/cameras/{id}/zones and /api/zones/{id} endpoints, same "load and
/// mutate client-side, page model just seeds the shell" shape as Pages/Views/Editor.</summary>
[Authorize("Cameras.Edit")]
public class ZonesModel(ICameraService cameraService) : PageModel
{
    public Guid CameraId { get; set; }
    public string CameraName { get; set; } = string.Empty;
    public bool CameraFound { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var camera = await cameraService.GetAsync(id);
        if (camera is null) return RedirectToPage("Index");

        CameraId = id;
        CameraName = camera.Name;
        CameraFound = true;
        return Page();
    }
}

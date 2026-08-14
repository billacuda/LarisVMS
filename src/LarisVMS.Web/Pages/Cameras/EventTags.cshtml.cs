using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

/// <summary>M8 pass 8: ONVIF event tag rule editor. Same "page model just seeds the shell, JS module
/// does all the loading/CRUD" shape as Zones — see ZonesModel's own doc comment. No canvas/drawing
/// here though: a rule is a topic match, not a polygon, so the whole editor is list + form panels
/// against /api/cameras/{id}/event-tag-rules and /api/event-tag-rules/{id}, plus a read-only observed-
/// topics panel (/api/cameras/{id}/event-tag-rules/observed-topics) the Start/Stop topic fields are
/// autocompleted from.</summary>
[Authorize("Cameras.Edit")]
public class EventTagsModel(ICameraService cameraService) : PageModel
{
    public Guid CameraId { get; set; }
    public string CameraName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var camera = await cameraService.GetAsync(id);
        if (camera is null) return RedirectToPage("Index");

        CameraId = id;
        CameraName = camera.Name;
        return Page();
    }
}

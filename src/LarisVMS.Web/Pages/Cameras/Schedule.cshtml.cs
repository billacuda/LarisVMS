using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

/// <summary>M8 Schedule mode: per-camera time-window editor. Same "page model just seeds the shell,
/// JS module does all the loading/CRUD" shape as Zones/EventTags — see ZonesModel's own doc
/// comment. No canvas/drawing and no observed-topics panel here: a window is a day-of-week +
/// start/end time pair, not a shape or a topic match, so the whole editor is a list + form panel
/// against /api/cameras/{id}/schedule-windows and /api/schedule-windows/{id}.</summary>
[Authorize("Cameras.Edit")]
public class ScheduleModel(ICameraService cameraService) : PageModel
{
    public Guid CameraId { get; set; }
    public string CameraName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var camera = await cameraService.GetAsync(id);
        if (camera is null) return Redirect("/Cameras");

        CameraId = id;
        CameraName = camera.Name;
        return Page();
    }
}

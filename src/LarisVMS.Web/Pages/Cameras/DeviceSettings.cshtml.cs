using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

/// <summary>The camera's own encoder, image, and audio settings, changed over ONVIF. Same "page model
/// seeds the shell, JS does the loading" shape as Zones/Schedule: the settings are read live from the
/// camera (camera-device-settings.js → /api/cameras/{id}/device-settings), so the page renders
/// immediately even when the camera is slow or offline.
///
/// <c>?with=id,id,…</c> preselects other cameras of the same model as bulk-apply targets — the
/// Cameras list's "Device settings…" bulk action links here with the rest of its selection. The JS
/// only offers cameras from the peers endpoint, so an id that isn't a same-model peer is ignored.</summary>
[Authorize("Cameras.Edit")]
[LarisVMS.Web.Auth.RequireCameraConfigure]
public class DeviceSettingsModel(ICameraService cameraService) : PageModel
{
    public Guid CameraId { get; set; }
    public string CameraName { get; set; } = string.Empty;
    public string? MakeAndModel { get; set; }
    public IReadOnlyList<Guid> WithIds { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, string? with)
    {
        var camera = await cameraService.GetAsync(id);
        if (camera is null) return Redirect("/Cameras");

        CameraId = id;
        CameraName = camera.Name;
        MakeAndModel = string.Join(" ", new[] { camera.Manufacturer, camera.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (MakeAndModel.Length == 0) MakeAndModel = null;
        WithIds = (with ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty && g != id)
            .Distinct()
            .ToList();
        return Page();
    }
}

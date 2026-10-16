using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

/// <summary>Speed trap editor. Like the Zones page, the server only seeds the shell; the trap list
/// and every create/update/delete go through speed-trap-editor.js against /api/cameras/{id}/speed-traps
/// and /api/speed-traps/{id}.</summary>
[Authorize("Cameras.Edit")]
public class SpeedTrapsModel(ICameraService cameraService, ISettingsResolver settings) : PageModel
{
    public Guid CameraId { get; set; }
    public string CameraName { get; set; } = string.Empty;
    public string? Codec { get; set; }
    public bool HasAudio { get; set; }
    public bool AiDetectionEnabled { get; set; }

    /// <summary>Detection.SpeedUnits — also picks whether the distance box starts in metres or feet.</summary>
    public string SpeedUnit { get; set; } = SpeedUnits.Default;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var camera = await cameraService.GetAsync(id);
        if (camera is null) return Redirect("/Cameras");

        CameraId = id;
        CameraName = camera.Name;
        AiDetectionEnabled = camera.AiDetectionEnabled;
        var mainStream = camera.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
        Codec = mainStream?.Codec;
        HasAudio = mainStream?.HasAudio ?? false;
        SpeedUnit = SpeedUnits.Normalize(await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default));
        return Page();
    }
}

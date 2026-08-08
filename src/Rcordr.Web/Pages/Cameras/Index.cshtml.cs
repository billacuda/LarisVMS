using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Entities;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Cameras;

[Authorize("Cameras.View")]
public class IndexModel(ICameraService cameraService) : PageModel
{
    public List<Camera> Cameras { get; set; } = [];

    public async Task OnGetAsync()
    {
        Cameras = await cameraService.ListAsync();
    }

    public async Task<IActionResult> OnPostProbeAsync(Guid id)
    {
        await cameraService.ProbeAsync(id);
        return RedirectToPage();
    }
}

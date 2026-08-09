using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Cameras;

[Authorize("Cameras.Edit")]
public class DiscoverModel(ICameraDiscoveryService discoveryService) : PageModel
{
    public List<DiscoveredCameraDto> Results { get; set; } = [];
    public bool HasScanned { get; set; }
    public bool Scanning { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostScanAsync()
    {
        Results = (await discoveryService.DiscoverAsync(TimeSpan.FromSeconds(5))).ToList();
        HasScanned = true;
        return Page();
    }
}

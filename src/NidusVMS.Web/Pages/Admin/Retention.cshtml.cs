using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Admin;

[Authorize("Retention.Edit")]
public class RetentionModel(ISettingsResolver settings) : PageModel
{
    [BindProperty] public int RetentionDays { get; set; } = 30;
    [BindProperty] public int WatermarkPercent { get; set; } = 90;
    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        RetentionDays = await settings.GetAsync("Retention.Days", 30);
        WatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await settings.SetGlobalAsync("Retention.Days", RetentionDays.ToString(), User.Identity?.Name);
        await settings.SetGlobalAsync("Storage.WatermarkPercent", WatermarkPercent.ToString(), User.Identity?.Name);
        SavedMessage = "Saved.";
        return Page();
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Setup;

public class BrandingModel(ISetupService setupService) : PageModel
{
    [BindProperty] public string AppName { get; set; } = "LarisVMS";
    [BindProperty] public string PrimaryColor { get; set; } = "#0d6efd";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await setupService.SaveBrandingAsync(AppName, PrimaryColor);
        return RedirectToPage("Review");
    }
}

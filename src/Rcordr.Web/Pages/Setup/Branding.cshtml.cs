using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Setup;

public class BrandingModel(ISetupService setupService) : PageModel
{
    [BindProperty] public string AppName { get; set; } = "Rcordr";
    [BindProperty] public string PrimaryColor { get; set; } = "#0d6efd";

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await setupService.SaveBrandingAsync(AppName, PrimaryColor);
        return RedirectToPage("Review");
    }
}

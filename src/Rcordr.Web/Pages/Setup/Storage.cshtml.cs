using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Setup;

public class StorageModel(ISetupService setupService) : PageModel
{
    [BindProperty] public string RootPath { get; set; } = string.Empty;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!string.IsNullOrWhiteSpace(RootPath))
            await setupService.SaveStorageRootAsync(RootPath);
        return RedirectToPage("Node");
    }
}

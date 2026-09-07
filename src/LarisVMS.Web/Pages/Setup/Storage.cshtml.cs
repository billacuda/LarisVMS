using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Setup;

// Informational only since 0.188.0 — storage config is per recorder node (set by install-node.ps1
// or on Admin -> Nodes), there is no global storage path to collect here anymore.
public class StorageModel : PageModel
{
    public void OnGet() { }

    public IActionResult OnPost() => RedirectToPage("Node");
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to Admin/Settings/Branding as one tab of the combined Settings page — see
/// Admin/Backup's own stub for the same move. Plain URL redirect, not RedirectToPage: that target
/// is now a Blazor route (Components/Pages/Admin/Settings/Branding.razor), not a Razor Page, and
/// RedirectToPage only resolves actual Razor Page names.</summary>
public class BrandingModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Admin/Settings/Branding");
}

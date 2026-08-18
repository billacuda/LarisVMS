using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to Admin/Settings/Branding as one tab of the combined Settings page — see
/// Admin/Backup's own stub for the same move.</summary>
public class BrandingModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Settings/Branding");
}

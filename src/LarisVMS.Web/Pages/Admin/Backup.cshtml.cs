using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to Admin/Settings/Backups as one tab of the combined Settings page — this stub
/// only exists so an old bookmark or nav link still lands somewhere real instead of a 404, same
/// pattern as Admin/Retention's own stub.</summary>
public class BackupModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Settings/Backups");
}

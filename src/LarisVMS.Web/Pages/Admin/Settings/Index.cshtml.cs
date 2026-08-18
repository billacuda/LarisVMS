using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Lands on the first tab alphabetically ("Backups") — no [Authorize] of its own, since
/// each tab is independently gated (most by Settings.Edit, Backups by Backups.Edit) and the first
/// real page hit decides what a given user can actually reach.</summary>
public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("Backups");
}

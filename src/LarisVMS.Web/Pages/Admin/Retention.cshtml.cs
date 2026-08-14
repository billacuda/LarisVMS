using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Retention got folded into the general Admin/Settings page (it's just one of several
/// global settings that gap applied to) — this stub only exists so an old bookmark or the previous
/// nav link's cached URL still lands somewhere real instead of a 404.</summary>
public class RetentionModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("Settings");
}

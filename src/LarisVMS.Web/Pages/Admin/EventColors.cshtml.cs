using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to Admin/Settings/Events as one tab of the combined Settings page (which now also
/// holds the event-tag-position setting alongside these colors) — see Admin/Backup's own stub for
/// the same move.</summary>
public class EventColorsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Settings/Events");
}

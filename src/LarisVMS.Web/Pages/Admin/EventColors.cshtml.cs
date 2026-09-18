using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to Admin/Settings/Events as one tab of the combined Settings page (which now also
/// holds the event-tag-position setting alongside these colors) — see Admin/Backup's own stub for
/// the same move. Plain URL redirect, not RedirectToPage: that target is now a Blazor route
/// (Components/Pages/Admin/Settings/Events.razor), not a Razor Page, and RedirectToPage only
/// resolves actual Razor Page names.</summary>
public class EventColorsModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Admin/Settings/Events");
}

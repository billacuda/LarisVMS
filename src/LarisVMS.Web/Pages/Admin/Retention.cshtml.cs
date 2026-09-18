using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Retention got folded into the general Settings page (it's just one of several global
/// settings that gap applied to), which itself later split into tabs — this stub only exists so an
/// old bookmark or the previous nav link's cached URL still lands somewhere real instead of a 404.
/// Points straight at the Storage and Retention tab rather than the Settings landing page, since
/// that's specifically where this content now lives. Plain URL redirect, not RedirectToPage: that
/// target is now a Blazor route (Components/Pages/Admin/Settings/StorageAndRetention.razor), not a
/// Razor Page, and RedirectToPage only resolves actual Razor Page names.</summary>
public class RetentionModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Admin/Settings/StorageAndRetention");
}

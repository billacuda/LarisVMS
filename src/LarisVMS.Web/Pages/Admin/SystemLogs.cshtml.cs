using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to its own top-level Logs area (Pages/Logs/SystemLogs) alongside Audit Logs,
/// rather than living inside the Admin dropdown — see Admin/Logs' own stub for the same move.
/// Plain URL redirect, not RedirectToPage: that target is now a Blazor route
/// (Components/Pages/Logs/SystemLogs.razor), not a Razor Page, and RedirectToPage only resolves
/// actual Razor Page names.</summary>
public class SystemLogsModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Logs/SystemLogs");
}

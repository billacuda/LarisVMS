using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Moved to its own top-level Logs area (Pages/Logs/SystemLogs) alongside Audit Logs,
/// rather than living inside the Admin dropdown — see Admin/Logs' own stub for the same move.</summary>
public class SystemLogsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Logs/SystemLogs");
}

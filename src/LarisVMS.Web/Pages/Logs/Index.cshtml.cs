using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Logs;

/// <summary>Lands on the first tab — no [Authorize] of its own, since which tab a given user can
/// actually reach depends on which of Logs.View/SystemLogs.View they hold, and AuditLogs' own gate
/// already handles that on the very next request.</summary>
public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("AuditLogs");
}

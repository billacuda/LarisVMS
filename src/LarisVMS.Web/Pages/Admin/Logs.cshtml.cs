using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>The audit trail moved to its own top-level Logs area (Pages/Logs/AuditLogs) alongside
/// System Logs, rather than living inside the Admin dropdown — this stub only exists so an old
/// bookmark or nav link still lands somewhere real instead of a 404, same pattern as
/// Admin/Retention's own stub.</summary>
public class LogsModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Logs/AuditLogs");
}

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
// The error page changes no state, and must render even for a request whose antiforgery token
// failed — the ASP.NET Core template default.
#pragma warning disable S4502
[IgnoreAntiforgeryToken]
#pragma warning restore S4502
public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}

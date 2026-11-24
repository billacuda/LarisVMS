using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
// The error page changes no state, and must render even for a request whose antiforgery token
// failed — the ASP.NET Core template default.
#pragma warning disable S4502
[IgnoreAntiforgeryToken]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
#pragma warning restore S4502
public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>The page failed on a password or key in the database that this server can't decrypt.</summary>
    public bool SecretsUnreadable =>
        SecretUnreadableException.IsIn(HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error);

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}

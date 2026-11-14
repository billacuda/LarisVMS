using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Localization;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Setup;

/// <summary>The setup token (<see cref="SetupToken"/>) before the wizard opens from another computer.</summary>
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("sign-in")]
public class TokenModel(SetupToken setupToken, ILogger<TokenModel> logger) : PageModel
{
    [BindProperty] public string? Token { get; set; }

#pragma warning disable S2325 // the page reads it as Model.FilePath
    public string FilePath => SetupToken.FilePath;
#pragma warning restore S2325
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (setupToken.IsAuthorized(HttpContext)) return Redirect("/Setup");
        setupToken.Ensure();
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!setupToken.Matches(Token))
        {
            logger.LogWarning("Wrong setup token from {Address}.", HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = Loc.T("setup.token.wrong");
            return Page();
        }
        Response.Cookies.Append(SetupToken.CookieName, setupToken.CookieValue(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
        });
        return Redirect("/Setup");
    }
}

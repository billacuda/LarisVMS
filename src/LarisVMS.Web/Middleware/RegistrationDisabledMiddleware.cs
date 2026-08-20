namespace LarisVMS.Web.Middleware;

/// <summary>
/// Blocks ASP.NET Core Identity's default scaffolded self-registration page
/// (<c>/Identity/Account/Register</c>, from the packaged Identity UI Razor Class Library —
/// AddDefaultUI() pulls it in with nothing in this project overriding or disabling it). Found while
/// looking into why there's no admin surface to control who gets an account: there was nothing
/// <em>stopping</em> anyone who could reach the site from creating one themselves, with no invite or
/// approval step. A self-registered account gets no role and so can't do anything today (every
/// permission check fails closed with zero roles — see PermissionService), but that's an accident of
/// there being nothing to grant it *with* yet, not a deliberate access control.
///
/// A path check in middleware rather than scaffolding and overriding the Register page's own Razor
/// Pages route: the packaged page's exact area/page routing is internal to the Identity UI RCL, and
/// this needs to keep working regardless of how that's structured. Redirects to Login rather than a
/// bare 404 — a user who reaches this via whatever link the packaged Login page renders should land
/// somewhere sensible, not a dead end. Real account creation is what the upcoming user-management
/// admin page is for.
/// </summary>
public class RegistrationDisabledMiddleware(RequestDelegate next)
{
    internal const string RegisterPath = "/Identity/Account/Register";

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsRegisterPath(context.Request.Path))
        {
            context.Response.Redirect("/Identity/Account/Login");
            return;
        }

        await next(context);
    }

    /// <summary>Pure so the match itself is unit-testable without a real HttpContext.</summary>
    internal static bool IsRegisterPath(PathString path) =>
        path.StartsWithSegments(RegisterPath, StringComparison.OrdinalIgnoreCase);
}

public static class RegistrationDisabledMiddlewareExtensions
{
    public static IApplicationBuilder UseRegistrationDisabled(this IApplicationBuilder app) =>
        app.UseMiddleware<RegistrationDisabledMiddleware>();
}

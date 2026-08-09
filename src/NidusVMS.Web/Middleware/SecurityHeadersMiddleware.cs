namespace NidusVMS.Web.Middleware;

/// <summary>
/// rsolva's SecurityHeadersMiddleware, with the CSP extended for NidusVMS's media path: `media-src
/// blob:` for MSE-backed <video> playback. `connect-src 'self'` covers every API call this app
/// makes — confirmed by the actual M5/M7 design (see the plan's "Media path" section): the browser
/// never talks to a recorder node directly, IIS/NidusVMS.Web proxies every live and playback byte,
/// so there's no cross-origin `wss://`/`https://` node connection to widen this for.
///
/// Bootstrap and GridStack are vendored locally (`wwwroot/lib/`) rather than loaded from a CDN, so
/// script-src/style-src/font-src don't need a CDN host allow-listed — one less origin to trust, and
/// the app works with no outbound internet access from the browser at all.
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "geolocation=()";
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "font-src 'self'; " +
                "img-src 'self' data: https:; " +
                "media-src 'self' blob:; " +
                "connect-src 'self'; " +
                "frame-ancestors 'self';";

            return Task.CompletedTask;
        });

        await next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}

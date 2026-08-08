namespace Rcordr.Web.Middleware;

/// <summary>
/// rsolva's SecurityHeadersMiddleware, with the CSP extended for Rcordr's media path: `media-src
/// blob:` for MSE-backed <video> playback, and `connect-src` widened for the WebSocket connections
/// live/playback open directly to recorder nodes (wss://&lt;node&gt;). The node hostnames are only
/// known once Nodes exist (M3) — for now `connect-src 'self'` covers same-origin API calls, and this
/// gets revisited when the direct-to-node media path (see the plan's "Media path" section) lands.
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
                "script-src 'self' 'unsafe-inline' cdn.jsdelivr.net; " +
                "style-src 'self' 'unsafe-inline' cdn.jsdelivr.net; " +
                "font-src 'self' cdn.jsdelivr.net; " +
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

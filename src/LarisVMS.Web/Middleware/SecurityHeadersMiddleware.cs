namespace LarisVMS.Web.Middleware;

/// <summary>
/// rsolva's SecurityHeadersMiddleware, with the CSP extended for LarisVMS's media path: `media-src
/// blob:` for MSE-backed <video> playback, and `img-src blob:` for hover-thumbnail <img> previews
/// (M7 pass 2) — fetched as a blob and shown via URL.createObjectURL, same mechanism as MSE's own
/// blob-URL video src, just a different element/directive that needed the same widening. Confirmed
/// live: media-src already allowed blob: for video, but img-src didn't for images, so every
/// thumbnail load was silently blocked by the browser's own CSP enforcement. `connect-src 'self'`
/// covers every API call this app makes in the original proxy design; since the failover plan's
/// phase 1 the browser can also be told to stream straight from a recorder node (direct mode), so
/// connect-src is widened at runtime with each node's own client-endpoint origin (see
/// <see cref="ClientEndpointCspCache"/>) — empty, and connect-src unchanged, whenever no node has one.
///
/// Bootstrap and GridStack are vendored locally (`wwwroot/lib/`) rather than loaded from a CDN, so
/// script-src/style-src/font-src don't need a CDN host allow-listed — one less origin to trust, and
/// the app works with no outbound internet access from the browser at all.
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClientEndpointCspCache cspCache)
    {
        await cspCache.EnsureFreshAsync(context.RequestAborted);
        // connect-src also covers the final URL after a redirect, so the node's https:// origin is
        // needed here as well as its wss:// one (playback direct mode 302s /playback-segment to it).
        var connectSrc = string.IsNullOrEmpty(cspCache.Sources) ? "'self'" : $"'self' {cspCache.Sources}";

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
                "img-src 'self' data: https: blob:; " +
                "media-src 'self' blob:; " +
                $"connect-src {connectSrc}; " +
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

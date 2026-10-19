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
    public async Task InvokeAsync(HttpContext context, ClientEndpointCspCache cspCache, LarisVMS.Web.Security.CspNonce nonce)
    {
        await cspCache.EnsureFreshAsync(context.RequestAborted);
        // connect-src also covers the final URL after a redirect, so the node's https:// origin is
        // needed here as well as its wss:// one (playback direct mode 302s /playback-segment to it).
        var connectSrc = string.IsNullOrEmpty(cspCache.Sources) ? "'self'" : $"'self' {cspCache.Sources}";
        // Images: this site, inline data/blob, the map tile server, and the node/proxy endpoints a
        // browser may be sent to — not any HTTPS host.
        var imgSrc = $"'self' data: blob: {cspCache.ImageSources} {cspCache.Sources}".Trim();

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "geolocation=()";
            string Policy(string scriptSrc) =>
                "default-src 'self'; " +
                $"script-src {scriptSrc}; " +
                "style-src 'self' 'unsafe-inline'; " +
                "font-src 'self'; " +
                $"img-src {imgSrc}; " +
                "media-src 'self' blob:; " +
                $"connect-src {connectSrc}; " +
                "frame-ancestors 'self'; " +
                "base-uri 'self'; " +
                "object-src 'none';";

            // Scripts: only this site's files and inline scripts carrying this request's nonce — so a
            // script injected into a page doesn't run. Until an admin turns it on (Settings →
            // Security) the strict policy is report-only and the old one is enforced: a nonce in the
            // enforced policy would make browsers ignore its 'unsafe-inline'.
            var strict = Policy($"'self' 'nonce-{nonce.Value}'") + " report-uri /api/csp-report; report-to csp;";
            headers["Reporting-Endpoints"] = "csp=\"/api/csp-report\"";
            if (cspCache.StrictScripts)
            {
                headers["Content-Security-Policy"] = strict;
            }
            else
            {
                headers["Content-Security-Policy"] = Policy("'self' 'unsafe-inline'");
                headers["Content-Security-Policy-Report-Only"] = strict;
            }

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

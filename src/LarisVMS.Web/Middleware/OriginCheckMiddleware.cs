using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Refuses cross-site use of the signed-in cookie. A WebSocket (live video, talk, the overlays) or a
/// state-changing API call (POST/PUT/PATCH/DELETE under /api) from a browser page carries an Origin
/// header; it has to name this site — the same hostname on any of its ports (live view may use its
/// own port), the host a reverse proxy forwarded (X-Forwarded-Host — a cross-site page can't set that
/// header on a WebSocket or a simple request), or the configured public origin. Node, proxy, and
/// API-key calls aren't cookie-based and aren't checked; a request with no Origin at all isn't from a
/// cross-site page.
/// </summary>
public class OriginCheckMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISettingsResolver settings)
    {
        if (NeedsCheck(context.Request, context.WebSockets.IsWebSocketRequest)
            && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin
            && !IsAllowed(origin, context.Request.Host.Host,
                await settings.GetAsync<string?>("LiveView.PublicOrigin", null, ct: context.RequestAborted))
            && !ForwardedHosts(context.Request).Any(h => IsAllowed(origin, h, null)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Cross-site request refused.", context.RequestAborted);
            return;
        }
        await next(context);
    }

    internal static bool NeedsCheck(HttpRequest request, bool isWebSocket)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/api/nodes", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/proxies", StringComparison.OrdinalIgnoreCase)
            || request.Headers.ContainsKey(ApiKeyAuthMiddleware.HeaderName))
            return false;
        if (isWebSocket) return true;
        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
                || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method));
    }

    private static IEnumerable<string> ForwardedHosts(HttpRequest request) =>
        request.Headers["X-Forwarded-Host"].SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(h => HostString.FromUriComponent(h).Host);

    internal static bool IsAllowed(string origin, string requestHost, string? publicOrigin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        if (string.Equals(originUri.Host, requestHost, StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(publicOrigin, UriKind.Absolute, out var configured)
            && string.Equals(originUri.Host, configured.Host, StringComparison.OrdinalIgnoreCase);
    }
}

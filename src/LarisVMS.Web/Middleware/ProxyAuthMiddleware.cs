using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Failover plan phase 2: authenticates standalone relays on <c>/api/proxies/*</c> (except
/// <c>/register</c>) via <c>Authorization: Bearer {proxyId}:{secret}</c> — the exact shape of
/// <see cref="NodeAuthMiddleware"/>, stashing the authenticated <see cref="MediaProxy"/> on
/// <c>HttpContext.Items</c> for the endpoint handlers.
/// </summary>
public class ProxyAuthMiddleware(RequestDelegate next)
{
    public const string HttpContextItemKey = "MediaProxy";

    public async Task InvokeAsync(HttpContext context, IProxyService proxyService)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/proxies/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/proxies/register", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var token = header["Bearer ".Length..];
        var separator = token.IndexOf(':');
        if (separator < 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Normalized for the same reason as NodeAuthMiddleware — see IpAllowListPolicy.ToUrlHost.
        var proxy = await proxyService.AuthenticateAsync(
            token[..separator], token[(separator + 1)..],
            IpAllowListPolicy.ToUrlHost(context.Connection.RemoteIpAddress), context.RequestAborted);
        if (proxy is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[HttpContextItemKey] = proxy;
        await next(context);
    }
}

using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Authenticates recorder nodes on `/api/nodes/*` (except `/register`, which authenticates with the
/// one-time registration key instead) via `Authorization: Bearer {nodeId}:{secret}`. Mirrors
/// dploid's AgentAuthMiddleware placement — registered between UseAuthentication and
/// UseAuthorization so it runs before endpoint authorization is evaluated, and stashes the
/// authenticated Node on HttpContext.Items for endpoint handlers to read.
/// </summary>
public class NodeAuthMiddleware(RequestDelegate next)
{
    public const string HttpContextItemKey = "Node";

    public async Task InvokeAsync(HttpContext context, INodeService nodeService)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/nodes/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/nodes/register", StringComparison.OrdinalIgnoreCase))
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

        var nodeId = token[..separator];
        var secret = token[(separator + 1)..];

        // Normalized to a URL-ready host (IPv4-mapped unwrapped, IPv6 bracketed) before it's stored as
        // Node.LastIpAddress — every media-proxy path builds a plain "http://{ip}:{port}/..." URL
        // straight from that stored value. See IpAllowListPolicy.ToUrlHost's own doc comment.
        var remoteIp = IpAllowListPolicy.ToUrlHost(context.Connection.RemoteIpAddress);
        var node = await nodeService.AuthenticateAsync(nodeId, secret, remoteIp, context.RequestAborted);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[HttpContextItemKey] = node;
        await next(context);
    }
}

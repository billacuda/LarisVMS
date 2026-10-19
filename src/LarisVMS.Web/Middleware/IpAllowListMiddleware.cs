using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// M20 pass 2: enforces the two IP allow lists (Admin &gt; Settings &gt; Security) — one for
/// management/API traffic, one for live/playback — reusing MediaRoutes.IsMediaPath for the same split
/// PortSegmentationMiddleware already draws between the two. Blank (the default) means unrestricted;
/// see IpAllowListPolicy's own doc comment. Rejected with 404, not 403, matching
/// PortSegmentationMiddleware's own reasoning: a request from outside an allowed range should learn
/// nothing about what exists on the other side of it.
/// </summary>
public class IpAllowListMiddleware(RequestDelegate next)
{
    public const string ManagementSettingKey = "Security.ManagementIpAllowList";
    public const string LiveViewSettingKey = "Security.LiveViewIpAllowList";

    public async Task InvokeAsync(HttpContext context, ISettingsResolver settings)
    {
        if (SetupMiddleware.IsSetupPending(context)) { await next(context); return; }

        // A live ticket hands out a token for direct-to-node video, which never passes this server
        // again, so it's held to the live/playback list too (but isn't a media route: port
        // segmentation keeps it on the page's own port).
        var isLive = MediaRoutes.IsMediaPath(context.Request.Path)
            || context.Request.Path.StartsWithSegments("/api/media/live-ticket", StringComparison.OrdinalIgnoreCase);
        var key = isLive ? LiveViewSettingKey : ManagementSettingKey;
        var raw = await settings.GetRawAsync(key, ct: context.RequestAborted);
        var parsed = IpAllowListPolicy.Parse(raw);

        if (!IpAllowListPolicy.IsAllowed(context.Connection.RemoteIpAddress, parsed.Networks))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}

public static class IpAllowListMiddlewareExtensions
{
    public static IApplicationBuilder UseIpAllowList(this IApplicationBuilder app) =>
        app.UseMiddleware<IpAllowListMiddleware>();
}

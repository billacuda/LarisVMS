using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// M14: when an admin configures a dedicated port for live view and playback traffic
/// (Admin &gt; Settings &gt; Live View, <see cref="SettingKey"/>), separates it from the management
/// interface at the application level — a request for a media route (see <see cref="MediaRoutes"/>)
/// on any port other than the configured one is refused, and a request for anything else on the
/// configured media port is refused too. Refused with 404, not 403 or a redirect: a probe on the
/// wrong port should learn nothing about what exists on the other one.
///
/// This only enforces which port a request must arrive on — it does not itself open the second
/// listening socket. Under self-hosted Kestrel, Program.cs reads this same setting synchronously at
/// startup (before the DI container exists) and opens a matching second Kestrel listener if it's set
/// — see Program.cs's "LiveView.CustomPort" block. Changing the setting takes effect on the next
/// service restart, not immediately. Left unset (the default), every route stays reachable on
/// whatever port the main listener binds, exactly as before this feature existed.
/// </summary>
public class PortSegmentationMiddleware(RequestDelegate next)
{
    public const string SettingKey = "LiveView.CustomPort";

    public async Task InvokeAsync(HttpContext context, ISettingsResolver settings)
    {
        if (SetupMiddleware.IsSetupPending(context)) { await next(context); return; }

        var configuredPort = await settings.GetAsync<int?>(SettingKey, null, ct: context.RequestAborted);
        if (configuredPort is not { } port || port <= 0)
        {
            await next(context);
            return;
        }

        var isMediaPath = MediaRoutes.IsMediaPath(context.Request.Path);
        var onMediaPort = context.Connection.LocalPort == port;

        if (isMediaPath != onMediaPort)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}

public static class PortSegmentationMiddlewareExtensions
{
    public static IApplicationBuilder UsePortSegmentation(this IApplicationBuilder app) =>
        app.UseMiddleware<PortSegmentationMiddleware>();
}

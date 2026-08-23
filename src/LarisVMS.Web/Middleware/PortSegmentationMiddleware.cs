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
/// This only enforces which port a request must arrive on — it does not itself open a new listening
/// socket. Under IIS in-process hosting, the actual socket bindings are IIS's own site bindings, set
/// up separately (IIS Manager or an extra <c>New-WebBinding</c> in deploy.ps1/the setup docs); this
/// middleware assumes that binding already exists once an admin sets the port here, the same way
/// every other IIS-hosted-app port story works. Left unset (the default), every route stays reachable
/// on whatever port(s) IIS already binds, exactly as before this feature existed.
/// </summary>
public class PortSegmentationMiddleware(RequestDelegate next)
{
    public const string SettingKey = "LiveView.CustomPort";

    public async Task InvokeAsync(HttpContext context, ISettingsResolver settings)
    {
        var configuredPort = await settings.GetAsync<int?>(SettingKey, null);
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

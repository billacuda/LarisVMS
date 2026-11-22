using System.Security.Claims;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>
/// Audits every successful change (anything but GET/HEAD) made through a route group, as
/// "METHOD /path" — for camera configuration APIs (zones, privacy masks among them, speed traps,
/// motion region, event tags, schedules) whose handlers don't write their own audit entry, so
/// removing a privacy mask or a recording schedule leaves a record of who did it.
/// </summary>
public static class AuditChangesFilter
{
    public static RouteGroupBuilder AuditChanges(this RouteGroupBuilder builder, string action) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var result = await next(ctx);
            var http = ctx.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) return result;
            var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;
            if (status >= 400) return result;

            await http.RequestServices.GetRequiredService<IAuditService>().LogAsync(action,
                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, http.User.Identity?.Name,
                http.Connection.RemoteIpAddress?.ToString(), $"{http.Request.Method} {http.Request.Path}", http.RequestAborted);
            return result;
        });
}

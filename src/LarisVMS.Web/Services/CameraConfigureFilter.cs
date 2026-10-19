using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Endpoint filters for camera configuration APIs (zones, privacy, speed traps, motion region, event
/// tags, schedules, map placements). Cameras.Edit is the coarse gate on these routes; this adds
/// per-camera Configure access, the same check the device-settings API makes, so a Technician scoped
/// to one group can't edit other cameras by id.
/// </summary>
public static class CameraConfigureFilter
{
    /// <summary>For routes with a <c>{cameraId}</c> segment.</summary>
    public static RouteHandlerBuilder RequireCameraConfigure(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter((ctx, next) => CheckAsync(ctx, next, FromRoute));

    public static RouteGroupBuilder RequireCameraConfigure(this RouteGroupBuilder builder) =>
        builder.AddEndpointFilter((ctx, next) => CheckAsync(ctx, next, FromRoute));

    /// <summary>For <c>/{id}</c> routes of a camera's child row: <paramref name="ownerOf"/> finds the
    /// row's camera (null when there's no such row — the handler then answers 404 itself).</summary>
    public static RouteGroupBuilder RequireCameraConfigure(this RouteGroupBuilder builder,
        Func<ApplicationDbContext, Guid, CancellationToken, Task<Guid?>> ownerOf) =>
        builder.AddEndpointFilter((ctx, next) => CheckAsync(ctx, next, async http =>
        {
            if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id)) return null;
            var db = http.RequestServices.GetRequiredService<ApplicationDbContext>();
            return await ownerOf(db, id, http.RequestAborted);
        }));

    /// <summary>For a <c>{cameraId}</c> route that needs another per-camera action (e.g. Playback).</summary>
    public static RouteHandlerBuilder RequireCameraAccess(this RouteHandlerBuilder builder, CameraAccessActions action) =>
        builder.AddEndpointFilter((ctx, next) => CheckAsync(ctx, next, FromRoute, action));

    public static Task<Guid?> ZoneOwner(ApplicationDbContext db, Guid id, CancellationToken ct) =>
        db.Zones.AsNoTracking().Where(z => z.Id == id).Select(z => (Guid?)z.CameraId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> SpeedTrapOwner(ApplicationDbContext db, Guid id, CancellationToken ct) =>
        db.SpeedTraps.AsNoTracking().Where(t => t.Id == id).Select(t => (Guid?)t.CameraId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> EventTagRuleOwner(ApplicationDbContext db, Guid id, CancellationToken ct) =>
        db.EventTagRules.AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.CameraId).FirstOrDefaultAsync(ct);

    public static Task<Guid?> ScheduleWindowOwner(ApplicationDbContext db, Guid id, CancellationToken ct) =>
        db.ScheduleWindows.AsNoTracking().Where(w => w.Id == id).Select(w => (Guid?)w.CameraId).FirstOrDefaultAsync(ct);

    private static Task<Guid?> FromRoute(HttpContext http) =>
        Task.FromResult(Guid.TryParse(http.Request.RouteValues["cameraId"]?.ToString(), out var id) ? id : (Guid?)null);

    private static async ValueTask<object?> CheckAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next,
        Func<HttpContext, Task<Guid?>> cameraOf, CameraAccessActions action = CameraAccessActions.Configure)
    {
        var http = ctx.HttpContext;
        if (await cameraOf(http) is { } cameraId)
        {
            var access = http.RequestServices.GetRequiredService<ICameraAccessService>();
            var allowed = await access.GetAccessibleCameraIdsAsync(http.User, action, http.RequestAborted);
            if (allowed is not null && !allowed.Contains(cameraId))
                return Results.Problem(action == CameraAccessActions.Configure
                    ? "You don't have configuration access to this camera."
                    : "You don't have access to this camera.", statusCode: StatusCodes.Status403Forbidden);
        }
        return await next(ctx);
    }
}

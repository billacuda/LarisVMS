using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>The manual recording routes. They live under /api/v1 so API keys work, and the live tiles
/// call the same routes with a browser session.</summary>
public static class ManualRecordingApi
{
    public static void MapManualRecordingApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/cameras/{cameraId:guid}/recording/start", (HttpContext ctx, Guid cameraId, [AsParameters] Dependencies d, CancellationToken ct) =>
            ChangeOneAsync(ctx, cameraId, start: true, d, ct)).RequireAuthorization("Recording.Manual");

        app.MapPost("/api/v1/cameras/{cameraId:guid}/recording/stop", (HttpContext ctx, Guid cameraId, [AsParameters] Dependencies d, CancellationToken ct) =>
            ChangeOneAsync(ctx, cameraId, start: false, d, ct)).RequireAuthorization("Recording.Manual");

        app.MapPost("/api/v1/recording/start", (HttpContext ctx, ManualRecordingBatchRequest request, [AsParameters] Dependencies d, CancellationToken ct) =>
            ChangeManyAsync(ctx, request, start: true, d, ct)).RequireAuthorization("Recording.Manual");

        app.MapPost("/api/v1/recording/stop", (HttpContext ctx, ManualRecordingBatchRequest request, [AsParameters] Dependencies d, CancellationToken ct) =>
            ChangeManyAsync(ctx, request, start: false, d, ct)).RequireAuthorization("Recording.Manual");

        app.MapGet("/api/v1/recording", async (HttpContext ctx, IManualRecordingService recordings, ICameraAccessService cameraAccess, CancellationToken ct) =>
        {
            var states = await recordings.GetStatesAsync(ct);
            var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.View, ct);
            if (accessible is not null) states = states.Where(s => accessible.Contains(s.CameraId)).ToList();
            return Results.Json(states);
        }).RequireAuthorization("Cameras.View");
    }

    /// <summary>Bound from services with [AsParameters], to keep the four routes above readable.</summary>
    public sealed record Dependencies(IManualRecordingService Recordings, ICameraAccessService CameraAccess, IAuditService Audit,
        ApplicationDbContext Db, NodeControlClient NodeControl);

    private static async Task<IResult> ChangeOneAsync(HttpContext ctx, Guid cameraId, bool start, Dependencies d, CancellationToken ct)
    {
        var (result, denied) = await ChangeAsync(ctx, [cameraId], start, d, ct);
        if (denied is not null) return denied;
        if (result!.Skipped.Count > 0)
            return Results.Problem(Loc.T("api.thisCameraCantRecord"), statusCode: StatusCodes.Status409Conflict);
        return Results.Json(result);
    }

    private static async Task<IResult> ChangeManyAsync(HttpContext ctx, ManualRecordingBatchRequest request, bool start, Dependencies d, CancellationToken ct)
    {
        if (request.CameraIds is not { Count: > 0 })
            return Results.Problem(Loc.T("api.chooseAtLeastOneCamera"), statusCode: StatusCodes.Status400BadRequest);
        var (result, denied) = await ChangeAsync(ctx, request.CameraIds, start, d, ct);
        return denied ?? Results.Json(result);
    }

    private static async Task<(ManualRecordingChangeResult? Result, IResult? Denied)> ChangeAsync(
        HttpContext ctx, IReadOnlyCollection<Guid> cameraIds, bool start, Dependencies d, CancellationToken ct)
    {
        var accessible = await d.CameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Record, ct);
        if (accessible is not null && cameraIds.Any(id => !accessible.Contains(id)))
            return (null, Results.Problem(Loc.T("api.youDontHaveRecordAccess"), statusCode: StatusCodes.Status403Forbidden));

        var actor = ctx.User.Identity?.Name;
        var result = start ? await d.Recordings.StartAsync(cameraIds, actor, ct) : await d.Recordings.StopAsync(cameraIds, actor, ct);

        if (result.Changed.Count > 0)
        {
            var cameras = await d.Db.Cameras.AsNoTracking().Where(c => result.Changed.Contains(c.Id))
                .Select(c => new { c.Name, c.NodeId }).ToListAsync(ct);
            await d.Audit.LogAsync(start ? "ManualRecording.Start" : "ManualRecording.Stop", ctx.User.ActorId(), actor,
                ctx.Connection.RemoteIpAddress?.ToString(), string.Join(", ", cameras.Select(c => c.Name)), ct);

            // Not awaited: the node's own 30s poll and the discard grace period cover a failed call.
            var nodeIds = cameras.Where(c => c.NodeId is not null).Select(c => c.NodeId!.Value).Distinct().ToList();
            foreach (var node in await d.Db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id)).ToListAsync(ct))
                _ = d.NodeControl.TriggerReconcileAsync(node, CancellationToken.None);
        }
        return (result, null);
    }
}

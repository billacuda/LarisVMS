using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>The view tree's routes (Pages/Views/Index, view-tree.js): read the trees, and create,
/// rename, copy, move, and delete views and folders. Signed-in browsers only — every rule about who
/// may change what is ViewService's; these map its answers to HTTP and write the audit log.</summary>
public static class ViewTreeApi
{
    public sealed record CreateViewRequest(string? Tree, Guid? FolderId, string? Name);
    public sealed record NameRequest(string? Name);
    public sealed record PlaceRequest(string? Tree, Guid? FolderId);
    public sealed record CreateFolderRequest(string? Tree, Guid? ParentId, string? Name);

    /// <summary>Bound from services with [AsParameters].</summary>
    public sealed record Dependencies(IViewService Views, IAuditService Audit, ApplicationDbContext Db);

    public static void MapViewTreeApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/views").RequireAuthorization("Views.View");

        group.MapGet("/tree", async (HttpContext ctx, [AsParameters] Dependencies d, CancellationToken ct) =>
        {
            if (UserId(ctx) is not { } userId) return Results.Unauthorized();
            var tree = await d.Views.GetTreeAsync(userId, ct);
            // Read fresh rather than from the row cached when the cookie was checked, which may predate
            // a "don't ask again" from earlier on this page.
            var askBeforeMove = UserSessionService.Current(ctx) is not { } sessionId
                || !await d.Db.UserSessions.AnyAsync(s => s.Id == sessionId && s.SkipViewMoveConfirm, ct);
            return Results.Json(new { trees = tree.Trees, askBeforeMove });
        });

        group.MapPost("", (HttpContext ctx, CreateViewRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "View.Created", async userId =>
            {
                var view = await d.Views.CreateAsync(r.Name ?? "", userId, r.Tree ?? ViewTrees.Private, r.FolderId, ct);
                return (new { id = view.Id, name = view.Name }, $"'{view.Name}' in {r.Tree ?? ViewTrees.Private}");
            }, ct));

        group.MapPatch("/{id:guid}", (HttpContext ctx, Guid id, NameRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "View.Renamed", async userId =>
            {
                var before = await NameOfViewAsync(d, id, ct);
                await d.Views.RenameAsync(id, userId, r.Name ?? "", ct);
                return (null, $"'{before}' to '{await NameOfViewAsync(d, id, ct)}'");
            }, ct));

        group.MapPost("/{id:guid}/copy", (HttpContext ctx, Guid id, PlaceRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "View.Copied", async userId =>
            {
                var copy = await d.Views.CopyAsync(id, userId, r.Tree ?? ViewTrees.Private, r.FolderId, ct);
                return (new { id = copy.Id, name = copy.Name }, $"'{await NameOfViewAsync(d, id, ct)}' as '{copy.Name}' in {r.Tree}");
            }, ct));

        group.MapPost("/{id:guid}/move", (HttpContext ctx, Guid id, PlaceRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "View.Moved", async userId =>
            {
                await d.Views.MoveAsync(id, userId, r.Tree ?? ViewTrees.Private, r.FolderId, ct);
                return (null, $"'{await NameOfViewAsync(d, id, ct)}' to {r.Tree}");
            }, ct));

        group.MapDelete("/{id:guid}", (HttpContext ctx, Guid id, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "View.Deleted", async userId =>
            {
                var name = await NameOfViewAsync(d, id, ct);
                await d.Views.DeleteAsync(id, userId, ct);
                return (null, $"'{name}'");
            }, ct));

        group.MapPost("/folders", (HttpContext ctx, CreateFolderRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "ViewFolder.Created", async userId =>
            {
                var folder = await d.Views.CreateFolderAsync(userId, r.Tree ?? ViewTrees.Private, r.ParentId, r.Name ?? "", ct);
                return (new { id = folder.Id, name = folder.Name }, $"'{folder.Name}' in {r.Tree ?? ViewTrees.Private}");
            }, ct));

        group.MapPatch("/folders/{id:guid}", (HttpContext ctx, Guid id, NameRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "ViewFolder.Renamed", async userId =>
            {
                var before = await NameOfFolderAsync(d, id, ct);
                await d.Views.RenameFolderAsync(id, userId, r.Name ?? "", ct);
                return (null, $"'{before}' to '{await NameOfFolderAsync(d, id, ct)}'");
            }, ct));

        group.MapPost("/folders/{id:guid}/move", (HttpContext ctx, Guid id, PlaceRequest r, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "ViewFolder.Moved", async userId =>
            {
                await d.Views.MoveFolderAsync(id, userId, r.Tree ?? ViewTrees.Private, r.FolderId, ct);
                return (null, $"'{await NameOfFolderAsync(d, id, ct)}' to {r.Tree}");
            }, ct));

        group.MapDelete("/folders/{id:guid}", (HttpContext ctx, Guid id, [AsParameters] Dependencies d, CancellationToken ct) =>
            RunAsync(ctx, d, "ViewFolder.Deleted", async userId =>
            {
                var name = await NameOfFolderAsync(d, id, ct);
                await d.Views.DeleteFolderAsync(id, userId, ct);
                return (null, $"'{name}' (its contents moved up a level)");
            }, ct));

        // "Don't ask again until I sign out", for moves within one tree: kept on this sign-in only.
        group.MapPost("/move-confirm-off", async (HttpContext ctx, ApplicationDbContext db, CancellationToken ct) =>
        {
            if (UserSessionService.Current(ctx) is not { } sessionId) return Results.NoContent();
            await db.UserSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SkipViewMoveConfirm, true), ct);
            return Results.NoContent();
        });
    }

    private static string? UserId(HttpContext ctx) => ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static async Task<string?> NameOfViewAsync(Dependencies d, Guid id, CancellationToken ct) =>
        await d.Db.Views.AsNoTracking().Where(v => v.Id == id).Select(v => v.Name).FirstOrDefaultAsync(ct);

    private static async Task<string?> NameOfFolderAsync(Dependencies d, Guid id, CancellationToken ct) =>
        await d.Db.ViewFolders.AsNoTracking().Where(f => f.Id == id).Select(f => f.Name).FirstOrDefaultAsync(ct);

    /// <summary>Runs one change: 400 with the message for a broken rule, 403 for missing rights,
    /// otherwise the result (or 204) and an audit entry.</summary>
    private static async Task<IResult> RunAsync(HttpContext ctx, Dependencies d, string action,
        Func<string, Task<(object? Result, string Details)>> change, CancellationToken ct)
    {
        if (UserId(ctx) is not { } userId) return Results.Unauthorized();
        try
        {
            var (result, details) = await change(userId);
            await d.Audit.LogAsync(action, userId, ctx.User.Identity?.Name, ctx.Connection.RemoteIpAddress?.ToString(), details, ct);
            return result is null ? Results.NoContent() : Results.Json(result);
        }
        catch (ViewTreeException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}

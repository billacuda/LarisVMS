using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Polls for Queued ExportJobItems and dispatches each to its camera's current node — the Web
/// tier's first BackgroundService and first async job (see the multi-camera export feature's design
/// notes for why: a job can span far more footage than a synchronous request should hold a browser
/// connection open for).
///
/// Singleton (registered via AddHostedService), but every dependency it actually touches is scoped
/// (IExportService, ApplicationDbContext) — IServiceScopeFactory creates a fresh scope each poll
/// cycle rather than the constructor taking a scoped service directly, which would capture a single
/// DbContext instance for the lifetime of the whole process (the classic scoped-in-singleton bug).
///
/// Loop shape mirrors LarisVMS.Node's StorageManager (the closest existing precedent for "a
/// periodic background task doing file/network I/O and reporting outcomes over an API client"): an
/// unhandled exception from one cycle is logged and the loop keeps going, rather than the whole
/// BackgroundService dying silently.
/// </summary>
public class ExportJobDispatcher(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, ILogger<ExportJobDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(7);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DispatchQueuedAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Export dispatch cycle failed — will retry next cycle.");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DispatchQueuedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var exportService = scope.ServiceProvider.GetRequiredService<IExportService>();
        var timeline = scope.ServiceProvider.GetRequiredService<ITimelineService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var candidates = await exportService.GetQueuedItemsAsync(ct);
        foreach (var candidate in candidates)
        {
            await DispatchOneAsync(candidate, exportService, timeline, db, ct);
        }
    }

    private async Task DispatchOneAsync(ExportDispatchCandidate candidate, IExportService exportService,
        ITimelineService timeline, ApplicationDbContext db, CancellationToken ct)
    {
        if (candidate.NodeId is not { } nodeId)
        {
            await exportService.MarkItemFailedAsync(candidate.ExportItemId, "Camera has no assigned node.", ct);
            return;
        }

        // Same node-connection-info lookup /playback-segment's handler does via
        // GetSegmentForPlaybackAsync, just against a camera's *current* NodeId instead of a
        // specific segment's — there's no existing service method for "one node's connection info
        // by id alone", so this stays a small inline query against the scope's own DbContext rather
        // than growing INodeService for a single caller.
        var node = await db.Nodes.AsNoTracking().Where(n => n.Id == nodeId)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);
        if (node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
        {
            await exportService.MarkItemFailedAsync(candidate.ExportItemId,
                "This camera's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).", ct);
            return;
        }

        var segmentPaths = await timeline.GetSegmentFilePathsAsync(candidate.CameraId, candidate.FromUtc, candidate.ToUtc, ct);
        if (segmentPaths.Count == 0)
        {
            await exportService.MarkItemFailedAsync(candidate.ExportItemId, "No recorded segments overlap the requested range.", ct);
            return;
        }

        var outputFileName = BuildOutputFileName(candidate.CameraName, candidate.CameraId, candidate.FromUtc, candidate.ToUtc);
        var request = new ExportRequest(candidate.ExportItemId, candidate.CameraId, segmentPaths, outputFileName);

        // Marked Running *before* the POST, not after — the node is expected to return 202 quickly
        // and may finish (and report completion) before this method would otherwise get around to
        // it, which would let ApplyCompletionReportAsync's write race this one. Awaited to
        // completion ahead of the POST below, not run concurrently with it, so that race can't
        // happen. A dispatch failure just below transitions the same row straight to Failed,
        // overwriting this.
        await exportService.MarkItemRunningAsync(candidate.ExportItemId, nodeId, DateTime.UtcNow, ct);

        var token = MediaToken.IssueForExport(candidate.CameraId, candidate.ExportItemId, key, TimeSpan.FromSeconds(30));
        var nodeUri = $"http://{ip}:{port}/export/{candidate.CameraId}?token={Uri.EscapeDataString(token)}";

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(25);
        try
        {
            var response = await client.PostAsJsonAsync(nodeUri, request, ct);
            if (!response.IsSuccessStatusCode)
            {
                await exportService.MarkItemFailedAsync(candidate.ExportItemId,
                    $"Node rejected the export request (HTTP {(int)response.StatusCode}).", ct);
            }
            // Success (202): the node runs ffmpeg in the background and reports back to
            // /api/nodes/exports/complete on its own — nothing more to do here.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not reach node {NodeId} to dispatch export item {ItemId} — marking failed.", nodeId, candidate.ExportItemId);
            await exportService.MarkItemFailedAsync(candidate.ExportItemId, $"Could not reach recorder node: {ex.Message}", ct);
        }
    }

    /// <summary>Sanitized for filesystem safety: keeps only alphanumerics/dash/underscore from the
    /// camera name so one containing slashes, colons, or other filesystem-hostile characters can't
    /// produce an invalid (or path-traversing) file name — the node independently re-validates this
    /// too (defense in depth, not a substitute for it) since it's the side that actually does
    /// Path.Combine with it.</summary>
    internal static string BuildOutputFileName(string cameraName, Guid cameraId, DateTime fromUtc, DateTime toUtc)
    {
        var safeName = new string(cameraName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safeName.Length == 0) safeName = cameraId.ToString("N");
        return $"{safeName}_{fromUtc:yyyyMMddTHHmmss}_{toUtc:yyyyMMddTHHmmss}.mp4";
    }
}

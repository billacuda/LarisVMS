using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Polls for Queued ExportJobItems and dispatches each to the node its segments actually live on —
/// normally the camera's current node, except when the requested range spans a reassignment, in
/// which case the item is first split into one node-pinned item per node involved (see
/// SplitItemAcrossNodesAsync) so each produces its own downloadable file instead of the whole export
/// failing outright. The Web tier's first BackgroundService and first async job (see the
/// multi-camera export feature's design notes for why: a job can span far more footage than a
/// synchronous request should hold a browser connection open for).
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

        var segments = await timeline.GetSegmentFilePathsAsync(candidate.CameraId, candidate.FromUtc, candidate.ToUtc, ct);
        if (segments.Count == 0)
        {
            await exportService.MarkItemFailedAsync(candidate.ExportItemId, "No recorded segments overlap the requested range.", ct);
            return;
        }

        // A camera reassigned to a different node mid-range leaves its older footage sitting on the
        // node it was recorded on, not the camera's current one (same split GetConfigAsync's
        // OrphanedCameras handles for retention/warnings — see 0.62.0). A normal (non-pinned) item
        // targets the camera's current node; if any segment lives elsewhere, this one item is
        // replaced with one fresh, node-pinned item per node actually involved (0.68.0) rather than
        // failing outright.
        //
        // SplitItemAcrossNodesAsync doesn't narrow FromUtc/ToUtc per item — all its items still read
        // the parent job's full range — so a pinned item's query above returns every node's segments
        // for that range, not just its own slice. Filter down to nodeId here instead of re-running
        // the multi-node check against the unfiltered set (which would just find >1 node again and
        // hard-fail every split item on its very next dispatch).
        if (candidate.IsPinnedToNode)
        {
            segments = segments.Where(s => s.NodeId == nodeId).ToList();
            if (segments.Count == 0)
            {
                await exportService.MarkItemFailedAsync(candidate.ExportItemId,
                    "Internal error: this export's segments no longer match the node it was pinned to.", ct);
                return;
            }
        }
        else
        {
            var distinctNodeIds = segments.Select(s => s.NodeId).Distinct().ToList();
            if (distinctNodeIds.Count > 1 || distinctNodeIds[0] != nodeId)
            {
                await exportService.SplitItemAcrossNodesAsync(candidate.ExportItemId, distinctNodeIds, ct);
                return;
            }
        }

        // Same node-connection-info lookup /playback-segment's handler does via
        // GetSegmentForPlaybackAsync, just against a camera's *current* NodeId instead of a
        // specific segment's — there's no existing service method for "one node's connection info
        // by id alone", so this stays a small inline query against the scope's own DbContext rather
        // than growing INodeService for a single caller. Name is included so a split job's several
        // per-node output files get distinct, self-describing filenames (see BuildOutputFileName).
        var node = await db.Nodes.AsNoTracking().Where(n => n.Id == nodeId)
            .Select(n => new { n.Name, n.LastIpAddress, n.LivePort, n.MediaSigningKey })
            .FirstOrDefaultAsync(ct);
        if (node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
        {
            await exportService.MarkItemFailedAsync(candidate.ExportItemId,
                "This camera's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).", ct);
            return;
        }

        var segmentPaths = segments.Select(s => s.FilePath).ToList();
        var outputFileName = BuildOutputFileName(candidate.CameraName, candidate.CameraId, node.Name, candidate.FromUtc, candidate.ToUtc);
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
    /// camera/node names so either one containing slashes, colons, or other filesystem-hostile
    /// characters can't produce an invalid (or path-traversing) file name — the node independently
    /// re-validates this too (defense in depth, not a substitute for it) since it's the side that
    /// actually does Path.Combine with it. nodeName is always included (not just for a
    /// SplitItemAcrossNodesAsync item) so a job that later turns out to span a reassignment doesn't
    /// produce two identically-named downloads — see 0.68.0's per-node export split.</summary>
    internal static string BuildOutputFileName(string cameraName, Guid cameraId, string nodeName, DateTime fromUtc, DateTime toUtc)
    {
        static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

        var safeName = Sanitize(cameraName);
        if (safeName.Length == 0) safeName = cameraId.ToString("N");
        var safeNode = Sanitize(nodeName);

        return safeNode.Length == 0
            ? $"{safeName}_{fromUtc:yyyyMMddTHHmmss}_{toUtc:yyyyMMddTHHmmss}.mp4"
            : $"{safeName}_{safeNode}_{fromUtc:yyyyMMddTHHmmss}_{toUtc:yyyyMMddTHHmmss}.mp4";
    }
}

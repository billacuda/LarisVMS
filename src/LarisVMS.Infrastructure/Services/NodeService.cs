using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Server side of the node control plane. Auth mirrors dploid's AgentAuthMiddleware: the node
/// presents "{nodeId}:{secret}", the secret is SHA-256 hashed and compared with
/// CryptographicOperations.FixedTimeEquals against the stored hash, with PreviousApiKeyHash as a
/// fallback slot for a future rotation flow (not wired up yet — see Node's doc comment).
/// </summary>
public class NodeService(ApplicationDbContext db, ISettingsResolver settings) : INodeService
{
    // One gate per node for RecordMotionSpansAsync: its check-then-insert/coalesce is not atomic
    // across concurrent report calls, and the node's own flush loop can land two here at once — a
    // batch retried after an HTTP timeout while the first call is still running, or a shutdown flush
    // overlapping the periodic one. Both arrive with their own scoped DbContext, both pass the
    // "does this span exist?" check, both INSERT, and the filtered unique index rejects the second.
    // Motion-span recording is a low-frequency, 15s-batched path, so a per-node mutex costs nothing.
    // Static because NodeService is registered scoped (one instance per request).
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _recordSpansGates = new();
    public async Task<List<Node>> ListAsync(CancellationToken ct = default)
        => await db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct);

    public async Task<NodeRegisterResponse> RegisterAsync(NodeRegisterRequest request, CancellationToken ct = default)
    {
        var expectedKey = await settings.GetRawAsync("Node.RegistrationKey", ct: ct);
        // FixedTimeEquals, not !=, matching this class's own AuthenticateAsync below (see this
        // class's doc comment) — a plain string comparison short-circuits on the first differing
        // byte, which leaks how many leading characters of the registration key a guess got right
        // through the response timing.
        if (string.IsNullOrEmpty(expectedKey) || !SecretHash.FixedTimeEquals(request.RegistrationKey ?? "", expectedKey))
            throw new UnauthorizedAccessException("Invalid registration key.");

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Name = request.Hostname,
            ApiKeyHash = SecretHash.Hash(secret),
            // Signs the short-lived media tokens LarisVMS.Web issues for live view (M5); the node
            // needs its own copy to validate a token locally with no DB round trip, so — same as
            // the bearer secret — it's handed back once in NodeRegisterResponse and persisted
            // client-side in node.config, never re-sent after this.
            MediaSigningKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Version = request.Version,
            Platform = request.Platform,
            Status = NodeStatus.Active,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        db.Nodes.Add(node);
        await db.SaveChangesAsync(ct);

        return new NodeRegisterResponse(node.Id, secret, node.MediaSigningKey);
    }

    public async Task<Node?> AuthenticateAsync(string nodeId, string secret, string? remoteIp, CancellationToken ct = default)
    {
        if (!Guid.TryParse(nodeId, out var id)) return null;

        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null) return null;

        var matchesCurrent = SecretHash.Matches(secret, node.ApiKeyHash);
        var matchesPrevious = node.PreviousApiKeyHash is not null && SecretHash.Matches(secret, node.PreviousApiKeyHash);
        if (!matchesCurrent && !matchesPrevious) return null;

        node.LastSeenAt = DateTime.UtcNow;
        node.Status = NodeStatus.Active;
        if (remoteIp is not null) node.LastIpAddress = remoteIp;
        await db.SaveChangesAsync(ct);

        return node;
    }

    public async Task<NodeConfigResponse> GetConfigAsync(Guid nodeId, CancellationToken ct = default)
    {
        var cameras = await db.Cameras
            .Where(c => c.NodeId == nodeId && c.IsEnabled)
            .Include(c => c.Streams)
            .Include(c => c.Capabilities)
            .ToListAsync(ct);

        // Per-node override (a node writing to its own local disk, say) takes priority over the
        // global default — see Node.StorageRootPath's doc comment.
        var nodeStorageRoot = await db.Nodes.Where(n => n.Id == nodeId).Select(n => n.StorageRootPath).FirstOrDefaultAsync(ct);
        var storageRoot = string.IsNullOrWhiteSpace(nodeStorageRoot)
            ? await settings.GetRawAsync("Storage.RootPath", ct: ct)
            : nodeStorageRoot;

        // Lazily backfilled here, not just generated at registration: a node that registered before
        // M5 (live view) shipped has none, and re-registering to get one would mean a brand new
        // NodeId — orphaning the old one along with its camera assignments and storage override.
        // Handing it out through the config the node already polls every 30s means an existing node
        // self-heals within one reconcile cycle, no re-registration or restart needed.
        var mediaSigningKey = await db.Nodes.Where(n => n.Id == nodeId).Select(n => n.MediaSigningKey).FirstOrDefaultAsync(ct);
        if (mediaSigningKey is null)
        {
            mediaSigningKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await db.Nodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(
                u => u.SetProperty(n => n.MediaSigningKey, mediaSigningKey), ct);
        }

        var watermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90, ct: ct);
        // M18: the admin-facing toggle for adaptive streaming (see Admin/Settings/LiveView.cshtml).
        // Global only, no per-node/per-camera override — this is a bandwidth/CPU trade-off for the
        // whole deployment, not something that makes sense to vary camera-by-camera.
        var adaptiveStreamingEnabled = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false, ct: ct);

        // Object detection plan decision 2: purely a per-node admin override (Node.AiAccelerator),
        // not resolved through ISettingsResolver's global/node/camera chain the way the settings
        // above are — a node's own hardware is a fact about that specific machine, not something a
        // global default or camera-scoped override makes sense for. Null (never configured) reads
        // as "Auto", the same "resolve automatically" meaning the enum's own Auto value carries.
        var aiAccelerator = await db.Nodes.Where(n => n.Id == nodeId).Select(n => n.AiAccelerator).FirstOrDefaultAsync(ct);

        // Object detection plan decisions 7/2: global detection policy, resolved once here the
        // same way WatermarkPercent/AdaptiveStreamingEnabled already are — see NodeConfigResponse's
        // own doc comments for why these are deployment-wide rather than per-camera settings.
        var reportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false, ct: ct);
        var aiIdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10, ct: ct);
        // Node-scoped (Global -> Node, no per-camera override) — one Vision Service process serves
        // every camera on a node from the same loaded model, see NodeConfigResponse.DetectionModelFamily's
        // own doc comment for why that makes this a per-node choice rather than a per-camera one.
        // AspectMode joins it here for pass 1 of the detection/hardware-acceleration overhaul,
        // replacing the old global Detection.Width/Detection.Height settings (a single decode
        // resolution shared by every camera) — decode resolution is now derived per camera from its
        // own real Sub-stream dimensions instead (NodeConfigStreamDto.Width/Height, below).
        var aspectMode = await settings.GetAsync("Detection.AspectMode", "Letterbox", nodeId: nodeId, ct: ct);
        var detectionModelFamily = await settings.GetAsync("Detection.ModelFamily", "Auto", nodeId: nodeId, ct: ct);
        var dfineWeights = await settings.GetAsync("Detection.DFineWeights", "Obj2Coco", nodeId: nodeId, ct: ct);
        // YOLOX model size — same node-scoped resolution; only meaningful when the family is YoloX.
        var yoloXSize = await settings.GetAsync("Detection.YoloXSize", "S", nodeId: nodeId, ct: ct);
        // Per-camera detection frame-rate ceiling (0 = decode rate). Node-scoped.
        var maxDetectionFps = await settings.GetAsync("Detection.MaxFps", 10, nodeId: nodeId, ct: ct);
        // Detection/hardware-acceleration overhaul, pass 3b — same node-scoped resolution as
        // AspectMode/DetectionModelFamily above.
        var enableHighResReDetection = await settings.GetAsync("Detection.EnableHighResReDetection", false, nodeId: nodeId, ct: ct);
        // Diagnostic-only: whether Vision Service writes its per-trigger cropped JPEGs to
        // logs\vision-debug\. Same Global -> Node resolution as the flags above. Defaults false
        // (opt-in) — an existing install that wants it keeps a global Setting row = 'true' (seeded
        // by the 0.168.0 migration); a fresh install starts with it off.
        var enableVisionDebugImages = await settings.GetAsync("Detection.EnableVisionDebugImages", false, nodeId: nodeId, ct: ct);
        // Pass 4a — same Global -> Node resolution as the flags above. Moves per-frame preprocessing
        // off the CPU onto whatever accelerator ONNX Runtime is using. Opt-in, default off.
        var gpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false, nodeId: nodeId, ct: ct);
        // Pass F — same Global -> Node resolution. Decode the Sub stream at up to native resolution
        // so the eager AI-detection snapshot crop is sharper. Opt-in, default off.
        var hiResSnapshots = await settings.GetAsync("Detection.HiResSnapshots", false, nodeId: nodeId, ct: ct);
        // Deployment-wide minimum log level for nodes + their vision services. Global only.
        var logLevel = await settings.GetAsync("Logging.Level", "Information", ct: ct);
        // Confidence/IoU/stream-role are resolved per camera below (Camera -> Node -> Global,
        // same chain RetentionDays/RecordingMode already use) — not read once globally here like
        // the settings above, since a specific camera can need its own threshold or stream choice.

        var cameraIds = cameras.Select(c => c.Id).ToList();
        // M18: Privacy joins ServerMotion/Ignore here — RecordingSession's own privacy-mask burn-in
        // needs it the same way MotionSession needs the other two. CameraMotion is still excluded:
        // nothing pushes it to a device yet (see ZoneKind's own doc comment), so the node has no use
        // for it.
        var zonesByCamera = await db.Zones
            .Where(z => cameraIds.Contains(z.CameraId) && z.IsEnabled
                && (z.Kind == ZoneKind.ServerMotion || z.Kind == ZoneKind.Ignore || z.Kind == ZoneKind.Privacy))
            .ToListAsync(ct);
        var zonesLookup = zonesByCamera.ToLookup(z => z.CameraId);

        // M8 pass 8: only enabled rules, same as zones above — the node has no use for a disabled
        // one and no reason to know it exists.
        var eventTagRulesByCamera = await db.EventTagRules
            .Where(r => cameraIds.Contains(r.CameraId) && r.IsEnabled)
            .ToListAsync(ct);
        var eventTagRulesLookup = eventTagRulesByCamera.ToLookup(r => r.CameraId);

        // Only enabled windows, same as zones/rules above — a disabled window has nothing for the
        // node to act on.
        var scheduleWindowsByCamera = await db.ScheduleWindows
            .Where(w => cameraIds.Contains(w.CameraId) && w.IsEnabled)
            .ToListAsync(ct);
        var scheduleWindowsLookup = scheduleWindowsByCamera.ToLookup(w => w.CameraId);

        var cameraDtos = new List<NodeConfigCameraDto>();
        foreach (var c in cameras)
        {
            // 0 or negative means "keep forever" — an explicit admin choice, not "unset". Unset
            // (no override anywhere) falls through to the 30-day compiled-in default below.
            var retentionDays = await settings.GetAsync<int?>("Retention.Days", 30, cameraId: c.Id, nodeId: nodeId, ct: ct);
            var recordingMode = await settings.GetAsync("Recording.Mode", "Continuous", cameraId: c.Id, nodeId: nodeId, ct: ct);
            var motionPreRollSeconds = await settings.GetAsync("Recording.MotionPreRollSeconds", 10, cameraId: c.Id, nodeId: nodeId, ct: ct);
            var motionPostRollSeconds = await settings.GetAsync("Recording.MotionPostRollSeconds", 30, cameraId: c.Id, nodeId: nodeId, ct: ct);
            // Clamped, not trusted outright: this value is baked directly into ffmpeg's own
            // `-f segment` invocation (RecordingSession.BuildTeeOutputs) with no other validation
            // downstream. A stray 0/negative would make ffmpeg either reject the argument or (worse)
            // roll a new file continuously; an enormous value would defeat the whole point of this
            // setting (bounding worst-case seek latency) while still looking "set".
            var segmentSeconds = Math.Clamp(
                await settings.GetAsync("Recording.SegmentSeconds", 60, cameraId: c.Id, nodeId: nodeId, ct: ct), 5, 300);
            var aiConfidence = await settings.GetAsync("Detection.Confidence", 0.35, cameraId: c.Id, nodeId: nodeId, ct: ct);
            var aiIou = await settings.GetAsync("Detection.Iou", 0.5, cameraId: c.Id, nodeId: nodeId, ct: ct);
            var aiDetectionStreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub", cameraId: c.Id, nodeId: nodeId, ct: ct);
            // Corrects a camera that misreports its watch stream's orientation (a corridor-mounted
            // device advertising 704x480 while delivering 480x704) before the node builds the
            // detection profile from it — see LarisVMS.Node.DetectionOrientation.
            var aiDetectionOrientation = await settings.GetAsync("AiDetection.Orientation", "Auto", cameraId: c.Id, nodeId: nodeId, ct: ct);
            cameraDtos.Add(new NodeConfigCameraDto(
                c.Id, c.Name, c.Username, c.Password,
                c.Streams.Where(s => s.IsEnabled).Select(s => new NodeConfigStreamDto(
                    s.Id, s.Role.ToString(), s.RtspUri, s.Codec, s.Width, s.Height, s.HasAudio, s.Fps)).ToList(),
                retentionDays, c.QuotaBytes,
                zonesLookup[c.Id].Select(z => new NodeConfigZoneDto(z.Id, z.Kind.ToString(), z.PolygonJson, z.Sensitivity)).ToList(),
                recordingMode, motionPreRollSeconds, motionPostRollSeconds,
                ResolveEventsServiceUri(c.Capabilities),
                eventTagRulesLookup[c.Id].Select(r => new NodeConfigEventTagRuleDto(r.Id, r.StartTopic, r.StopTopic, r.DrivesRecording)).ToList(),
                scheduleWindowsLookup[c.Id].Select(w => new NodeConfigScheduleWindowDto(w.Id, w.Days.ToString(), w.StartTime, w.EndTime)).ToList(),
                // Only sent when a provider is actually registered for the stored key, so a key left
                // behind by a removed/newer provider quietly means "no integration" rather than
                // asking the node to start something it can't resolve.
                CameraIntegrations.ByKey(c.IntegrationKey)?.Key,
                ResolveIntegrationBaseUri(c), segmentSeconds,
                c.AiDetectionEnabled, c.MotionDetectionSource?.ToString(),
                aiConfidence, aiIou, aiDetectionStreamRole, aiDetectionOrientation, c.ServerMotionEnabled,
                c.MotionRegionMode.ToString(), c.MotionGridSize, c.MotionGridMask, c.MotionGridSensitivity));
        }

        // Cameras this node has leftover Segments for but doesn't currently record — reassigned to a
        // different node, or deleted outright. Without this, StorageManager's orphaned-folder sweep
        // has no RetentionDays to honor for them at all (the camera simply isn't in `cameras` above),
        // which is exactly the gap that let old footage accumulate on a node forever after a camera
        // moved away — see StorageManager.SweepOrphanedCameraFolders' own doc comment.
        var assignedCameraIds = cameras.Select(c => c.Id).ToList();
        var orphanedCameraIds = await db.Segments
            .Where(s => s.NodeId == nodeId && !assignedCameraIds.Contains(s.CameraId))
            .Select(s => s.CameraId)
            .Distinct()
            .ToListAsync(ct);

        var orphanedCameraDtos = new List<NodeConfigOrphanedCameraDto>();
        foreach (var orphanedCameraId in orphanedCameraIds)
        {
            // Same resolution chain as every assigned camera's own RetentionDays above, scoped to
            // *this* node — a per-node override this node had for that camera (set back when it was
            // still assigned here) still applies to aging out its leftover copy.
            var retentionDays = await settings.GetAsync<int?>("Retention.Days", 30, cameraId: orphanedCameraId, nodeId: nodeId, ct: ct);
            orphanedCameraDtos.Add(new NodeConfigOrphanedCameraDto(orphanedCameraId, retentionDays));
        }

        return new NodeConfigResponse(cameraDtos, storageRoot, watermarkPercent, mediaSigningKey, orphanedCameraDtos,
            adaptiveStreamingEnabled, (aiAccelerator ?? AiAccelerator.Auto).ToString(),
            reportIdleDetections, aspectMode, detectionModelFamily, dfineWeights, aiIdleTimeoutSeconds,
            enableHighResReDetection, enableVisionDebugImages, gpuPreprocessing, logLevel)
        { YoloXSize = yoloXSize, MaxDetectionFps = maxDetectionFps, HiResSnapshots = hiResSnapshots };
    }

    /// <summary>Pulls the Events service's own XAddr out of the capability prober's raw category map
    /// — same source/pattern CameraService.ReplaceStreamsAsync already uses for the Media XAddr.
    /// Null whenever there's nothing to resolve: no probe yet, an old probe from before HasEvents
    /// existed, or a device that genuinely doesn't advertise an Events service at all.</summary>
    /// <summary>The camera's own scheme+host+port for a vendor plugin to build its API calls against.
    /// Derived from DeviceServiceUri (the one address a camera is guaranteed to have — it's how it
    /// was added) rather than Host/OnvifPort, so a camera reached over https, or on a non-default
    /// port, keeps working. Null when there's no integration to serve or the stored URI isn't
    /// parseable.</summary>
    private static string? ResolveIntegrationBaseUri(Camera camera)
    {
        if (CameraIntegrations.ByKey(camera.IntegrationKey) is null) return null;
        if (!Uri.TryCreate(camera.DeviceServiceUri, UriKind.Absolute, out var deviceUri)) return null;
        return $"{deviceUri.Scheme}://{deviceUri.Authority}";
    }

    private static string? ResolveEventsServiceUri(CameraCapabilities? capabilities)
    {
        if (capabilities is not { HasEvents: true, RawProbeJson: { } json }) return null;
        try
        {
            var rawXAddrs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return rawXAddrs?.GetValueOrDefault("Events");
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupted or pre-M8 RawProbeJson shape — treat as unresolvable, not fatal
        }
    }

    public async Task DeleteSegmentsAsync(Guid nodeId, IReadOnlyList<string> filePaths, CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return;
        await db.Segments
            .Where(s => s.NodeId == nodeId && filePaths.Contains(s.FilePath))
            .ExecuteDeleteAsync(ct);
    }

    public async Task<List<string>> ListSegmentFilePathsAsync(Guid nodeId, CancellationToken ct = default)
        => await db.Segments.AsNoTracking().Where(s => s.NodeId == nodeId).Select(s => s.FilePath).ToListAsync(ct);

    public async Task<List<long>> ListMotionSpanIdsAsync(Guid nodeId, CancellationToken ct = default)
        => await db.MotionSpans.AsNoTracking().Where(m => m.Camera.NodeId == nodeId).Select(m => m.Id).ToListAsync(ct);

    /// <summary>Pure so it's unit-testable without a database — same "isolate the arithmetic from the
    /// I/O" shape as TimelineService.NormalizeToUtc. Null input (an older node build not sending
    /// SentAtUtc yet) means "no measurement this heartbeat," not "zero skew."</summary>
    internal static double? ComputeClockSkewSeconds(DateTime? nodeSentAtUtc, DateTime serverReceivedUtc)
        => nodeSentAtUtc is { } sentAt ? (serverReceivedUtc - sentAt).TotalSeconds : null;

    public async Task RecordHeartbeatAsync(Guid nodeId, long? freeBytes, long? totalBytes, string? version, int? livePort,
        DateTime? nodeSentAtUtc, DateTime serverReceivedUtc, List<string>? detectedEncoders = null,
        List<string>? detectedAccelerators = null, CancellationToken ct = default)
    {
        var skew = ComputeClockSkewSeconds(nodeSentAtUtc, serverReceivedUtc);
        var encodersJson = detectedEncoders is not null ? System.Text.Json.JsonSerializer.Serialize(detectedEncoders) : null;
        // Object detection plan decision 2: same coalesce-preserve shape as encodersJson above.
        var acceleratorsJson = detectedAccelerators is not null ? System.Text.Json.JsonSerializer.Serialize(detectedAccelerators) : null;

        await db.Nodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.StorageFreeBytes, freeBytes)
            .SetProperty(n => n.StorageTotalBytes, totalBytes)
            .SetProperty(n => n.StorageStatsUpdatedAt, DateTime.UtcNow)
            .SetProperty(n => n.Version, n => version ?? n.Version)
            .SetProperty(n => n.LivePort, n => livePort ?? n.LivePort)
            .SetProperty(n => n.ClockSkewSeconds, n => skew ?? n.ClockSkewSeconds)
            .SetProperty(n => n.ClockSkewMeasuredAt, n => skew != null ? serverReceivedUtc : n.ClockSkewMeasuredAt)
            .SetProperty(n => n.DetectedEncodersJson, n => encodersJson ?? n.DetectedEncodersJson)
            .SetProperty(n => n.DetectedAcceleratorsJson, n => acceleratorsJson ?? n.DetectedAcceleratorsJson), ct);
    }

    /// <summary>Every field is coalesce-preserve (`item.X ?? s.X`), not a blind overwrite — M11 added
    /// a second, periodic report source (NodeWorker's health tick, Fps/BitrateKbps/ReconnectCount)
    /// that shares this same DTO/pipeline with the original once-per-connection report
    /// (Width/Height/Codec) but doesn't know the other's fields, and neither should be able to null
    /// out what the other one knows. HealthReportedAt only advances when a report actually carries a
    /// health measurement (Fps not null) — a resolution-only report shouldn't make stale health data
    /// look fresh.</summary>
    public async Task UpdateStreamInfoAsync(Guid nodeId, IReadOnlyList<StreamInfoReportItem> items, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            if (!Enum.TryParse<CameraStreamRole>(item.StreamRole, out var role)) continue;

            // Scoped to Camera.NodeId == nodeId, not just CameraId: a report from a node the camera
            // has since been reassigned away from is stale by definition and must not overwrite what
            // the camera's *current* node measured.
            await db.CameraStreams
                .Where(s => s.CameraId == item.CameraId && s.Role == role && s.Camera.NodeId == nodeId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Width, s => item.Width ?? s.Width)
                    .SetProperty(s => s.Height, s => item.Height ?? s.Height)
                    .SetProperty(s => s.Codec, s => item.Codec ?? s.Codec)
                    .SetProperty(s => s.Fps, s => item.Fps ?? s.Fps)
                    .SetProperty(s => s.BitrateKbps, s => item.BitrateKbps ?? s.BitrateKbps)
                    .SetProperty(s => s.ReconnectCount, s => item.ReconnectCount ?? s.ReconnectCount)
                    .SetProperty(s => s.AudioCodec, s => item.AudioCodec ?? s.AudioCodec)
                    .SetProperty(s => s.AudioSampleRateHz, s => item.AudioSampleRateHz ?? s.AudioSampleRateHz)
                    .SetProperty(s => s.HealthReportedAt, s => item.Fps != null ? now : s.HealthReportedAt), ct);
        }
    }

    public async Task<Dictionary<Guid, double?>> GetEstimatedDaysRemainingAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var bytesPerNodeLastDay = await db.Segments
            .Where(s => s.StartUtc >= since)
            .GroupBy(s => s.NodeId)
            .Select(g => new { NodeId = g.Key, Bytes = g.Sum(s => s.SizeBytes) })
            .ToDictionaryAsync(x => x.NodeId, x => x.Bytes, ct);

        var nodes = await db.Nodes.AsNoTracking().Select(n => new { n.Id, n.StorageFreeBytes }).ToListAsync(ct);

        var result = new Dictionary<Guid, double?>();
        foreach (var n in nodes)
        {
            if (n.StorageFreeBytes is not { } free
                || !bytesPerNodeLastDay.TryGetValue(n.Id, out var bytesPerDay)
                || bytesPerDay <= 0)
            {
                result[n.Id] = null;
                continue;
            }

            result[n.Id] = free / (double)bytesPerDay;
        }

        return result;
    }

    public async Task RecordSegmentsAsync(Guid nodeId, IReadOnlyList<SegmentReportItem> segments, CancellationToken ct = default)
    {
        // One recorder node, one 60s segment per camera stream — a handful of rows per minute even
        // at dozens of cameras, nowhere near the volume the plan calls out SqlBulkCopy for
        // (MotionSpans/Detections in M8). Plain inserts are the right amount of engineering here.
        foreach (var item in segments)
        {
            if (!Enum.TryParse<CameraStreamRole>(item.StreamRole, out var role)) continue;

            db.Segments.Add(new Segment
            {
                CameraId = item.CameraId,
                NodeId = nodeId,
                StreamRole = role,
                StartUtc = item.StartUtc,
                EndUtc = item.EndUtc,
                DurationMs = (int)(item.EndUtc - item.StartUtc).TotalMilliseconds,
                FilePath = item.FilePath,
                SizeBytes = item.SizeBytes,
                Codec = item.Codec,
                Width = item.Width,
                Height = item.Height,
                HasAudio = item.HasAudio
            });
        }

        // Most likely failure mode is the FilePath unique constraint — a node that restarted
        // mid-session and rescanned its output directory with no memory of what it already reported
        // (in-session de-duplication lives in RecordingSession's HashSet, which a process restart
        // clears) will occasionally re-send a segment the server already has. One bad row shouldn't
        // cost the rest of the batch: retry, each time detaching exactly the entries EF identifies
        // as the cause of that attempt's failure, until either everything savable is saved or a
        // failure can't be attributed to specific entries (in which case it propagates normally).
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (ex.Entries.Count > 0)
            {
                foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
            }
        }
    }

    public async Task RecordMotionSpansAsync(Guid nodeId, IReadOnlyList<MotionSpanReportItem> spans, CancellationToken ct = default)
    {
        var gate = _recordSpansGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await RecordMotionSpansCoreAsync(spans, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecordMotionSpansCoreAsync(IReadOnlyList<MotionSpanReportItem> spans, CancellationToken ct)
    {
        // Object detection plan decision 5: resolved once per distinct category name actually
        // reported in this batch, not once per item — a busy batch can report the same category
        // (e.g. "Vehicle") many times in one call, and caching avoids redundant round-trips (and,
        // more importantly, redundant find-or-create races) within a single call.
        var categoryCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // How far apart two AI-detection sightings of the same object on the same camera can be and
        // still be treated as one span (B1 coalescing below) — one full idle-timeout gap (the window
        // a track can vanish and resume without opening a new span, Vision-side) plus a few seconds
        // of slack for tracker-ID churn / a pipeline restart landing between two frames.
        var coalesceGap = TimeSpan.FromSeconds(Math.Max(0, await settings.GetAsync("Detection.IdleTimeoutSeconds", 10, ct: ct)) + 5);

        // M8: a still-open span is checkpointed periodically (NodeWorker.EnqueueMotionCheckpoints),
        // not just reported once on close — without upserting, every checkpoint of the same
        // long-running span would pile up as its own row instead of one row that keeps extending.
        // (CameraId, ZoneId, StartUtc) is a natural identity for "the same span": StartUtc never
        // changes once a span is confirmed open (MotionHysteresis backdates it once, at
        // confirmation, and never moves it again), so every checkpoint and the eventual close-report
        // for one span all carry the same triple.
        foreach (var item in spans)
        {
            var detectedObjectCategoryId = item.DetectedObjectCategory is { } categoryName
                ? await ResolveDetectedObjectCategoryIdAsync(categoryName, categoryCache, ct)
                : (Guid?)null;

            // M8 pass 8: EventTagRuleId joins the identity key alongside ZoneId — both the built-in
            // camera-pushed classifier and a custom EventTagRule report with ZoneId=null, so without
            // this a rule's span starting at the same instant as a built-in motion span (or another
            // rule's) would collide with it here and silently steal its checkpoints/EndUtc extension.
            // DetectionKind joins the identity for the same reason EventTagRuleId did: an
            // object-detection span reports with ZoneId and EventTagRuleId both null, exactly like a
            // built-in motion span, so without it a person detected at the same instant as plain
            // motion (the normal case — a camera fires both families for one real event) would
            // collide with it and silently steal its checkpoints. DetectedObjectLabel joins for the
            // identical reason on the AI-detection side: LarisVMS.Vision.Service debounces per raw
            // class name (a Dictionary<string, MotionHysteresis>, mirroring
            // DahuaCgiEventSession's own per-DetectionKind dictionary — both collapse multiple
            // simultaneous instances of the same class into one span, an existing, accepted
            // simplification this doesn't change), so the label is what actually distinguishes,
            // say, a "car" span from a "person" span reported at the same instant — the coarser
            // DetectedObjectCategoryId alone would collide the two together.
            var existing = await db.MotionSpans.FirstOrDefaultAsync(m =>
                m.CameraId == item.CameraId && m.ZoneId == item.ZoneId && m.EventTagRuleId == item.EventTagRuleId
                && m.DetectionKind == item.DetectionKind && m.DetectedObjectLabel == item.DetectedObjectLabel
                && m.StartUtc == item.StartUtc, ct);

            if (existing is not null)
            {
                // Only ever extends forward — a checkpoint racing a slightly-stale retry of an
                // older report should never pull EndUtc backward.
                if (item.EndUtc > existing.EndUtc) existing.EndUtc = item.EndUtc;
                existing.Score = Math.Max(existing.Score, item.Score);
                // Object detection plan decision 10: LarisVMS.Vision.Service's own best-frame
                // tracking is already monotonic (a later report's box is never worse than an
                // earlier one for the same track), so a later checkpoint can just overwrite rather
                // than needing its own comparison here.
                if (item.BestFrameAtUtc is not null)
                {
                    existing.BestFrameAtUtc = item.BestFrameAtUtc;
                    existing.BestBoxX = item.BestBoxX;
                    existing.BestBoxY = item.BestBoxY;
                    existing.BestBoxW = item.BestBoxW;
                    existing.BestBoxH = item.BestBoxH;
                    existing.BestBoxConfidence = item.BestBoxConfidence;
                }
                continue;
            }

            // B1: coalesce fragmented AI-detection spans. Tracker-ID churn, a per-frame label flip
            // (car<->truck), a brief occlusion, or a Vision Service / pipeline restart each make one
            // physical object's sighting close and a new span open a few seconds later with a fresh
            // StartUtc — which surfaces as several near-identical Snapshots cards for one event. If
            // this AI-detection item overlaps (within coalesceGap) an existing span for the same
            // camera + label, extend that span instead of adding a new row.
            if (item.DetectedObjectLabel is not null && item.ZoneId is null
                && item.EventTagRuleId is null && item.DetectionKind is null)
            {
                var windowStart = item.StartUtc - coalesceGap;
                var windowEnd = item.EndUtc + coalesceGap;
                var sibling = await db.MotionSpans
                    .Where(m => m.CameraId == item.CameraId
                        && m.DetectedObjectLabel == item.DetectedObjectLabel
                        && m.ZoneId == null && m.EventTagRuleId == null && m.DetectionKind == null
                        && m.StartUtc <= windowEnd && m.EndUtc >= windowStart)
                    .OrderByDescending(m => m.StartUtc)
                    .FirstOrDefaultAsync(ct);
                if (sibling is not null)
                {
                    // Never move sibling.StartUtc. It is part of the filtered unique index
                    // IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc and the whole
                    // check-then-upsert here (and the periodic checkpoint path above) treats
                    // (CameraId, DetectedObjectLabel, StartUtc) as a stable identity — see that
                    // index's own comment. An earlier fragment backdating the sibling's StartUtc
                    // onto a value another AI-detection row already holds (common when one moving
                    // object produces several overlapping fragments) turns this UPDATE into a
                    // duplicate-key violation, which the SaveChanges catch below then "handles" by
                    // dropping the row — so a busy scene silently stops producing any AI-detection
                    // spans at all. The span already exists; coalescing only needs to keep it from
                    // fragmenting into extra cards, not to nudge its start a few seconds earlier.
                    if (item.EndUtc > sibling.EndUtc) sibling.EndUtc = item.EndUtc;
                    sibling.Score = Math.Max(sibling.Score, item.Score);
                    // Adopt the incoming best frame when it's at least as confident as the one on
                    // record (or the span never had one) — same "a later report is never worse"
                    // assumption the exact-match checkpoint path above already relies on.
                    if (item.BestFrameAtUtc is not null
                        && (sibling.BestBoxConfidence is null || (item.BestBoxConfidence ?? 0) >= sibling.BestBoxConfidence))
                    {
                        sibling.BestFrameAtUtc = item.BestFrameAtUtc;
                        sibling.BestBoxX = item.BestBoxX;
                        sibling.BestBoxY = item.BestBoxY;
                        sibling.BestBoxW = item.BestBoxW;
                        sibling.BestBoxH = item.BestBoxH;
                        sibling.BestBoxConfidence = item.BestBoxConfidence;
                    }
                    continue;
                }
            }

            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = item.CameraId,
                ZoneId = item.ZoneId,
                EventTagRuleId = item.EventTagRuleId,
                // M8 pass 8: a non-null EventTagRuleId always means a custom-tag span regardless of
                // ZoneId (which a custom-tag report never sets anyway) — checked first for that
                // reason. AiDetection is checked next, ahead of the original two-way ZoneId
                // inference below, since an AI-detection report also always has ZoneId=null (it has
                // no zone concept of its own) and would otherwise be misread as CameraEvent. A null
                // ZoneId with no EventTagRuleId or category only ever comes from the built-in
                // camera-pushed classifier, since a ServerMotion span always names the zone that
                // detected it.
                Source = item.EventTagRuleId is not null ? MotionSource.CustomTag
                    : detectedObjectCategoryId is not null ? MotionSource.AiDetection
                    : item.ZoneId is null ? MotionSource.CameraEvent : MotionSource.ServerMotion,
                // Object detections stay Source=CameraEvent (they arrive over the same PullPoint
                // channel from the same classifier) and are distinguished by this instead — see
                // DetectionKind's own doc comment for why the two are separate questions. Always
                // null for an AiDetection-sourced span — see DetectedObjectCategoryId below.
                DetectionKind = item.DetectionKind,
                DetectedObjectCategoryId = detectedObjectCategoryId,
                DetectedObjectLabel = item.DetectedObjectLabel,
                BestFrameAtUtc = item.BestFrameAtUtc,
                BestBoxX = item.BestBoxX,
                BestBoxY = item.BestBoxY,
                BestBoxW = item.BestBoxW,
                BestBoxH = item.BestBoxH,
                BestBoxConfidence = item.BestBoxConfidence,
                StartUtc = item.StartUtc,
                EndUtc = item.EndUtc,
                Score = item.Score
            });
        }

        // Detach-and-retry: a row the batch can't save is dropped (not the whole batch). Two causes
        // in practice —
        //  - an FK violation: a zone/rule/category deleted mid-flight (the *Service.DeleteAsync nulls
        //    matching MotionSpans rows first but can't fix a report already in flight); the row is
        //    genuinely unsaveable, so dropping it is right.
        //  - a unique-index violation on IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc: another
        //    report call already inserted this AI-detection span (the per-node gate above makes that
        //    rare, but a multi-instance web farm, or a future caller, could still race). Here the row
        //    is not junk — merge its forward progress (later EndUtc, better best-frame) into the
        //    committed winner before dropping the duplicate, rather than losing it.
        var attempt = 0;
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (ex.Entries.Count > 0 && ++attempt <= 20)
            {
                var uniqueViolation = ex.InnerException is SqlException { Number: 2601 or 2627 };
                foreach (var entry in ex.Entries)
                {
                    if (uniqueViolation && entry.State == EntityState.Added
                        && entry.Entity is MotionSpan rejected && rejected.DetectedObjectLabel is not null)
                    {
                        var winner = await db.MotionSpans.FirstOrDefaultAsync(m =>
                            m.CameraId == rejected.CameraId
                            && m.DetectedObjectLabel == rejected.DetectedObjectLabel
                            && m.StartUtc == rejected.StartUtc, ct);
                        if (winner is not null) MergeSpanForward(winner, rejected);
                    }
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>Folds one AI-detection span's forward progress into another — later EndUtc, higher
    /// Score, and a best frame that's at least as confident as the one on record. Same monotonic
    /// "a later report is never worse" assumption the checkpoint and coalesce paths in
    /// RecordMotionSpansCoreAsync already rely on. Used when a concurrent report already inserted the
    /// row this one was trying to add.</summary>
    private static void MergeSpanForward(MotionSpan target, MotionSpan incoming)
    {
        if (incoming.EndUtc > target.EndUtc) target.EndUtc = incoming.EndUtc;
        target.Score = Math.Max(target.Score, incoming.Score);
        if (incoming.BestFrameAtUtc is not null
            && (target.BestBoxConfidence is null || (incoming.BestBoxConfidence ?? 0) >= target.BestBoxConfidence))
        {
            target.BestFrameAtUtc = incoming.BestFrameAtUtc;
            target.BestBoxX = incoming.BestBoxX;
            target.BestBoxY = incoming.BestBoxY;
            target.BestBoxW = incoming.BestBoxW;
            target.BestBoxH = incoming.BestBoxH;
            target.BestBoxConfidence = incoming.BestBoxConfidence;
        }
    }

    /// <summary>Find-or-create for a DetectedObjectCategory row, auto-assigning a color only on
    /// create — object detection plan decision 5's "pick one automatically that hasn't been used
    /// yet." <paramref name="cache"/> is scoped to one RecordMotionSpansAsync call, so a batch that
    /// reports the same brand-new category name several times only creates one row (and only
    /// queries the database once) for it.</summary>
    private async Task<Guid> ResolveDetectedObjectCategoryIdAsync(string categoryName, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(categoryName, out var cachedId)) return cachedId;

        var existing = await db.DetectedObjectCategories.FirstOrDefaultAsync(c => c.Name == categoryName, ct);
        if (existing is not null)
        {
            cache[categoryName] = existing.Id;
            return existing.Id;
        }

        var existingColors = await db.DetectedObjectCategories.Select(c => c.ColorHex).ToListAsync(ct);
        var category = new DetectedObjectCategory
        {
            Id = Guid.NewGuid(),
            Name = categoryName,
            ColorHex = DetectedObjectColorAssigner.PickNextColor(existingColors),
            FirstSeenUtc = DateTime.UtcNow
        };
        db.DetectedObjectCategories.Add(category);

        try
        {
            // Saved immediately, not batched with the rest of this call's own SaveChangesAsync, so
            // its Id is committed and visible before any MotionSpan row below references it via FK.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (db.Entry(category).State != EntityState.Detached)
        {
            // Lost a race against another concurrent report creating the same brand-new category
            // name at the same time (two nodes, or two cameras on one node, both seeing a genuinely
            // new class for the first time in the same instant) — the unique index on Name rejected
            // this insert. Detach our own now-orphaned attempt and use whichever row actually won.
            db.Entry(category).State = EntityState.Detached;
            var winner = await db.DetectedObjectCategories.FirstAsync(c => c.Name == categoryName, ct);
            cache[categoryName] = winner.Id;
            return winner.Id;
        }

        cache[categoryName] = category.Id;
        return category.Id;
    }

    /// <summary>M8 pass 6: every raw ONVIF PullPoint notification a node reported, logged verbatim —
    /// classification (IsMotion) already happened node-side (see CameraEventClassifier), this just
    /// trusts that flag rather than re-deriving it. Plain inserts, no upsert: unlike a MotionSpan a
    /// raw notification has no natural "same event, extend it" identity — each one is a discrete
    /// point-in-time report, so real volume is exactly the notification count, not a running total
    /// that would need collapsing.</summary>
    public async Task RecordCameraEventsAsync(Guid nodeId, IReadOnlyList<CameraEventReportItem> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;

        db.CameraEvents.AddRange(events.Select(item => new CameraEvent
        {
            CameraId = item.CameraId,
            OnvifTopic = item.OnvifTopic,
            ReceivedUtc = item.ReceivedUtc,
            PayloadJson = item.PayloadJson
        }));

        while (true)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (ex.Entries.Count > 0)
            {
                // Same detach-and-retry shape as RecordMotionSpansAsync — a camera reassigned away
                // from this node between the event firing and this report landing is the realistic
                // cause (FK still enforced on insert), not worth losing the rest of the batch over.
                foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
            }
        }
    }

    public async Task AssignCameraAsync(Guid cameraId, Guid? nodeId, CancellationToken ct = default)
    {
        var camera = await db.Cameras.FirstOrDefaultAsync(c => c.Id == cameraId, ct)
            ?? throw new InvalidOperationException("Camera not found.");
        camera.NodeId = nodeId;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReassignCamerasAsync(IReadOnlyCollection<Guid> cameraIds, Guid? nodeId, CancellationToken ct = default)
    {
        if (cameraIds.Count == 0) return;
        await db.Cameras.Where(c => cameraIds.Contains(c.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.NodeId, nodeId), ct);
    }

    public async Task UpdateAsync(Guid nodeId, string name, string? storageRootPath, AiAccelerator? aiAccelerator = null, CancellationToken ct = default)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new InvalidOperationException("Node not found.");
        node.Name = name;
        node.StorageRootPath = string.IsNullOrWhiteSpace(storageRootPath) ? null : storageRootPath;
        node.AiAccelerator = aiAccelerator;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid nodeId, CancellationToken ct = default)
    {
        // Cameras.NodeId is configured DeleteBehavior.SetNull, so this unassigns rather than
        // deletes the cameras that were on this node — their recording just stops until they're
        // reassigned. Segments already written keep their NodeId as-is (no FK on it); they're
        // historical recordings, not live node state, and shouldn't disappear with the node.
        await db.Nodes.Where(n => n.Id == nodeId).ExecuteDeleteAsync(ct);
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
public class NodeService(ApplicationDbContext db, ISettingsResolver settings, ILogger<NodeService>? logger = null) : INodeService
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
            // Storage/archive paths are per-node — captured here from what install-node.ps1 passed
            // (--storage-root / --archive-root). A node that registers with neither (an older
            // install-node.ps1) starts with no storage path and records nothing until an admin sets
            // one on Admin/Nodes — see GetConfigAsync.
            StorageRootPath = string.IsNullOrWhiteSpace(request.StorageRootPath) ? null : request.StorageRootPath,
            ArchiveRootPath = string.IsNullOrWhiteSpace(request.ArchiveRootPath) ? null : request.ArchiveRootPath,
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
        // Failover plan phase 5b: the post-rotation grace window closes the first time the node
        // actually authenticates with the *new* secret (matches current, not previous) — until then
        // an in-flight request or a node that hasn't applied NewSecret yet still works off the old one.
        if (matchesCurrent && !matchesPrevious && node.PreviousApiKeyHash is not null)
            node.PreviousApiKeyHash = null;
        await db.SaveChangesAsync(ct);

        return node;
    }

    public async Task<NodeCheckInSecurity> ApplyCheckInSecurityAsync(Guid nodeId, string? presentedNonce, CancellationToken ct = default)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null) return new NodeCheckInSecurity(ReplayRejected: false, NextNonce: null, NewSecret: null);

        // Phase 5a nonce gate. Absent and wrong are deliberately different cases (see the failover
        // plan's nonce-rollout note — never `presentedNonce ?? ""`):
        //  - stored nonce, node echoes nothing  -> a build that predates this field; allow, and clear
        //    the stored value so it isn't stuck failing the moment it does update.
        //  - stored nonce, node echoes the wrong one -> replay (or a corrupted node.config); reject
        //    401 and touch nothing, so the legitimate node still holding the real nonce passes next.
        if (node.CheckInNonce is not null)
        {
            if (presentedNonce is null)
            {
                node.CheckInNonce = null;
            }
            else if (!SecretHash.FixedTimeEquals(presentedNonce, node.CheckInNonce))
            {
                return new NodeCheckInSecurity(ReplayRejected: true, NextNonce: null, NewSecret: null);
            }
        }

        var nextNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        node.CheckInNonce = nextNonce;

        // Phase 5b secret rotation — only ever reached on a check-in that already passed the nonce
        // gate above, so a replayed/forged heartbeat can never harvest a fresh secret.
        string? newSecret = null;
        var rotationDue = node.PendingSecretRotation
            || (node.SecretRotationDays > 0
                && DateTime.UtcNow - (node.ApiKeyRotatedAt ?? node.CreatedAt) >= TimeSpan.FromDays(node.SecretRotationDays));
        if (rotationDue)
        {
            newSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            node.PreviousApiKeyHash = node.ApiKeyHash;
            node.ApiKeyHash = SecretHash.Hash(newSecret);
            node.ApiKeyRotatedAt = DateTime.UtcNow;
            node.PendingSecretRotation = false;
        }

        await db.SaveChangesAsync(ct);
        return new NodeCheckInSecurity(ReplayRejected: false, NextNonce: nextNonce, NewSecret: newSecret);
    }

    public async Task ResetAuthAsync(Guid nodeId, bool rotateSecretNow, CancellationToken ct = default)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null) return;
        node.CheckInNonce = null;
        node.AllowReregistration = true;
        if (rotateSecretNow) node.PendingSecretRotation = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<NodeConfigResponse> GetConfigAsync(Guid nodeId, CancellationToken ct = default)
    {
        // Storage path (and the optional archive path) are per-node, no global default — set when the
        // node is added (install-node.ps1 -StorageRoot/-ArchiveRoot -> RegisterAsync) or on
        // Admin/Nodes. A node with no storage path is served no camera config (below): it must not
        // silently record to some fallback location an admin never chose.
        // Failover plan phase 1: the direct-streaming / client-endpoint columns come along on the
        // same single-row read.
        var nodeRoots = await db.Nodes.Where(n => n.Id == nodeId)
            .Select(n => new
            {
                n.StorageRootPath, n.ArchiveRootPath,
                n.DirectStreamingMode, n.AllowInsecureClientEndpoint,
                n.ClientCertPfxPath, n.ClientCertPfxPassword,
                n.DisableAiObjectDetection,
            }).FirstOrDefaultAsync(ct);
        var storageRoot = string.IsNullOrWhiteSpace(nodeRoots?.StorageRootPath) ? null : nodeRoots.StorageRootPath;
        var archiveRoot = string.IsNullOrWhiteSpace(nodeRoots?.ArchiveRootPath) ? null : nodeRoots.ArchiveRootPath;
        // Failover plan phase 3: "recording only" mode forces AI detection off for every camera this
        // node serves — its own and any it adopts during a failover.
        var disableAiDetection = nodeRoots?.DisableAiObjectDetection ?? false;

        // Failover plan phase 3: the cameras this node should actually be recording right now — its
        // own (unless it is itself FailedOverAway) plus any it has adopted from a down node that
        // resolves to it as backup. RecordingNodeResolver drives it off persisted FailoverState only;
        // RecordingFailoverService is the one writer of that. A node deemed down drops its own cameras
        // here immediately, so a still-reachable node stops recording them within one reconcile.
        var failoverStateRows = await db.Nodes.AsNoTracking()
            .Select(n => new { n.Id, n.FailoverState, n.BackupNodeId }).ToListAsync(ct);
        var failoverStateById = failoverStateRows.ToDictionary(n => n.Id, n => n.FailoverState);
        var defaultBackupById = failoverStateRows.ToDictionary(n => n.Id, n => n.BackupNodeId);

        List<Camera> cameras = [];
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            var enabledCameraNodes = await db.Cameras.AsNoTracking()
                .Where(c => c.IsEnabled && c.NodeId != null)
                .Select(c => new { c.Id, NodeId = c.NodeId!.Value, c.BackupNodeIdOverride })
                .ToListAsync(ct);

            var effectiveIds = enabledCameraNodes
                .Where(c => RecordingNodeResolver.Resolve(
                    c.NodeId, c.BackupNodeIdOverride, failoverStateById, defaultBackupById) == nodeId)
                .Select(c => c.Id)
                .ToHashSet();

            if (effectiveIds.Count > 0)
                cameras = await db.Cameras
                    .Where(c => effectiveIds.Contains(c.Id))
                    .Include(c => c.Streams)
                    .Include(c => c.Capabilities)
                    .ToListAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(storageRoot))
            logger?.LogWarning("Node {NodeId} has no storage path configured — serving no camera config until one is set on Admin/Nodes.", nodeId);

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

        // Failover plan phase 1: direct-to-node streaming. Per-node DirectStreamingMode /
        // AllowInsecureClientEndpoint override the matching global LiveView setting (null = inherit).
        var effectiveDirectMode = !string.IsNullOrWhiteSpace(nodeRoots?.DirectStreamingMode)
            ? nodeRoots!.DirectStreamingMode!
            : await settings.GetAsync("LiveView.DirectStreaming", "Proxy", ct: ct);
        var clientEndpointEnabled = string.Equals(effectiveDirectMode, "Direct", StringComparison.OrdinalIgnoreCase);
        var clientEndpointAllowInsecure = nodeRoots?.AllowInsecureClientEndpoint
            ?? await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false, ct: ct);
        // Optional operator-set public origin for the node's CORS allow-list — when unset the node
        // just reflects the request Origin (these routes are MediaToken-gated regardless).
        var publicOrigin = await settings.GetAsync<string?>("LiveView.PublicOrigin", null, ct: ct);

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
        // Global detection-quality toggle — prompt span finalization when an object leaves frame
        // (so a later unrelated same-type object isn't merged into it). Default on; off is the fast
        // A/B revert. The box-jitter rejection this once also governed is now the per-camera
        // Detection.RejectMotionJitter, resolved in the camera loop below. See NodeConfigResponse.
        var snapshotMotionAccuracy = await settings.GetAsync("Detection.SnapshotMotionAccuracy", true, ct: ct);
        // How long a span is held with no Moving instance before the early-finalize above flushes it
        // (was a hard-coded 5s). Global; 1-10s. See NodeConfigResponse.DepartureGraceSeconds.
        var departureGraceSeconds = Math.Clamp(await settings.GetAsync("Detection.DepartureGraceSeconds", 5, ct: ct), 1, 10);
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
        // How D-FINE uses TensorRT ("Off" / "FP32" / "FP16") — same node-scoped resolution; only
        // acted on for a D-FINE pipeline on a node with the machine-local Vision:EnableTensorRt set.
        var dfineTensorRtMode = await settings.GetAsync("Detection.DFineTensorRtMode", "Off", nodeId: nodeId, ct: ct);
        // Per-camera detection frame-rate ceiling (0 = decode rate). Node-scoped.
        var maxDetectionFps = await settings.GetAsync("Detection.MaxFps", 10, nodeId: nodeId, ct: ct);
        // Pass 4a — same Global -> Node resolution as the flags above. Moves per-frame preprocessing
        // off the CPU onto whatever accelerator ONNX Runtime is using. Opt-in, default off.
        var gpuPreprocessing = await settings.GetAsync("Detection.GpuPreprocessing", false, nodeId: nodeId, ct: ct);
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
            // Box-jitter rejection is per-camera (a driveway with a parked vehicle in view needs it;
            // a street camera doesn't) — off by default, restoring the pre-0.188 movement classifier.
            var rejectMotionJitter = await settings.GetAsync("Detection.RejectMotionJitter", false, cameraId: c.Id, nodeId: nodeId, ct: ct);
            var motionJitterPixels = Math.Clamp(
                await settings.GetAsync("Detection.MotionJitterPixels", 3, cameraId: c.Id, nodeId: nodeId, ct: ct), 1, 15);
            var aiDetectionStreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub", cameraId: c.Id, nodeId: nodeId, ct: ct);
            // Corrects a camera that misreports its watch stream's orientation (a corridor-mounted
            // device advertising 704x480 while delivering 480x704) before the node builds the
            // detection profile from it — see LarisVMS.Node.DetectionOrientation.
            var aiDetectionOrientation = await settings.GetAsync("AiDetection.Orientation", "Auto", cameraId: c.Id, nodeId: nodeId, ct: ct);
            // Archive-storage: same Camera -> Node -> Global chain as RetentionDays above.
            var archiveEnabled = await settings.GetAsync("Archive.Enabled", false, cameraId: c.Id, nodeId: nodeId, ct: ct);
            var archiveRetentionDays = await settings.GetAsync<int?>("Archive.RetentionDays", 0, cameraId: c.Id, nodeId: nodeId, ct: ct);
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
                c.AiDetectionEnabled && !disableAiDetection, c.MotionDetectionSource?.ToString(),
                aiConfidence, aiIou, aiDetectionStreamRole, aiDetectionOrientation, c.ServerMotionEnabled,
                c.MotionRegionMode.ToString(), c.MotionGridSize, c.MotionGridMask, c.MotionGridSensitivity,
                archiveEnabled, archiveRetentionDays, rejectMotionJitter, motionJitterPixels));
        }

        // Cameras this node has leftover Segments for but doesn't currently record — reassigned to a
        // different node, or deleted outright. Without this, StorageManager's orphaned-folder sweep
        // has no RetentionDays to honor for them at all (the camera simply isn't in `cameras` above),
        // which is exactly the gap that let old footage accumulate on a node forever after a camera
        // moved away — see StorageManager.SweepOrphanedCameraFolders' own doc comment.
        var assignedCameraIds = cameras.Select(c => c.Id).ToList();
        // No storage path -> no sweeps of any kind for this node (see the top of this method).
        var orphanedCameraIds = string.IsNullOrWhiteSpace(storageRoot)
            ? []
            : await db.Segments
                .Where(s => s.NodeId == nodeId && !assignedCameraIds.Contains(s.CameraId))
                .Select(s => s.CameraId)
                .Distinct()
                .ToListAsync(ct);

        // Failover plan phase 3: the recorder nodes this one is the backup for — via Node.BackupNodeId
        // or a per-camera Camera.BackupNodeIdOverride — which it must probe for the recording-failover
        // quorum. Only nodes with a known LAN address are probeable; a node with no LastIpAddress/
        // LivePort yet simply isn't handed out.
        var backedUpNodeIds = await db.Nodes.AsNoTracking()
            .Where(n => n.BackupNodeId == nodeId && n.Id != nodeId)
            .Select(n => n.Id).ToListAsync(ct);
        var overrideBackedUpNodeIds = await db.Cameras.AsNoTracking()
            .Where(c => c.BackupNodeIdOverride == nodeId && c.NodeId != null && c.NodeId != nodeId)
            .Select(c => c.NodeId!.Value).Distinct().ToListAsync(ct);
        var partnerIds = backedUpNodeIds.Concat(overrideBackedUpNodeIds).Distinct().ToList();
        var partnersToProbe = partnerIds.Count == 0
            ? null
            : await db.Nodes.AsNoTracking()
                .Where(n => partnerIds.Contains(n.Id) && n.LastIpAddress != null && n.LivePort != null)
                .Select(n => new NodePartnerProbeDto(n.Id, n.LastIpAddress!, n.LivePort!.Value))
                .ToListAsync(ct);

        var orphanedCameraDtos = new List<NodeConfigOrphanedCameraDto>();
        foreach (var orphanedCameraId in orphanedCameraIds)
        {
            // Same resolution chain as every assigned camera's own RetentionDays above, scoped to
            // *this* node — a per-node override this node had for that camera (set back when it was
            // still assigned here) still applies to aging out its leftover copy.
            var retentionDays = await settings.GetAsync<int?>("Retention.Days", 30, cameraId: orphanedCameraId, nodeId: nodeId, ct: ct);
            var orphanArchiveEnabled = await settings.GetAsync("Archive.Enabled", false, cameraId: orphanedCameraId, nodeId: nodeId, ct: ct);
            var orphanArchiveRetentionDays = await settings.GetAsync<int?>("Archive.RetentionDays", 0, cameraId: orphanedCameraId, nodeId: nodeId, ct: ct);
            orphanedCameraDtos.Add(new NodeConfigOrphanedCameraDto(orphanedCameraId, retentionDays, orphanArchiveEnabled, orphanArchiveRetentionDays));
        }

        return new NodeConfigResponse(cameraDtos, storageRoot, watermarkPercent, mediaSigningKey, orphanedCameraDtos,
            adaptiveStreamingEnabled, (aiAccelerator ?? AiAccelerator.Auto).ToString(),
            reportIdleDetections, aspectMode, detectionModelFamily, dfineWeights, aiIdleTimeoutSeconds,
            gpuPreprocessing, logLevel)
        {
            YoloXSize = yoloXSize, MaxDetectionFps = maxDetectionFps, DFineTensorRtMode = dfineTensorRtMode,
            ArchiveRootPath = archiveRoot, SnapshotMotionAccuracy = snapshotMotionAccuracy,
            DepartureGraceSeconds = departureGraceSeconds,
            ClientEndpointEnabled = clientEndpointEnabled,
            ClientCertPfxPath = string.IsNullOrWhiteSpace(nodeRoots?.ClientCertPfxPath) ? null : nodeRoots!.ClientCertPfxPath,
            ClientCertPfxPassword = string.IsNullOrWhiteSpace(nodeRoots?.ClientCertPfxPassword) ? null : nodeRoots!.ClientCertPfxPassword,
            ClientEndpointAllowInsecure = clientEndpointAllowInsecure,
            WebOrigin = string.IsNullOrWhiteSpace(publicOrigin) ? null : publicOrigin,
            PartnersToProbe = partnersToProbe,
        };
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

    public async Task RelocateSegmentsAsync(Guid nodeId, IReadOnlyList<SegmentRelocateItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;
        var now = DateTime.UtcNow;
        // One statement per item — each row moves to a distinct new path, so there's no bulk form.
        // Same "a handful of rows per sweep" scale as RecordSegmentsAsync. Idempotent: if the row is
        // already at NewFilePath (a retried report after the node deleted the source), 0 rows update
        // and we just move on — the node clears its queue either way.
        foreach (var item in items)
        {
            var updated = await db.Segments
                .Where(s => s.NodeId == nodeId && s.FilePath == item.OldFilePath)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.FilePath, item.NewFilePath)
                    .SetProperty(s => s.StorageTier, StorageTier.Archive)
                    .SetProperty(s => s.ArchivedAt, now)
                    .SetProperty(s => s.SizeBytes, item.SizeBytes), ct);

            if (updated == 0)
            {
                var alreadyMoved = await db.Segments
                    .AnyAsync(s => s.NodeId == nodeId && s.FilePath == item.NewFilePath, ct);
                if (!alreadyMoved)
                    logger?.LogWarning("Node {NodeId} reported a relocation of '{Old}' but no matching Segment row exists.", nodeId, item.OldFilePath);
            }
        }
    }

    public async Task<List<string>> ListSegmentFilePathsAsync(Guid nodeId, CancellationToken ct = default)
        => await db.Segments.AsNoTracking().Where(s => s.NodeId == nodeId).Select(s => s.FilePath).ToListAsync(ct);

    public async Task<List<string>> ListPrimaryTieredSegmentFilePathsAsync(Guid nodeId, CancellationToken ct = default)
        => await db.Segments.AsNoTracking()
            .Where(s => s.NodeId == nodeId && s.StorageTier == StorageTier.Primary)
            .Select(s => s.FilePath).ToListAsync(ct);

    public async Task<List<long>> ListMotionSpanIdsAsync(Guid nodeId, CancellationToken ct = default)
        => await db.MotionSpans.AsNoTracking().Where(m => m.Camera.NodeId == nodeId).Select(m => m.Id).ToListAsync(ct);

    /// <summary>Pure so it's unit-testable without a database — same "isolate the arithmetic from the
    /// I/O" shape as TimelineService.NormalizeToUtc. Null input (an older node build not sending
    /// SentAtUtc yet) means "no measurement this heartbeat," not "zero skew."</summary>
    internal static double? ComputeClockSkewSeconds(DateTime? nodeSentAtUtc, DateTime serverReceivedUtc)
        => nodeSentAtUtc is { } sentAt ? (serverReceivedUtc - sentAt).TotalSeconds : null;

    public async Task RecordHeartbeatAsync(Guid nodeId, long? freeBytes, long? totalBytes, string? version, int? livePort,
        DateTime? nodeSentAtUtc, DateTime serverReceivedUtc, List<string>? detectedEncoders = null,
        List<string>? detectedAccelerators = null, long? archiveFreeBytes = null, long? archiveTotalBytes = null,
        bool storagePressureActive = false, int? clientEndpointReportedPort = null,
        DateTime? clientEndpointCertNotAfter = null, bool? clientEndpointCertIsSelfSigned = null,
        string? clientEndpointLastError = null, List<NodePartnerHealthReport>? partnerHealthReports = null,
        CancellationToken ct = default)
    {
        var skew = ComputeClockSkewSeconds(nodeSentAtUtc, serverReceivedUtc);
        var encodersJson = detectedEncoders is not null ? System.Text.Json.JsonSerializer.Serialize(detectedEncoders) : null;
        // Object detection plan decision 2: same coalesce-preserve shape as encodersJson above.
        var acceleratorsJson = detectedAccelerators is not null ? System.Text.Json.JsonSerializer.Serialize(detectedAccelerators) : null;
        // Failover plan phase 3: this node's outgoing quorum votes on the partners it backs up. A
        // current node always sends a list (empty when it probed nothing), so a non-null value here is
        // the fresh truth and overwrites; an older node sends null and its stored value is preserved.
        var partnerHealthJson = partnerHealthReports is not null
            ? System.Text.Json.JsonSerializer.Serialize(partnerHealthReports) : null;
        var now = DateTime.UtcNow;

        await db.Nodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.StorageFreeBytes, freeBytes)
            .SetProperty(n => n.StorageTotalBytes, totalBytes)
            .SetProperty(n => n.StorageStatsUpdatedAt, now)
            // Archive stats: the node always measures both when it has an archive root, so a blind
            // overwrite is right (unlike the coalesce-preserve encoder JSON above). Both null when the
            // node has no archive root configured or predates the field.
            .SetProperty(n => n.ArchiveFreeBytes, archiveFreeBytes)
            .SetProperty(n => n.ArchiveTotalBytes, archiveTotalBytes)
            .SetProperty(n => n.ArchiveStatsUpdatedAt, n => archiveFreeBytes != null ? now : n.ArchiveStatsUpdatedAt)
            .SetProperty(n => n.StoragePressureActive, storagePressureActive)
            .SetProperty(n => n.StoragePressureSince, n => storagePressureActive
                ? (n.StoragePressureActive ? n.StoragePressureSince : now)
                : (DateTime?)null)
            .SetProperty(n => n.Version, n => version ?? n.Version)
            .SetProperty(n => n.LivePort, n => livePort ?? n.LivePort)
            .SetProperty(n => n.ClockSkewSeconds, n => skew ?? n.ClockSkewSeconds)
            .SetProperty(n => n.ClockSkewMeasuredAt, n => skew != null ? serverReceivedUtc : n.ClockSkewMeasuredAt)
            .SetProperty(n => n.DetectedEncodersJson, n => encodersJson ?? n.DetectedEncodersJson)
            .SetProperty(n => n.DetectedAcceleratorsJson, n => acceleratorsJson ?? n.DetectedAcceleratorsJson)
            // Failover plan phase 1: the node reports the full current truth of its client HTTPS
            // endpoint every heartbeat (or all-null when it isn't running one / predates this), so a
            // blind overwrite is right — same as the archive stats above.
            .SetProperty(n => n.ClientEndpointReportedPort, clientEndpointReportedPort)
            .SetProperty(n => n.ClientEndpointCertNotAfter, clientEndpointCertNotAfter)
            .SetProperty(n => n.ClientEndpointCertIsSelfSigned, clientEndpointCertIsSelfSigned)
            .SetProperty(n => n.ClientEndpointLastError, clientEndpointLastError)
            .SetProperty(n => n.PartnerHealthReportsJson, n => partnerHealthJson ?? n.PartnerHealthReportsJson), ct);
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

    /// <summary>free / (bytes written per day) as a day count, or null when either input is missing
    /// or the write rate is non-positive — an honest "unknown" rather than a misleading number from
    /// too little history. Pure so it's unit-testable without a database.</summary>
    internal static double? EstimateDaysRemaining(long? freeBytes, long bytesPerDay)
        => freeBytes is { } free && bytesPerDay > 0 ? free / (double)bytesPerDay : null;

    public async Task<Dictionary<Guid, double?>> GetEstimatedDaysRemainingAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var bytesPerNodeLastDay = await db.Segments
            .Where(s => s.StartUtc >= since && s.StorageTier == StorageTier.Primary)
            .GroupBy(s => s.NodeId)
            .Select(g => new { NodeId = g.Key, Bytes = g.Sum(s => s.SizeBytes) })
            .ToDictionaryAsync(x => x.NodeId, x => x.Bytes, ct);

        var nodes = await db.Nodes.AsNoTracking().Select(n => new { n.Id, n.StorageFreeBytes }).ToListAsync(ct);

        return nodes.ToDictionary(
            n => n.Id,
            n => EstimateDaysRemaining(n.StorageFreeBytes, bytesPerNodeLastDay.GetValueOrDefault(n.Id)));
    }

    /// <summary>Same shape as GetEstimatedDaysRemainingAsync but for each node's archive volume:
    /// archive free bytes divided by the bytes moved to archive over the last 24h. Null until there
    /// is archiving activity to estimate a rate from.</summary>
    public async Task<Dictionary<Guid, double?>> GetArchiveEstimatedDaysRemainingAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var bytesPerNodeLastDay = await db.Segments
            .Where(s => s.StorageTier == StorageTier.Archive && s.ArchivedAt >= since)
            .GroupBy(s => s.NodeId)
            .Select(g => new { NodeId = g.Key, Bytes = g.Sum(s => s.SizeBytes) })
            .ToDictionaryAsync(x => x.NodeId, x => x.Bytes, ct);

        var nodes = await db.Nodes.AsNoTracking().Select(n => new { n.Id, n.ArchiveFreeBytes }).ToListAsync(ct);

        return nodes.ToDictionary(
            n => n.Id,
            n => EstimateDaysRemaining(n.ArchiveFreeBytes, bytesPerNodeLastDay.GetValueOrDefault(n.Id)));
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
        //
        // Spans this call has created but not yet saved. The exact-identity and coalesce lookups
        // below are database queries — they can't see rows still in Added state from an earlier
        // iteration of this same loop. A single batch routinely carries two reports for one span
        // (Vision's 15s checkpoint landing in the same flush as that span's close-report — both
        // carry the identical backdated StartUtc), so without also checking this list the loop
        // Adds two rows with the same (CameraId, DetectedObjectLabel, StartUtc) and SaveChanges
        // fails the filtered unique index IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc.
        var pending = new List<MotionSpan>();

        // Folds one report item's forward progress onto a span already on record — a tracked DB
        // row or a pending Added one: extend EndUtc forward only, running-max Score/MovingCount,
        // adopt the incoming best frame. bestFrameByConfidence mirrors the two call sites' existing
        // rule — the exact-identity path trusts Vision's monotonic best-frame tracking and just
        // overwrites; the coalesce path, joining two independently-tracked fragments, only takes a
        // best frame at least as confident as the one already on record.
        static void Apply(MotionSpan span, MotionSpanReportItem item, bool bestFrameByConfidence)
        {
            if (item.EndUtc > span.EndUtc) span.EndUtc = item.EndUtc;
            span.Score = Math.Max(span.Score, item.Score);
            if (item.MovingCount is { } mc) span.MovingCount = Math.Max(span.MovingCount ?? 1, mc);
            if (item.BestFrameAtUtc is not null
                && (!bestFrameByConfidence || span.BestBoxConfidence is null
                    || (item.BestBoxConfidence ?? 0) >= span.BestBoxConfidence))
            {
                span.BestFrameAtUtc = item.BestFrameAtUtc;
                span.BestBoxX = item.BestBoxX;
                span.BestBoxY = item.BestBoxY;
                span.BestBoxW = item.BestBoxW;
                span.BestBoxH = item.BestBoxH;
                span.BestBoxConfidence = item.BestBoxConfidence;
            }
        }

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
                // older report should never pull EndUtc backward. Best-frame just overwrites:
                // object detection plan decision 10 — LarisVMS.Vision.Service's own best-frame
                // tracking is already monotonic (a later report's box is never worse than an
                // earlier one for the same track).
                Apply(existing, item, bestFrameByConfidence: false);
                continue;
            }

            // Same identity, but the only match is a row this batch already Added and hasn't
            // saved yet (the checkpoint-plus-close pair described at `pending` above). Fold onto
            // it rather than Adding a second row that would collide on SaveChanges.
            var pendingExact = pending.FirstOrDefault(m =>
                m.CameraId == item.CameraId && m.ZoneId == item.ZoneId && m.EventTagRuleId == item.EventTagRuleId
                && m.DetectionKind == item.DetectionKind && m.DetectedObjectLabel == item.DetectedObjectLabel
                && m.StartUtc == item.StartUtc);
            if (pendingExact is not null)
            {
                Apply(pendingExact, item, bestFrameByConfidence: false);
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
                    Apply(sibling, item, bestFrameByConfidence: true);
                    continue;
                }

                // Same as pendingExact above, for the coalesce case: the overlapping span this
                // fragment belongs to is one this batch just Added and hasn't saved.
                var pendingSibling = pending.FirstOrDefault(m =>
                    m.CameraId == item.CameraId
                    && m.DetectedObjectLabel == item.DetectedObjectLabel
                    && m.ZoneId == null && m.EventTagRuleId == null && m.DetectionKind == null
                    && m.StartUtc <= windowEnd && m.EndUtc >= windowStart);
                if (pendingSibling is not null)
                {
                    Apply(pendingSibling, item, bestFrameByConfidence: true);
                    continue;
                }
            }

            var created = new MotionSpan
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
                Score = item.Score,
                // "x2" / "x3" snapshot badge — peak simultaneous moving instances of this label.
                // Null for every non-AiDetection span and for an older node that doesn't report it.
                MovingCount = item.MovingCount
            };
            db.MotionSpans.Add(created);
            pending.Add(created);
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
        if (incoming.MovingCount is { } mc) target.MovingCount = Math.Max(target.MovingCount ?? 1, mc);
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

    public async Task UpdateAsync(Guid nodeId, string name, string? storageRootPath, AiAccelerator? aiAccelerator = null,
        string? archiveRootPath = null, NodeClientEndpointUpdate? clientEndpoint = null, CancellationToken ct = default)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new InvalidOperationException("Node not found.");
        node.Name = name;
        node.StorageRootPath = string.IsNullOrWhiteSpace(storageRootPath) ? null : storageRootPath;
        node.ArchiveRootPath = string.IsNullOrWhiteSpace(archiveRootPath) ? null : archiveRootPath;
        node.AiAccelerator = aiAccelerator;

        // Failover plan phase 1: direct-to-node client endpoint. Null clientEndpoint = caller isn't
        // touching these (keeps every existing call site unchanged).
        if (clientEndpoint is { } ce)
        {
            node.DirectStreamingMode = string.IsNullOrWhiteSpace(ce.DirectStreamingMode) ? null : ce.DirectStreamingMode;
            node.AllowInsecureClientEndpoint = ce.AllowInsecureClientEndpoint;
            node.ClientEndpointHost = string.IsNullOrWhiteSpace(ce.ClientEndpointHost) ? null : ce.ClientEndpointHost.Trim();
            node.ClientCertPfxPath = string.IsNullOrWhiteSpace(ce.ClientCertPfxPath) ? null : ce.ClientCertPfxPath.Trim();
            // Write-only, like every other secret field on the admin forms: null = leave as-is,
            // empty string = clear, a value = set.
            if (ce.ClientCertPfxPassword is not null)
                node.ClientCertPfxPassword = ce.ClientCertPfxPassword.Length == 0 ? null : ce.ClientCertPfxPassword;

            // Failover plan phase 2: proxy assignment. The form always submits both dropdowns, so a
            // null/Guid.Empty value means "no proxy" — a distinct backup that matches the primary is
            // pointless, so it is dropped.
            var primary = ce.PrimaryProxyId is { } p && p != Guid.Empty ? p : (Guid?)null;
            var backup = ce.BackupProxyId is { } b && b != Guid.Empty && b != primary ? b : (Guid?)null;
            node.PrimaryProxyId = primary;
            node.BackupProxyId = backup;

            // Failover plan phase 3: recording failover. A backup that is this node itself is
            // meaningless and dropped. RecordingFailoverService reconciles FailoverState from here on
            // its own tick — this only sets the configuration, never the live state.
            node.BackupNodeId = ce.BackupNodeId is { } bn && bn != Guid.Empty && bn != nodeId ? bn : null;
            node.DisableAiObjectDetection = ce.DisableAiObjectDetection;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetMaintenanceAsync(Guid nodeId, bool enabled, string? by, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // Only the flag + who/when — RecordingFailoverService is the single writer of FailoverState
        // and picks this up on its next tick.
        await db.Nodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(u => u
            .SetProperty(n => n.MaintenanceMode, enabled)
            .SetProperty(n => n.MaintenanceSinceUtc, n => enabled
                ? (n.MaintenanceMode ? n.MaintenanceSinceUtc : now)
                : (DateTime?)null)
            .SetProperty(n => n.MaintenanceBy, n => enabled ? by : null), ct);
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

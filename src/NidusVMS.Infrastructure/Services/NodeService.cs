using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

/// <summary>
/// Server side of the node control plane. Auth mirrors dploid's AgentAuthMiddleware: the node
/// presents "{nodeId}:{secret}", the secret is SHA-256 hashed and compared with
/// CryptographicOperations.FixedTimeEquals against the stored hash, with PreviousApiKeyHash as a
/// fallback slot for a future rotation flow (not wired up yet — see Node's doc comment).
/// </summary>
public class NodeService(ApplicationDbContext db, ISettingsResolver settings) : INodeService
{
    public async Task<List<Node>> ListAsync(CancellationToken ct = default)
        => await db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct);

    public async Task<NodeRegisterResponse> RegisterAsync(NodeRegisterRequest request, CancellationToken ct = default)
    {
        var expectedKey = await settings.GetRawAsync("Node.RegistrationKey", ct: ct);
        if (string.IsNullOrEmpty(expectedKey) || request.RegistrationKey != expectedKey)
            throw new UnauthorizedAccessException("Invalid registration key.");

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Name = request.Hostname,
            ApiKeyHash = Hash(secret),
            // Signs the short-lived media tokens NidusVMS.Web issues for live view (M5); the node
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

        var candidateHash = Hash(secret);
        var matchesCurrent = FixedTimeEquals(candidateHash, node.ApiKeyHash);
        var matchesPrevious = node.PreviousApiKeyHash is not null && FixedTimeEquals(candidateHash, node.PreviousApiKeyHash);
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

        var cameraDtos = new List<NodeConfigCameraDto>();
        foreach (var c in cameras)
        {
            // 0 or negative means "keep forever" — an explicit admin choice, not "unset". Unset
            // (no override anywhere) falls through to the 30-day compiled-in default below.
            var retentionDays = await settings.GetAsync<int?>("Retention.Days", 30, cameraId: c.Id, nodeId: nodeId, ct: ct);
            cameraDtos.Add(new NodeConfigCameraDto(
                c.Id, c.Name, c.Username, c.Password,
                c.Streams.Where(s => s.IsEnabled).Select(s => new NodeConfigStreamDto(
                    s.Id, s.Role.ToString(), s.RtspUri, s.Codec, s.Width, s.Height, s.HasAudio)).ToList(),
                retentionDays, c.QuotaBytes));
        }

        return new NodeConfigResponse(cameraDtos, storageRoot, watermarkPercent, mediaSigningKey);
    }

    public async Task DeleteSegmentsAsync(Guid nodeId, IReadOnlyList<string> filePaths, CancellationToken ct = default)
    {
        if (filePaths.Count == 0) return;
        await db.Segments
            .Where(s => s.NodeId == nodeId && filePaths.Contains(s.FilePath))
            .ExecuteDeleteAsync(ct);
    }

    public async Task RecordHeartbeatAsync(Guid nodeId, long? freeBytes, long? totalBytes, string? version, int? livePort, CancellationToken ct = default)
    {
        await db.Nodes.Where(n => n.Id == nodeId).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.StorageFreeBytes, freeBytes)
            .SetProperty(n => n.StorageTotalBytes, totalBytes)
            .SetProperty(n => n.StorageStatsUpdatedAt, DateTime.UtcNow)
            .SetProperty(n => n.Version, n => version ?? n.Version)
            .SetProperty(n => n.LivePort, n => livePort ?? n.LivePort), ct);
    }

    public async Task UpdateStreamInfoAsync(Guid nodeId, IReadOnlyList<StreamInfoReportItem> items, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            if (!Enum.TryParse<CameraStreamRole>(item.StreamRole, out var role)) continue;

            // Scoped to Camera.NodeId == nodeId, not just CameraId: a report from a node the camera
            // has since been reassigned away from is stale by definition and must not overwrite what
            // the camera's *current* node measured.
            await db.CameraStreams
                .Where(s => s.CameraId == item.CameraId && s.Role == role && s.Camera.NodeId == nodeId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Width, item.Width)
                    .SetProperty(s => s.Height, item.Height)
                    .SetProperty(s => s.Codec, item.Codec), ct);
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

    public async Task UpdateAsync(Guid nodeId, string name, string? storageRootPath, CancellationToken ct = default)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new InvalidOperationException("Node not found.");
        node.Name = name;
        node.StorageRootPath = string.IsNullOrWhiteSpace(storageRootPath) ? null : storageRootPath;
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

    private static string Hash(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

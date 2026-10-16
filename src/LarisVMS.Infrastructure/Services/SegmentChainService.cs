using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Keeps each camera's segment hash chain (SegmentChain): links newly recorded segments onto the
/// camera's chain head as they're saved, and signs checkpoints of the head.
///
/// Chaining follows the order segments were saved in, one camera at a time under a lock, so two
/// report batches for the same camera (a failover overlap) can't both chain onto the same head.
/// Segments with no content hash (an older node, an unreadable file) are simply left out.
/// </summary>
public class SegmentChainService(ApplicationDbContext db, ISigningKeyService? signing = null)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    /// <summary>Links the given just-saved segments into their cameras' chains and saves.</summary>
    public async Task ChainAsync(IEnumerable<Segment> saved, CancellationToken ct = default)
    {
        foreach (var camera in saved.Where(s => s.ChainHash is null && SegmentChain.IsHash(s.ContentSha256)).GroupBy(s => s.CameraId))
        {
            var gate = Gates.GetOrAdd(camera.Key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var head = await db.SegmentChainHeads.FirstOrDefaultAsync(h => h.CameraId == camera.Key, ct);
                foreach (var segment in camera.OrderBy(s => s.Id))
                {
                    segment.PrevChainHash = head?.ChainHash;
                    segment.ChainHash = SegmentChain.Link(segment.PrevChainHash, segment.ContentSha256!, segment.CameraId, segment.StartUtc, segment.EndUtc);
                    if (head is null)
                    {
                        head = new SegmentChainHead { CameraId = camera.Key };
                        db.SegmentChainHeads.Add(head);
                    }
                    head.SegmentId = segment.Id;
                    head.ChainHash = segment.ChainHash;
                    head.SegmentEndUtc = segment.EndUtc;
                    head.UpdatedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Signs the camera's current chain head. With <paramref name="onlyIfMoved"/>, returns
    /// the latest checkpoint instead when the head hasn't moved since it. Null when the camera has no
    /// chain yet.</summary>
    public async Task<ChainCheckpoint?> CheckpointAsync(Guid cameraId, bool onlyIfMoved, CancellationToken ct = default)
    {
        if (signing is null) throw new InvalidOperationException("Checkpoints need the signing key service.");
        var head = await db.SegmentChainHeads.AsNoTracking().FirstOrDefaultAsync(h => h.CameraId == cameraId, ct);
        if (head is null) return null;
        if (onlyIfMoved)
        {
            var latest = await db.ChainCheckpoints.AsNoTracking().Where(c => c.CameraId == cameraId)
                .OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
            if (latest?.SegmentId == head.SegmentId) return latest;
        }

        var signedUtc = DateTime.UtcNow;
        var payload = SegmentChain.CheckpointPayload(cameraId, head.SegmentId, head.ChainHash, head.SegmentEndUtc, signedUtc);
        var signature = await signing.SignAsync(payload, ct);
        var checkpoint = new ChainCheckpoint
        {
            CameraId = cameraId,
            SegmentId = head.SegmentId,
            ChainHash = head.ChainHash,
            SegmentEndUtc = DateTime.SpecifyKind(head.SegmentEndUtc, DateTimeKind.Utc),
            SignedUtc = signedUtc,
            KeyId = signature.KeyId,
            Signature = signature.Signature,
        };
        db.ChainCheckpoints.Add(checkpoint);
        await db.SaveChangesAsync(ct);
        return checkpoint;
    }

    /// <summary>Hourly: a fresh checkpoint for every camera whose chain moved since its last one.</summary>
    public async Task CheckpointAllAsync(CancellationToken ct = default)
    {
        var cameras = await db.SegmentChainHeads.AsNoTracking().Select(h => h.CameraId).ToListAsync(ct);
        foreach (var cameraId in cameras) await CheckpointAsync(cameraId, onlyIfMoved: true, ct);
    }
}

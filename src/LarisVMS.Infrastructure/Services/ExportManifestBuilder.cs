using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Builds the signed manifest for a finished export item — what lets anyone confirm, later and away
/// from this server, that an exported file is exactly what LarisVMS produced from footage that hadn't
/// been changed since it was recorded.
///
/// The file is JSON: <c>{ format, signedPayload, signature, signatureDer, keyId, algorithm, publicKeyPem }</c>.
/// <c>signedPayload</c> is the manifest itself as JSON text, and the signature (ECDSA P-256 / SHA-256)
/// covers exactly those UTF-8 bytes — kept as a string so no re-serialization can change them.
/// <c>signature</c> is IEEE P1363 (WebCrypto); <c>signatureDer</c> the same in DER (openssl).
///
/// The payload carries:
///  - the exported file's name, size and SHA-256;
///  - every source segment with its recorded hash, its chain links, and whether the node found it
///    unchanged when it re-hashed it for this export;
///  - the camera's chain from the last exported segment up to the nearest signed checkpoint, and that
///    checkpoint — so the chain can be followed to a signature that was made independently of this
///    export (hourly), or, for very recent footage, one made now.
/// All times are UTC in round-trip ("o") form, which the verifier needs to recompute chain links to the
/// tick.
/// </summary>
public class ExportManifestBuilder(ApplicationDbContext db, ISigningKeyService signing)
{
    public const string Format = "LarisVMS export manifest v1";
    private const int MaxChainLinks = 20_000;

    private static string Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    public async Task<string?> BuildAsync(Guid exportItemId, ExportCompleteReportItem report, CancellationToken ct = default)
    {
        if (report.OutputSha256 is null) return null;
        var item = await db.ExportJobItems.AsNoTracking().Include(i => i.ExportJob).FirstOrDefaultAsync(i => i.Id == exportItemId, ct);
        if (item is null) return null;
        var cameraName = await db.Cameras.AsNoTracking().Where(c => c.Id == item.CameraId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            ?? "(deleted camera)";

        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var checks = (report.SegmentChecks ?? []).GroupBy(c => Norm(c.FilePath)).ToDictionary(g => g.Key, g => g.First());
        var segmentQuery = db.Segments.AsNoTracking().Where(s => s.CameraId == item.CameraId
            && s.StartUtc < item.ExportJob.ToUtc && s.EndUtc > item.ExportJob.FromUtc);
        if (item.NodeId is { } nodeId) segmentQuery = segmentQuery.Where(s => s.NodeId == nodeId);
        var segments = (await segmentQuery.OrderBy(s => s.StartUtc).ToListAsync(ct))
            .Where(s => checks.Count == 0 || checks.ContainsKey(Norm(s.FilePath)))
            .ToList();

        var exported = segments.Select(s => new
        {
            startUtc = Utc(s.StartUtc),
            endUtc = Utc(s.EndUtc),
            sha256 = s.ContentSha256,
            prevChainHash = s.PrevChainHash,
            chainHash = s.ChainHash,
            unchangedAtExport = checks.TryGetValue(Norm(s.FilePath), out var c) ? c.Matches : null,
        }).ToList();

        // From the last exported link along the chain to a signed checkpoint.
        var last = segments.Where(s => s.ChainHash is not null).OrderBy(s => s.Id).LastOrDefault();
        ChainCheckpoint? checkpoint = null;
        var links = new List<Segment>();
        if (last is not null)
        {
            checkpoint = await db.ChainCheckpoints.AsNoTracking()
                .Where(cp => cp.CameraId == item.CameraId && cp.SegmentId >= last.Id)
                .OrderBy(cp => cp.SegmentId).ThenBy(cp => cp.Id).FirstOrDefaultAsync(ct)
                ?? await new SegmentChainService(db, signing).CheckpointAsync(item.CameraId, onlyIfMoved: false, ct);
            if (checkpoint is not null && checkpoint.SegmentId != last.Id)
            {
                var following = await db.Segments.AsNoTracking()
                    .Where(s => s.CameraId == item.CameraId && s.Id > last.Id && s.Id <= checkpoint.SegmentId && s.ChainHash != null)
                    .OrderBy(s => s.Id).Take(MaxChainLinks).ToListAsync(ct);
                // Follow the links themselves rather than trusting id order.
                var byPrev = following.GroupBy(s => s.PrevChainHash!).ToDictionary(g => g.Key, g => g.First());
                var at = last.ChainHash!;
                while (at != checkpoint.ChainHash && byPrev.TryGetValue(at, out var next))
                {
                    links.Add(next);
                    at = next.ChainHash!;
                }
            }
        }

        string? checkpointKeyPem = null;
        if (checkpoint is not null)
            checkpointKeyPem = await db.SigningKeys.AsNoTracking().Where(k => k.Id == checkpoint.KeyId).Select(k => k.PublicKeyPem).FirstOrDefaultAsync(ct);

        var payload = new
        {
            format = Format,
            exportItemId = item.Id,
            exportJobId = item.ExportJobId,
            createdUtc = Utc(DateTime.UtcNow),
            camera = new { id = item.CameraId, name = cameraName },
            requested = new { fromUtc = Utc(item.ExportJob.FromUtc), toUtc = Utc(item.ExportJob.ToUtc) },
            file = new
            {
                name = report.OutputFilePath is null ? null : Path.GetFileName(report.OutputFilePath),
                sizeBytes = report.SizeBytes,
                sha256 = report.OutputSha256,
            },
            redacted = item.RedactionJson is not null,
            segments = exported,
            chainToCheckpoint = links.Select(s => new
            {
                startUtc = Utc(s.StartUtc),
                endUtc = Utc(s.EndUtc),
                sha256 = s.ContentSha256,
                prevChainHash = s.PrevChainHash,
                chainHash = s.ChainHash,
            }).ToList(),
            checkpoint = checkpoint is null ? null : new
            {
                segmentId = checkpoint.SegmentId,
                chainHash = checkpoint.ChainHash,
                segmentEndUtc = Utc(checkpoint.SegmentEndUtc),
                signedUtc = Utc(checkpoint.SignedUtc),
                keyId = checkpoint.KeyId,
                publicKeyPem = checkpointKeyPem,
                signature = checkpoint.Signature,
            },
        };

        var payloadText = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var signature = await signing.SignAsync(payloadText, ct);
        return JsonSerializer.Serialize(new
        {
            format = Format,
            signedPayload = payloadText,
            signature = signature.Signature,
            signatureDer = signature.SignatureDer,
            keyId = signature.KeyId,
            algorithm = signature.Algorithm,
            publicKeyPem = signature.PublicKeyPem,
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

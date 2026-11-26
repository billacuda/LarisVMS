using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>One exported video in a package: where it goes in the zip and where to fetch it.</summary>
public sealed record ExportPackageVideo(string ZipPath, Guid ExportItemId, Guid NodeId, string NodeFilePath, long SizeBytes, string Sha256);

/// <summary>How to reach a recorder for its export files.</summary>
public sealed record ExportPackageNode(string? Ip, int? Port, string? MediaSigningKey);

/// <summary>A file of a package that's built in memory: the data, the per-camera manifests, the README,
/// and (added by the web tier) the player's own files.</summary>
public sealed record ExportPackageFile(string ZipPath, byte[] Content);

/// <summary>Everything that goes into one export package. Paths are inside <see cref="RootFolder"/>.</summary>
public sealed class ExportPackagePlan
{
    public required ExportJob Job { get; init; }
    public required string RootFolder { get; init; }
    public required string DownloadName { get; init; }
    public required IReadOnlyList<ExportPackageVideo> Videos { get; init; }
    public required IReadOnlyDictionary<Guid, ExportPackageNode> Nodes { get; init; }
    public required List<ExportPackageFile> Files { get; init; }
}

/// <summary>
/// Builds the export package the standalone player ships in (ExportJob.IncludePlayer): one zip that a
/// job's recipient — an investigator, police, an attorney — unzips and opens with Player.html, in any
/// browser, with no network and nothing to install.
///
///   Player.html, player/…                   the player (added by the web tier)
///   video/{camera}/{file}.mp4               each camera's exported video, plus .h264.mp4 when converted
///   data/export.json                        cameras and their files, events, bookmarks, evidence locks, the export log
///   data/export.js                          the same, as a script: a page opened from disk can't fetch JSON
///   manifests/{file}.manifest.json          each video's own signed manifest (ExportManifestBuilder)
///   README.txt                              how to open and check it
///   package.manifest.json                   every file above with its size and SHA-256, signed
///
/// The package manifest is signed the same way as an export manifest (ECDSA P-256 / SHA-256 over the
/// exact signedPayload text, the public key inside), so the player's Verify tab, or anyone else, can
/// tell whether any file was changed, added, or removed after LarisVMS made it.
/// </summary>
public class ExportPackageBuilder(ApplicationDbContext db, ISigningKeyService signing, IEventColorService eventColors)
{
    public const string DataFormat = "LarisVMS export package v1";
    public const string ManifestFormat = "LarisVMS export package manifest v1";
    public const string ManifestFileName = "package.manifest.json";
    private const int MaxEvents = 50_000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>The package for a finished job, or null when there's no such job. The caller checks
    /// that the job asked for the player and that every item is done.</summary>
    public async Task<ExportPackagePlan?> PlanAsync(Guid jobId, string? packagedBy, CancellationToken ct = default)
    {
        var job = await db.ExportJobs.AsNoTracking().Include(j => j.Items).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return null;
        var now = DateTime.UtcNow;
        var done = job.Items.Where(i => i.Status == ExportItemStatus.Done && i.OutputFilePath is not null && i.NodeId is not null
                && i.OutputSha256 is not null && i.OutputSizeBytes is not null)
            .OrderBy(i => i.CameraId).ThenBy(i => i.StartedUtc).ToList();

        var cameraIds = job.Items.Select(i => i.CameraId).Distinct().ToList();
        var cameraNames = await db.Cameras.AsNoTracking().Where(c => cameraIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var folders = CameraFolders(cameraIds.Select(id => (id, cameraNames.GetValueOrDefault(id) ?? id.ToString("N"))).ToList());

        var nodeIds = done.Select(i => i.NodeId!.Value).Distinct().ToList();
        var nodeRows = await db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
            .Select(n => new { n.Id, n.Name, n.LastIpAddress, n.LivePort, n.MediaSigningKey }).ToListAsync(ct);
        var nodes = nodeRows.ToDictionary(n => n.Id, n => new ExportPackageNode(n.LastIpAddress, n.LivePort, n.MediaSigningKey));
        var nodeNames = nodeRows.ToDictionary(n => n.Id, n => n.Name);

        var manifests = await db.ExportManifests.AsNoTracking().Where(m => done.Select(i => i.Id).Contains(m.ExportJobItemId))
            .ToDictionaryAsync(m => m.ExportJobItemId, m => m.Json, ct);

        var videos = new List<ExportPackageVideo>();
        var files = new List<ExportPackageFile>();
        var cameraData = new List<object>();
        foreach (var cameraId in cameraIds)
        {
            var folder = folders[cameraId];
            var fileData = new List<object>();
            foreach (var item in done.Where(i => i.CameraId == cameraId))
            {
                // The node's path, which may be a Windows one on a Linux server or the reverse.
                var name = LarisVMS.Core.ExportFileName.Friendly(item.OutputFilePath!);
                var videoPath = $"video/{folder}/{name}";
                videos.Add(new ExportPackageVideo(videoPath, item.Id, item.NodeId!.Value, item.OutputFilePath!, item.OutputSizeBytes!.Value, item.OutputSha256!));
                string? h264Path = null;
                if (item is { ConvertedFilePath: not null, ConvertedSha256: not null, ConvertedSizeBytes: not null })
                {
                    h264Path = $"video/{folder}/{LarisVMS.Core.ExportFileName.Friendly(item.ConvertedFilePath)}";
                    videos.Add(new ExportPackageVideo(h264Path, item.Id, item.NodeId!.Value, item.ConvertedFilePath, item.ConvertedSizeBytes.Value, item.ConvertedSha256));
                }
                string? manifestPath = null;
                if (manifests.TryGetValue(item.Id, out var manifestJson))
                {
                    manifestPath = $"manifests/{LarisVMS.Core.ForeignPath.GetFileNameWithoutExtension(name)}.manifest.json";
                    files.Add(new ExportPackageFile(manifestPath, Encoding.UTF8.GetBytes(manifestJson)));
                }

                // Where each recorded segment sits in the file: the file is the segments back to back,
                // so its timeline skips any gap between them. The player maps clock time to file time
                // with these.
                var segments = await db.Segments.AsNoTracking()
                    .Where(s => s.CameraId == cameraId && s.NodeId == item.NodeId && s.StartUtc < job.ToUtc && s.EndUtc > job.FromUtc)
                    .OrderBy(s => s.StartUtc)
                    .Select(s => new { s.StartUtc, s.EndUtc, s.Codec, s.Width, s.Height })
                    .ToListAsync(ct);
                fileData.Add(new
                {
                    path = videoPath,
                    h264Path,
                    manifest = manifestPath,
                    sizeBytes = item.OutputSizeBytes,
                    sha256 = item.OutputSha256,
                    codec = segments.Select(s => s.Codec).FirstOrDefault(c => c is not null),
                    width = segments.Select(s => s.Width).FirstOrDefault(w => w is not null),
                    height = segments.Select(s => s.Height).FirstOrDefault(h => h is not null),
                    redacted = item.RedactionJson is not null,
                    node = nodeNames.GetValueOrDefault(item.NodeId!.Value),
                    segments = segments.Select(s => new { startUtc = Utc(s.StartUtc), endUtc = Utc(s.EndUtc) }),
                });
            }
            cameraData.Add(new { id = cameraId, name = cameraNames.GetValueOrDefault(cameraId) ?? Loc.T("common.deletedCamera"), folder, files = fileData });
        }

        var palette = await eventColors.GetAsync(ct);
        var spans = await db.MotionSpans.AsNoTracking()
            .Where(m => cameraIds.Contains(m.CameraId) && m.StartUtc < job.ToUtc && m.EndUtc > job.FromUtc)
            .OrderBy(m => m.StartUtc).Take(MaxEvents)
            .Select(m => new
            {
                m.Id, m.CameraId, m.StartUtc, m.EndUtc, m.Source, m.DetectionKind,
                Category = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.Name : null,
                CategoryColor = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.ColorHex : null,
                Tag = m.EventTagRule != null ? m.EventTagRule.Name : null,
                TagColor = m.EventTagRule != null ? m.EventTagRule.ColorHex : null,
                m.DetectedObjectLabel, m.BestFrameAtUtc, m.BestBoxX, m.BestBoxY, m.BestBoxW, m.BestBoxH, m.MovingCount,
            })
            .ToListAsync(ct);
        var events = spans.Select(m =>
        {
            var (kind, label, color) = m switch
            {
                { Category: not null } => ("detection", m.DetectedObjectLabel ?? m.Category, palette.ColorForAiCategory(m.Category, m.CategoryColor)),
                { DetectionKind: { } k } => ("detection", DetectionDisplay.LocalizedLabel(k), palette.ColorFor(k)),
                { Tag: not null } => ("tag", m.Tag, string.IsNullOrWhiteSpace(m.TagColor) ? palette.MotionColor : m.TagColor),
                _ => ("motion", Loc.T("activity.motion"), palette.MotionColor),
            };
            return new
            {
                id = m.Id,
                cameraId = m.CameraId,
                startUtc = Utc(m.StartUtc),
                endUtc = Utc(m.EndUtc),
                kind,
                category = m.Category ?? m.DetectionKind?.ToString(),
                label,
                color,
                count = m.MovingCount,
                bestFrameUtc = m.BestFrameAtUtc is { } best ? Utc(best) : null,
                box = m.BestBoxX is { } x && m.BestBoxY is { } y && m.BestBoxW is { } w && m.BestBoxH is { } h ? new[] { x, y, w, h } : null,
            };
        }).ToList();

        var bookmarks = await db.Bookmarks.AsNoTracking()
            .Where(b => cameraIds.Contains(b.CameraId) && b.TimestampUtc >= job.FromUtc && b.TimestampUtc < job.ToUtc)
            .OrderBy(b => b.TimestampUtc)
            .Select(b => new { b.CameraId, b.TimestampUtc, b.Note, b.CreatedByUserName })
            .ToListAsync(ct);
        var locks = await db.EvidenceLocks.AsNoTracking()
            .Where(l => cameraIds.Contains(l.CameraId) && l.FromUtc < job.ToUtc && l.ToUtc > job.FromUtc)
            .OrderBy(l => l.FromUtc)
            .Select(l => new { l.CameraId, l.FromUtc, l.ToUtc, l.Reason, l.CreatedByUserName, l.CreatedAtUtc, l.RemovedAtUtc })
            .ToListAsync(ct);

        // What happened to the export, oldest first — the start of its chain of custody.
        var log = new List<object> { new { atUtc = Utc(job.CreatedUtc), action = "requested", by = job.RequestedByUserName } };
        foreach (var item in done.Where(i => i.CompletedUtc is not null).OrderBy(i => i.CompletedUtc))
        {
            log.Add(new
            {
                atUtc = Utc(item.CompletedUtc!.Value),
                action = "exported",
                camera = cameraNames.GetValueOrDefault(item.CameraId),
                node = nodeNames.GetValueOrDefault(item.NodeId!.Value),
                redacted = item.RedactionJson is not null,
                converted = item.ConvertedFilePath is not null,
                signedManifest = manifests.ContainsKey(item.Id),
            });
        }
        log.Add(new { atUtc = Utc(now), action = "packaged", by = packagedBy });

        var failed = job.Items.Where(i => i.Status != ExportItemStatus.Done)
            .Select(i => new { camera = cameraNames.GetValueOrDefault(i.CameraId), error = i.ErrorMessage }).ToList();

        var data = new
        {
            format = DataFormat,
            jobId = job.Id,
            fromUtc = Utc(job.FromUtc),
            toUtc = Utc(job.ToUtc),
            createdUtc = Utc(job.CreatedUtc),
            packagedUtc = Utc(now),
            requestedBy = job.RequestedByUserName,
            packagedBy,
            serverVersion = ServerVersion(),
            siteTimeZone = SiteTimeZone(),
            colors = new { motion = palette.MotionColor, recording = palette.RecordingColor },
            cameras = cameraData,
            events,
            eventsTruncated = spans.Count >= MaxEvents,
            bookmarks = bookmarks.Select(b => new { cameraId = b.CameraId, atUtc = Utc(b.TimestampUtc), note = b.Note, by = b.CreatedByUserName }),
            evidenceLocks = locks.Select(l => new
            {
                cameraId = l.CameraId, fromUtc = Utc(l.FromUtc), toUtc = Utc(l.ToUtc), reason = l.Reason,
                by = l.CreatedByUserName, createdUtc = Utc(l.CreatedAtUtc), removedUtc = l.RemovedAtUtc is { } r ? Utc(r) : null,
            }),
            notExported = failed,
            log,
        };
        var json = JsonSerializer.Serialize(data, Indented);
        files.Add(new ExportPackageFile("data/export.json", Encoding.UTF8.GetBytes(json)));
        files.Add(new ExportPackageFile("data/export.js", Encoding.UTF8.GetBytes(ScriptOf("LARIS_EXPORT", json))));

        var local = DateTime.SpecifyKind(job.FromUtc, DateTimeKind.Utc).ToLocalTime();
        var root = $"LarisVMS export {local:yyyy-MM-dd HHmm}";
        return new ExportPackagePlan
        {
            Job = job,
            RootFolder = root,
            DownloadName = root + ".zip",
            Videos = videos,
            Nodes = nodes,
            Files = files,
        };
    }

    /// <summary>The signed list of every file in the package: path, size and SHA-256. Paths are
    /// relative to the package's folder, with forward slashes.</summary>
    public async Task<byte[]> SignManifestAsync(ExportPackagePlan plan, IEnumerable<(string Path, long Size, string Sha256)> files,
        string? packagedBy, CancellationToken ct = default)
    {
        var payload = new
        {
            format = ManifestFormat,
            jobId = plan.Job.Id,
            createdUtc = Utc(DateTime.UtcNow),
            packagedBy,
            requested = new { fromUtc = Utc(plan.Job.FromUtc), toUtc = Utc(plan.Job.ToUtc) },
            files = files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { path = f.Path, sizeBytes = f.Size, sha256 = f.Sha256 }),
        };
        var payloadText = JsonSerializer.Serialize(payload, Indented);
        var signature = await signing.SignAsync(payloadText, ct);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = ManifestFormat,
            signedPayload = payloadText,
            signature = signature.Signature,
            signatureDer = signature.SignatureDer,
            keyId = signature.KeyId,
            algorithm = signature.Algorithm,
            publicKeyPem = signature.PublicKeyPem,
        }, Indented));
    }

    /// <summary>A file's SHA-256, lower-case hex.</summary>
    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>JSON as a script that sets a global, for a page opened from disk. "&lt;/" is escaped
    /// so no text in the data can end the script early.</summary>
    public static string ScriptOf(string global, string json) =>
        $"window.{global} = {json.Replace("</", "<\\/", StringComparison.Ordinal)};\n";

    /// <summary>A folder name per camera that's safe on Windows, macOS, and Linux: characters a file
    /// name can't hold become "_", and cameras with the same name get " (2)", " (3)".</summary>
    internal static Dictionary<Guid, string> CameraFolders(IReadOnlyList<(Guid Id, string Name)> cameras)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<Guid, string>();
        foreach (var (id, name) in cameras)
        {
            var safe = SafeName(name);
            var candidate = safe;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{safe} ({n})";
            result[id] = candidate;
        }
        return result;
    }

    internal static string SafeName(string name)
    {
        var chars = name.Trim().Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim().TrimEnd('.', ' ');
        if (safe.Length > 60) safe = safe[..60].TrimEnd('.', ' ');
        if (safe.Length == 0 || safe is "." or "..") return "camera";
        // Names Windows keeps for devices.
        var stem = safe.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
            ? "_" + safe
            : safe;
    }

    private static string ServerVersion()
    {
        var version = typeof(ExportPackageBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    /// <summary>The server's time zone, as an IANA name a browser can show times in.</summary>
    private static string? SiteTimeZone()
    {
        var zone = TimeZoneInfo.Local;
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;
    }
}

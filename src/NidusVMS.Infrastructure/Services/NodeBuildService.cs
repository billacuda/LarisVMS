using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

/// <summary>Admin-upload / node-download side of recorder-node auto-update — see INodeBuildService's
/// own doc comment. Port of dploid's AgentVersionsController.Upload + AgentBinaryStore, collapsed into
/// this codebase's service-per-feature-area convention (compare CameraService).</summary>
public class NodeBuildService(ApplicationDbContext db) : INodeBuildService
{
    /// <summary>Deliberately outside the IIS site directory entirely — same %ProgramData%\NidusVMS
    /// root as node.config's DPAPI store (NodeConfigStore) and the data-protection key ring
    /// (Program.cs), just its own subfolder. deploy.ps1's robocopy /MIR only ever touches
    /// $DestinationPath, so a folder that never lives under it needs no /XD exclusion entry — see
    /// deploy.ps1's own storage-root guard for why an uploaded build living *inside* the site
    /// directory would be actively dangerous (the next deploy would silently delete it).</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NidusVMS", "node-builds");

    public async Task<NodeBuildVersion> UploadAsync(Stream content, string fileName, string version, string platform,
        string? notes, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DefaultRoot);

        var id = Guid.NewGuid();
        // Stored under its own new Id, not the uploaded file name — two uploads (even of files that
        // happen to share a name) can never collide, and nothing about the on-disk path needs to be
        // guessable or stable across re-uploads of "the same" version.
        var extension = Path.GetExtension(fileName);
        var storedPath = Path.Combine(DefaultRoot, string.IsNullOrEmpty(extension) ? id.ToString() : $"{id}{extension}");

        long sizeBytes;
        string sha256;
        await using (var fileStream = new FileStream(storedPath, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 81920, useAsync: true))
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }

            sizeBytes = total;
            sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        var entity = new NodeBuildVersion
        {
            Id = id,
            Version = version,
            Platform = platform,
            FilePath = storedPath,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            UploadedAt = DateTime.UtcNow,
            Notes = notes,
        };

        db.NodeBuildVersions.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<NodeBuildVersion?> GetLatestForPlatformAsync(string platform, CancellationToken ct = default)
        => await db.NodeBuildVersions.AsNoTracking()
            .Where(b => b.Platform == platform)
            .OrderByDescending(b => b.UploadedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<List<NodeBuildVersion>> ListAsync(CancellationToken ct = default)
        => await db.NodeBuildVersions.AsNoTracking()
            .OrderByDescending(b => b.UploadedAt)
            .ToListAsync(ct);

    public async Task<NodeBuildVersion?> GetDownloadInfoAsync(Guid buildId, CancellationToken ct = default)
        => await db.NodeBuildVersions.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildId, ct);
}

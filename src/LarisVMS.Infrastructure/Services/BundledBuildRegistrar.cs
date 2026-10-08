using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Registers the node/proxy builds bundled with the web install as Pending — see
/// <see cref="IBundledBuildRegistrar"/>. Package layout (written by build-installers.ps1 /
/// install-web.ps1):
/// <code>
/// packages\node\LarisVMS.Node.exe [+ LarisVMS.Vision.Service.exe] + node-build-version.txt
/// packages\proxy\LarisVMS.Proxy.exe + proxy-build-version.txt
/// packages\cuda-provider\onnxruntime_providers_cuda.dll
/// </code>
/// Failures are logged, never thrown: a missing package only means nothing new to offer nodes.</summary>
public class BundledBuildRegistrar(ApplicationDbContext db, ILogger<BundledBuildRegistrar> logger) : IBundledBuildRegistrar
{
    public const string NodePlatform = "win-x64";
    public const string ProxyPlatform = "proxy-win-x64";

    /// <summary>Where registered binaries are stored — overridable for tests.</summary>
    public string NodeBuildsRoot { get; init; } = NodeBuildService.DefaultRoot;

    /// <summary>Where the CUDA provider is served from (VisionNativeDistributor) — overridable for tests.</summary>
    public string VisionNativeRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "vision-native");

    public async Task RegisterAsync(string packagesRoot, CancellationToken ct = default)
    {
        await TryRegisterAsync(Path.Combine(packagesRoot, "node"), "LarisVMS.Node.exe", "node-build-version.txt",
            NodePlatform, "LarisVMS.Vision.Service.exe", ct);
        await TryRegisterAsync(Path.Combine(packagesRoot, "proxy"), "LarisVMS.Proxy.exe", "proxy-build-version.txt",
            ProxyPlatform, visionExeName: null, ct);
        TrySeedCudaProvider(Path.Combine(packagesRoot, "cuda-provider", "onnxruntime_providers_cuda.dll"));

        // Each upgrade bundles a newer build; without this, every older one still waiting would stay
        // in the approval queue next to it.
        try
        {
            var superseded = await NodeBuildService.SupersedeOutdatedPendingAsync(db, ct);
            if (superseded > 0) logger.LogInformation("Marked {Count} older pending build(s) superseded", superseded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not supersede older pending builds");
        }
    }

    private async Task TryRegisterAsync(string dir, string exeName, string versionFileName, string platform,
        string? visionExeName, CancellationToken ct)
    {
        try
        {
            var exePath = Path.Combine(dir, exeName);
            var versionPath = Path.Combine(dir, versionFileName);
            if (!File.Exists(exePath) || !File.Exists(versionPath)) return;

            var version = (await File.ReadAllTextAsync(versionPath, ct)).Trim();
            if (!System.Version.TryParse(version, out _))
            {
                logger.LogWarning("Bundled {Platform} build has an unreadable version '{Version}' - not registered", platform, version);
                return;
            }

            if (await db.NodeBuildVersions.AnyAsync(b => b.Version == version && b.Platform == platform, ct))
                return;

            Directory.CreateDirectory(NodeBuildsRoot);
            var build = new NodeBuildVersion
            {
                Id = Guid.NewGuid(),
                Version = version,
                Platform = platform,
                Status = NodeBuildStatus.Pending,
                UploadedAt = DateTime.UtcNow,
                Notes = "Bundled with the web install",
            };

            build.FilePath = Path.Combine(NodeBuildsRoot, $"{build.Id}.exe");
            File.Copy(exePath, build.FilePath, overwrite: true);
            (build.Sha256, build.SizeBytes) = await HashAsync(build.FilePath, ct);

            var visionPath = visionExeName is null ? null : Path.Combine(dir, visionExeName);
            if (visionPath is not null && File.Exists(visionPath))
            {
                build.VisionFilePath = Path.Combine(NodeBuildsRoot, $"{build.Id}.vision.exe");
                File.Copy(visionPath, build.VisionFilePath, overwrite: true);
                var (visionHash, visionSize) = await HashAsync(build.VisionFilePath, ct);
                build.VisionSha256 = visionHash;
                build.VisionSizeBytes = visionSize;
            }

            db.NodeBuildVersions.Add(build);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Registered bundled {Platform} build {Version} as Pending", platform, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not register the bundled {Platform} build", platform);
        }
    }

    private void TrySeedCudaProvider(string source)
    {
        try
        {
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(VisionNativeRoot);
            var dest = Path.Combine(VisionNativeRoot, Path.GetFileName(source));
            if (File.Exists(dest) && FileHash(dest) == FileHash(source)) return;
            File.Copy(source, dest, overwrite: true);
            logger.LogInformation("Seeded {File} for NVIDIA nodes to download", Path.GetFileName(source));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not seed the CUDA provider library; NVIDIA nodes keep running DirectML");
        }
    }

    private static async Task<(string Sha256, long Size)> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

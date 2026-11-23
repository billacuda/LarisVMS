using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Registers the node/proxy builds bundled with the web install as Pending — see
/// <see cref="IBundledBuildRegistrar"/>. A Web package carries builds for every platform it supports,
/// so one server can update Windows and Linux nodes and proxies alike. Package layout (written by
/// build-installers.ps1 / install-web.ps1):
/// <code>
/// packages/node/&lt;platform&gt;/LarisVMS.Node[.exe] [+ LarisVMS.Vision.Service[.exe]] + node-build-version.txt
///     [+ onnxruntime-backends.zip + backends-stamp.txt (+ backends.publisher.sig)]
/// packages/proxy/&lt;platform&gt;/LarisVMS.Proxy[.exe] + proxy-build-version.txt
/// packages/cuda-provider/&lt;platform&gt;/onnxruntime_providers_cuda.dll | libonnxruntime_providers_cuda.so
/// packages/cuda-provider-manifest.json
/// </code>
/// The older flat layout (packages/node/LarisVMS.Node.exe, packages/cuda-provider/onnxruntime_providers_cuda.dll)
/// is still read, as win-x64. Failures are logged, never thrown: a missing package only means nothing
/// new to offer nodes.</summary>
public class BundledBuildRegistrar(ApplicationDbContext db, ILogger<BundledBuildRegistrar> logger) : IBundledBuildRegistrar
{
    public const string NodePlatform = LarisVMS.Core.PlatformId.WindowsX64;
    public const string ProxyPlatform = "proxy-" + LarisVMS.Core.PlatformId.WindowsX64;

    /// <summary>Where registered binaries are stored — overridable for tests.</summary>
    public string NodeBuildsRoot { get; init; } = NodeBuildService.DefaultRoot;

    /// <summary>Where the CUDA provider is served from (VisionNativeDistributor) — overridable for tests.</summary>
    public string VisionNativeRoot { get; init; } = LarisVMS.Core.CudaProviderFiles.CacheDirectory;

    public async Task RegisterAsync(string packagesRoot, CancellationToken ct = default)
    {
        foreach (var platform in LarisVMS.Core.PlatformId.Bundled)
        {
            var nodeExe = LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Node, platform);
            var visionExe = LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.VisionService, platform);
            var proxyExe = LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Proxy, platform);

            await TryRegisterAsync(PackageDir(packagesRoot, "node", platform, nodeExe), nodeExe, "node-build-version.txt",
                platform, visionExe, ct);
            await TryRegisterAsync(PackageDir(packagesRoot, "proxy", platform, proxyExe), proxyExe, "proxy-build-version.txt",
                LarisVMS.Core.PlatformId.ForProxy(platform), visionExeName: null, ct);

            var cudaFile = LarisVMS.Core.CudaProviderFiles.FileName(platform);
            TrySeedCudaProvider(Path.Combine(PackageDir(packagesRoot, "cuda-provider", platform, cudaFile), cudaFile), platform);
        }
        TrySeedManifest(Path.Combine(packagesRoot, LarisVMS.Core.CudaProviderFiles.ManifestFileName));

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

    /// <summary>packages/&lt;kind&gt;/&lt;platform&gt;, or for win-x64 the older flat packages/&lt;kind&gt;
    /// when that's where the file is.</summary>
    private static string PackageDir(string packagesRoot, string kind, string platform, string fileName)
    {
        var perPlatform = Path.Combine(packagesRoot, kind, platform);
        var flat = Path.Combine(packagesRoot, kind);
        return platform == LarisVMS.Core.PlatformId.WindowsX64
               && !File.Exists(Path.Combine(perPlatform, fileName)) && File.Exists(Path.Combine(flat, fileName))
            ? flat
            : perPlatform;
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

            build.FilePath = Path.Combine(NodeBuildsRoot, LarisVMS.Core.ExecutableName.For(build.Id.ToString(), platform));
            File.Copy(exePath, build.FilePath, overwrite: true);
            (build.Sha256, build.SizeBytes) = await HashAsync(build.FilePath, ct);

            var visionPath = visionExeName is null ? null : Path.Combine(dir, visionExeName);
            if (visionPath is not null && File.Exists(visionPath))
            {
                build.VisionFilePath = Path.Combine(NodeBuildsRoot, LarisVMS.Core.ExecutableName.For($"{build.Id}.vision", platform));
                File.Copy(visionPath, build.VisionFilePath, overwrite: true);
                var (visionHash, visionSize) = await HashAsync(build.VisionFilePath, ct);
                build.VisionSha256 = visionHash;
                build.VisionSizeBytes = visionSize;
                await TryAddBackendsAsync(build, dir, ct);
            }

            // The publisher's signature, if the build script had the publisher's key (PublisherKey).
            var signaturePath = Path.Combine(dir, LarisVMS.Core.Security.PublisherKey.SignatureFileName);
            if (File.Exists(signaturePath))
                build.PublisherSignature = (await File.ReadAllTextAsync(signaturePath, ct)).Trim();

            db.NodeBuildVersions.Add(build);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Registered bundled {Platform} build {Version} as Pending", platform, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not register the bundled {Platform} build", platform);
        }
    }

    /// <summary>The build's native backend libraries (BackendsBundle), when the package has them:
    /// only offered with a Vision Service binary, since that's what loads them.</summary>
    private async Task TryAddBackendsAsync(NodeBuildVersion build, string dir, CancellationToken ct)
    {
        var zipPath = Path.Combine(dir, LarisVMS.Core.Update.BackendsBundle.ZipFileName);
        var stampPath = Path.Combine(dir, LarisVMS.Core.Update.BackendsBundle.StampTextFileName);
        if (!File.Exists(zipPath) || !File.Exists(stampPath)) return;

        var stamp = (await File.ReadAllTextAsync(stampPath, ct)).Trim().ToLowerInvariant();
        if (stamp.Length != 64 || !stamp.All(Uri.IsHexDigit))
        {
            logger.LogWarning("Bundled {Platform} build has an unreadable backends stamp - its native libraries won't be offered", build.Platform);
            return;
        }

        build.BackendsFilePath = Path.Combine(NodeBuildsRoot, $"{build.Id}.{LarisVMS.Core.Update.BackendsBundle.ZipFileName}");
        File.Copy(zipPath, build.BackendsFilePath, overwrite: true);
        var (hash, size) = await HashAsync(build.BackendsFilePath, ct);
        build.BackendsSha256 = hash;
        build.BackendsSizeBytes = size;
        build.BackendsStamp = stamp;
        var signaturePath = Path.Combine(dir, LarisVMS.Core.Update.BackendsBundle.PublisherSignatureFileName);
        if (File.Exists(signaturePath))
            build.BackendsPublisherSignature = (await File.ReadAllTextAsync(signaturePath, ct)).Trim();
    }

    /// <summary>Copies the package's CUDA provider for <paramref name="platform"/> (and its publisher
    /// signature) to where the server serves it from. A package carries only its own OS's copy; the
    /// other is fetched on demand (VisionNativeDistributor).</summary>
    private void TrySeedCudaProvider(string source, string platform)
    {
        try
        {
            if (!File.Exists(source)) return;
            var dest = LarisVMS.Core.CudaProviderFiles.ServerPath(VisionNativeRoot, platform);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var sourceSignature = LarisVMS.Core.CudaProviderFiles.SignaturePath(source);
            if (File.Exists(sourceSignature))
                File.Copy(sourceSignature, LarisVMS.Core.CudaProviderFiles.SignaturePath(dest), overwrite: true);
            if (File.Exists(dest) && FileHash(dest) == FileHash(source)) return;
            File.Copy(source, dest, overwrite: true);
            logger.LogInformation("Seeded the {Platform} CUDA provider for NVIDIA nodes to download", platform);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not seed the {Platform} CUDA provider library; NVIDIA nodes on it keep their fallback backend", platform);
        }
    }

    /// <summary>Keeps the package's CUDA provider manifest (release asset names and pinned SHA-256s)
    /// beside the server's copies, for fetching the ones the package didn't carry.</summary>
    private void TrySeedManifest(string source)
    {
        try
        {
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(VisionNativeRoot);
            File.Copy(source, Path.Combine(VisionNativeRoot, LarisVMS.Core.CudaProviderFiles.ManifestFileName), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not copy the CUDA provider manifest; the server won't fetch CUDA providers for other platforms");
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

using System.Security.Cryptography;
using System.Threading;
using LarisVMS.Core.Dtos;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Web.Services;

/// <summary>
/// Serves large Vision Service native dependencies that the node package deliberately doesn't bundle
/// (<c>GET /api/nodes/vision-native/{name}</c>). Currently just <c>onnxruntime_providers_cuda.dll</c>
/// (~320 MB) — shipping it in every node package would bloat CPU/DirectML-only installs by that much
/// for a file only NVIDIA nodes ever load, so a node that resolves the CUDA backend fetches it once
/// and caches it.
///
/// Unlike <see cref="DetectionModelDistributor"/> there is no pinned upstream: the file lives inside
/// a NuGet package on the build machine, so <c>deploy.ps1</c> copies it straight into this cache
/// directory (the same <c>%ProgramData%\LarisVMS</c> root as node-builds, chosen so a redeploy's
/// <c>robocopy /MIR</c> of the IIS site can't wipe it). A node with no seeded file just keeps
/// running DirectML.
/// </summary>
public sealed class VisionNativeDistributor(ILogger<VisionNativeDistributor> logger)
{
    /// <summary>Public request name -> the on-disk filename in the cache directory.</summary>
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cuda-provider"] = "onnxruntime_providers_cuda.dll",
    };

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "vision-native");

    // SHA-256 is expensive on a ~320 MB file; cache it keyed on the file's path + last-write time so a
    // reseed by deploy.ps1 invalidates it.
    private readonly Lock _hashLock = new();
    private (string Path, DateTime WriteUtc, VisionNativeInfo Info)? _cachedInfo;

    public static bool IsKnown(string name) => Files.ContainsKey(name);

    private static string? ResolvePath(string name)
    {
        if (!Files.TryGetValue(name, out var fileName)) return null;
        var path = Path.Combine(CacheDirectory, fileName);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>Size + SHA-256 of the seeded file, or null if the server has no copy.</summary>
    public VisionNativeInfo? GetInfo(string name)
    {
        if (ResolvePath(name) is not { } path) return null;

        var writeUtc = File.GetLastWriteTimeUtc(path);
        lock (_hashLock)
        {
            if (_cachedInfo is { } c && c.Path == path && c.WriteUtc == writeUtc) return c.Info;

            using var stream = File.OpenRead(path);
            var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            var info = new VisionNativeInfo(name, sha, new FileInfo(path).Length);
            _cachedInfo = (path, writeUtc, info);
            logger.LogInformation("Vision native '{Name}' available: {Bytes} bytes, sha256 {Sha}.", name, info.SizeBytes, sha);
            return info;
        }
    }

    /// <summary>An open read stream for the seeded file, or null if the server has no copy.</summary>
    public static Stream? Open(string name)
        => ResolvePath(name) is { } path ? File.OpenRead(path) : null;
}

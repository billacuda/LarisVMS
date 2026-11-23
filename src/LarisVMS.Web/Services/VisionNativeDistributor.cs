using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Web.Services;

/// <summary>
/// Serves large Vision Service native dependencies that the node package deliberately doesn't bundle
/// (<c>GET /api/nodes/vision-native/{name}</c>). Currently just the ONNX Runtime CUDA provider
/// (~300 MB): shipping it in every node package would bloat CPU/DirectML/OpenVINO installs by that
/// much for a file only NVIDIA nodes ever load, so a node that resolves the CUDA backend fetches it
/// once and caches it.
///
/// One server serves Windows and Linux nodes, so it keeps one copy per platform (see
/// <see cref="CudaProviderFiles"/>) and answers each node with its own platform's. A Web package
/// carries the copy for its own OS; the other OS's copy is downloaded once, in the background, from the
/// matching GitHub release the first time a node of that OS asks, and only kept if its SHA-256 matches
/// the one the package pinned (<see cref="CudaProviderFiles.ManifestFileName"/>). Until then — or for
/// good, on a server with no internet access — that node keeps running its fallback backend, and its
/// note on the Nodes page says why; copying the file into the cache folder by hand also works.
/// </summary>
public sealed class VisionNativeDistributor(IHttpClientFactory httpFactory, IConfiguration config, ILogger<VisionNativeDistributor> logger)
{
    private const string ReleaseDownloadBase = "https://github.com/billacuda/LarisVMS/releases/download";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    // SHA-256 is expensive on a ~300 MB file; cache it keyed on the file's path + last-write time so a
    // reseed invalidates it.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime WriteUtc, string Sha256, long Size)> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _fetches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastFailure = new(StringComparer.Ordinal);

    public static bool IsKnown(string name) => string.Equals(name, CudaProviderFiles.RequestName, StringComparison.OrdinalIgnoreCase);

    private static string? ResolvePath(string platform)
    {
        var path = CudaProviderFiles.ServerPath(platform);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>Size, SHA-256, and signed name of the copy for <paramref name="platform"/> (and the
    /// publisher's signature, when the build was signed), or null if the server has no copy yet — in
    /// which case it starts fetching one if it can.</summary>
    public VisionNativeInfo? GetInfo(string name, string platform)
    {
        if (!IsKnown(name)) return null;
        if (ResolvePath(platform) is not { } path)
        {
            StartFetch(platform);
            return null;
        }

        var (sha, size) = Hash(path);
        var info = new VisionNativeInfo(CudaProviderFiles.SignedName(platform), sha, size);
        string? publisherSignature = null;
        try
        {
            var signaturePath = CudaProviderFiles.SignaturePath(path);
            if (File.Exists(signaturePath)) publisherSignature = File.ReadAllText(signaturePath).Trim();
        }
        catch (IOException) { /* being replaced; the next request reads it */ }
        return string.IsNullOrEmpty(publisherSignature) ? info : info with { PublisherSignature = publisherSignature };
    }

    /// <summary>An open read stream for the copy for <paramref name="platform"/>, or null if the
    /// server has none.</summary>
    public static Stream? Open(string name, string platform)
        => IsKnown(name) && ResolvePath(platform) is { } path ? File.OpenRead(path) : null;

    private (string Sha256, long Size) Hash(string path)
    {
        var writeUtc = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_hashes.TryGetValue(path, out var cached) && cached.WriteUtc == writeUtc) return (cached.Sha256, cached.Size);
        }

        string sha;
        long size;
        using (var stream = File.OpenRead(path))
        {
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            size = stream.Length;
        }
        lock (_gate) _hashes[path] = (writeUtc, sha, size);
        logger.LogInformation("CUDA provider for {Path} available: {Bytes} bytes, sha256 {Sha}.", path, size, sha);
        return (sha, size);
    }

    private sealed record ManifestEntry(string Asset, string Sha256, string? Release, string? PublisherSignature);

    private static ManifestEntry? ReadManifest(string platform)
    {
        try
        {
            if (!File.Exists(CudaProviderFiles.ManifestPath)) return null;
            var all = JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(
                File.ReadAllText(CudaProviderFiles.ManifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return all is not null && all.TryGetValue(platform, out var entry)
                && !string.IsNullOrWhiteSpace(entry.Asset) && entry.Sha256 is { Length: 64 }
                ? entry
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Starts at most one background download per platform, and not again within an hour of
    /// a failed one.</summary>
    private void StartFetch(string platform)
    {
        if (ReadManifest(platform) is not { } entry) return;
        lock (_gate)
        {
            if (_fetches.TryGetValue(platform, out var running) && !running.IsCompleted) return;
            if (_lastFailure.TryGetValue(platform, out var failedAt) && DateTime.UtcNow - failedAt < RetryAfterFailure) return;
            _fetches[platform] = Task.Run(() => FetchAsync(platform, entry));
        }
    }

    private async Task FetchAsync(string platform, ManifestEntry entry)
    {
        var dest = CudaProviderFiles.ServerPath(platform);
        var tmp = dest + ".tmp";
        // A mirror for servers that can't reach GitHub; must be HTTPS. The pinned SHA-256 still applies.
        var baseUrl = config["VisionNative:DownloadBaseUrl"] is { Length: > 0 } mirror ? mirror.TrimEnd('/') : null;
        if (baseUrl is not null && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var mirrorUri) || mirrorUri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("VisionNative:DownloadBaseUrl must be an https:// URL; not fetching the {Platform} CUDA provider.", platform);
            lock (_gate) _lastFailure[platform] = DateTime.UtcNow;
            return;
        }
        var release = entry.Release ?? LarisVMS.Infrastructure.AppRelease.Version;
        var url = baseUrl is null
            ? $"{ReleaseDownloadBase}/v{release}/{entry.Asset}"
            : $"{baseUrl}/{entry.Asset}";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            logger.LogInformation("Fetching the {Platform} CUDA provider for NVIDIA nodes from {Url} (one-time; about 300 MB).", platform, url);
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(30);
            await using (var upstream = await http.GetStreamAsync(url))
            await using (var file = File.Create(tmp))
                await upstream.CopyToAsync(file);

            string actual;
            await using (var downloaded = File.OpenRead(tmp))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(downloaded));
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{url} isn't the file this server's package expects (sha256 {actual}, expected {entry.Sha256}).");

            File.Move(tmp, dest, overwrite: true);
            if (!string.IsNullOrWhiteSpace(entry.PublisherSignature))
                await File.WriteAllTextAsync(CudaProviderFiles.SignaturePath(dest), entry.PublisherSignature);
            logger.LogInformation("Cached the {Platform} CUDA provider at {Path}; NVIDIA nodes on that platform pick it up on their next check.",
                platform, dest);
        }
        catch (Exception ex)
        {
            lock (_gate) _lastFailure[platform] = DateTime.UtcNow;
            logger.LogWarning(ex,
                "Could not fetch the {Platform} CUDA provider from {Url}; NVIDIA nodes on that platform keep their fallback backend. " +
                "Retrying in an hour, or copy {Asset} from the release to {Path} by hand.",
                platform, url, entry.Asset, dest);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { /* best effort */ } }
        }
    }
}

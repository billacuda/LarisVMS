using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Services;

/// <summary>
/// Serves detection-model <c>.onnx</c> files to recorder nodes (<c>GET /api/nodes/detection-model/{family}/{variant}</c>).
/// YOLOX models aren't bundled in the node package — a node fetches the size it needs from here on
/// first use and caches it locally. The server keeps its own cache directory and, on a miss, fetches
/// once from the pinned upstream URL for that family+variant. The server is the only tier that needs
/// outbound access to the model host; nodes only ever talk to the server.
///
/// Upstream URLs default to the pinned values below, each checked against its SHA-256, and can be
/// overridden per variant (HTTPS only) via configuration — <c>DetectionModels:yolox:s</c>, etc. — e.g. to point at an internal mirror or a
/// re-export. An operator with no outbound access seeds the file directly into the cache dir
/// (<c>{contentRoot}/detection-models/yolox_s.onnx</c>); <c>tools/export-models/fetch_yolox.py</c>
/// produces exactly those filenames.
/// </summary>
public sealed class DetectionModelDistributor(
    IHttpClientFactory httpFactory, IConfiguration config, IWebHostEnvironment env, ILogger<DetectionModelDistributor> logger)
{
    private readonly record struct ModelSource(string FileName, string DefaultUrl, string Sha256);

    // Megvii's own 0.1.1rc0 release ONNX: the standard export, a single 1 x N x 85 output with raw box
    // columns, and YoloXDecoder does the grid decode and NMS. yolox_s is confirmed working (34 MB, CUDA
    // loads it). The other five are the same tag; verify each by switching the node's size and watching
    // its log. Override any that 404 with the DetectionModels:yolox:<size> setting, or seed the cache folder.
    private static readonly Dictionary<string, Dictionary<string, ModelSource>> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yolox"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["nano"] = new("yolox_nano.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.onnx",
                "C789161ED43C8269FCD4E67C67EEEB4E80C622DA2EB296A20BC6007BD18A0B7D"),
            ["tiny"] = new("yolox_tiny.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_tiny.onnx",
                "427CC366D34E27FF7A03E2899B5E3671425C262EA2291F88BB942BC1CC70B0F7"),
            ["s"] = new("yolox_s.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_s.onnx",
                "C5C2D13E59AE883E6AF3B45DAEA64AF4833A4951C92D116EC270D9DDBE998063"),
            ["m"] = new("yolox_m.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_m.onnx",
                "21FF6CFDEB53B013BAC2249599E55F00BFF3CFDFDAB37ED7A4620818C1D15B3F"),
            ["l"] = new("yolox_l.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_l.onnx",
                "7860AE79DE6C89A3C1EB72AE9A2756C0CCFBE04B7791BB5880AFABD97855A411"),
            ["x"] = new("yolox_x.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_x.onnx",
                "C892D7AAF1C4746D8A4D675BEC669A4DB4F434B4EE1EFB654BC9B353379C7C55"),
        },
    };

    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private string CacheDirectory => Path.Combine(env.ContentRootPath, "detection-models");

    public static bool IsKnown(string family, string variant)
        => Sources.TryGetValue(family, out var v) && v.ContainsKey(variant);

    /// <summary>An open read stream for the model file, or null if the family/variant isn't known.
    /// Throws if it isn't cached and can't be fetched.</summary>
    public async Task<Stream?> OpenAsync(string family, string variant, CancellationToken ct)
    {
        if (!Sources.TryGetValue(family, out var byVariant) || !byVariant.TryGetValue(variant, out var source))
            return null;

        var path = Path.Combine(CacheDirectory, source.FileName);
        if (TryOpen(path) is { } hit) return hit;

        await _fetchGate.WaitAsync(ct);
        try
        {
            if (TryOpen(path) is { } hitAfterWait) return hitAfterWait;

            // The default download must be the exact file pinned above, so a swapped upstream file
            // never reaches the nodes. An override (a mirror, a re-export) can't be pinned, but it
            // must be HTTPS.
            var overrideUrl = config[$"DetectionModels:{family}:{variant}"];
            var url = overrideUrl ?? source.DefaultUrl;
            if (overrideUrl is not null
                && (!Uri.TryCreate(overrideUrl, UriKind.Absolute, out var overrideUri) || overrideUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException(Loc.T("server.detectionModelDistributor.detectionmodelsMustBeAnHttps", new { family, variant }));
            var expectedSha256 = overrideUrl is null ? source.Sha256 : null;
            Directory.CreateDirectory(CacheDirectory);
            logger.LogInformation("Fetching detection model {Family}/{Variant} from {Url} (one-time; will be cached).", family, variant, url);

            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            var tmp = path + ".tmp";
            try
            {
                await using (var upstream = await http.GetStreamAsync(url, ct))
                await using (var file = File.Create(tmp))
                    await upstream.CopyToAsync(file, ct);
                if (new FileInfo(tmp).Length == 0) throw new InvalidOperationException(Loc.T("server.detectionModelDistributor.upstreamReturnedAnEmptyFile"));
                if (expectedSha256 is not null)
                {
                    string actual;
                    await using (var downloaded = File.OpenRead(tmp))
                        actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(downloaded, ct));
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(Loc.T("server.detectionModelDistributor.fromIsntTheExpectedFile", new { fileName = source.FileName, url, actual, expectedSha256 }));
                }
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort */ } }
            }

            logger.LogInformation("Cached detection model {Family}/{Variant} to {Path} ({Bytes} bytes).",
                family, variant, path, new FileInfo(path).Length);
            return File.OpenRead(path);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private static Stream? TryOpen(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0 ? File.OpenRead(path) : null;
}

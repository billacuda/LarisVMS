using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Web.Services;

/// <summary>
/// Serves detection-model <c>.onnx</c> files to recorder nodes (<c>GET /api/nodes/detection-model/{family}/{variant}</c>).
/// YOLOX models aren't bundled in the node package — a node fetches the size it needs from here on
/// first use and caches it locally. The server keeps its own cache directory and, on a miss, fetches
/// once from the pinned upstream URL for that family+variant. The server is the only tier that needs
/// outbound access to the model host; nodes only ever talk to the server.
///
/// Upstream URLs default to the pinned values below and can be overridden per variant via
/// configuration — <c>DetectionModels:yolox:s</c>, etc. — e.g. to point at an internal mirror or a
/// re-export. An operator with no outbound access seeds the file directly into the cache dir
/// (<c>{contentRoot}/detection-models/yolox_s.onnx</c>); <c>tools/export-models/fetch_yolox.py</c>
/// produces exactly those filenames.
/// </summary>
public sealed class DetectionModelDistributor(
    IHttpClientFactory httpFactory, IConfiguration config, IWebHostEnvironment env, ILogger<DetectionModelDistributor> logger)
{
    private readonly record struct ModelSource(string FileName, string DefaultUrl);

    // Megvii's own 0.1.1rc0 release ONNX (standard export — single [1,N,85] output, raw box columns;
    // YoloXDecoder does the grid decode + NMS). yolox_s confirmed working (34 MB, CUDA loads it). The
    // other five are the same tag — verify each by switching the node's size and watching its log;
    // override any that 404 via DetectionModels:yolox:<size> config or by seeding the cache dir.
    private static readonly Dictionary<string, Dictionary<string, ModelSource>> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["yolox"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["nano"] = new("yolox_nano.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.onnx"),
            ["tiny"] = new("yolox_tiny.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_tiny.onnx"),
            ["s"] = new("yolox_s.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_s.onnx"),
            ["m"] = new("yolox_m.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_m.onnx"),
            ["l"] = new("yolox_l.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_l.onnx"),
            ["x"] = new("yolox_x.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_x.onnx"),
        },
    };

    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private string CacheDirectory => Path.Combine(env.ContentRootPath, "detection-models");

    public bool IsKnown(string family, string variant)
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

            var url = config[$"DetectionModels:{family}:{variant}"] ?? source.DefaultUrl;
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
                if (new FileInfo(tmp).Length == 0) throw new InvalidOperationException("upstream returned an empty file");
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

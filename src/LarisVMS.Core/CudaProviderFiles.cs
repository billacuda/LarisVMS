namespace LarisVMS.Core;

/// <summary>
/// The ONNX Runtime CUDA provider library (~300 MB) that the node package leaves out and NVIDIA nodes
/// fetch from the server instead: its file name per platform, where the server keeps it, and the name
/// its signature covers. One server serves Windows and Linux nodes, so it keeps one copy per platform.
///
/// Windows keeps the original location (the cache folder itself) and signature name
/// ("cuda-provider"), so a server upgraded in place and nodes already running keep working. Other
/// platforms get a subfolder and a platform-qualified signature name ("cuda-provider-linux-x64"), so a
/// file signed for one platform can never pass a node of another.
/// </summary>
public static class CudaProviderFiles
{
    /// <summary>The name nodes request it by (<c>/api/nodes/vision-native/cuda-provider</c>).</summary>
    public const string RequestName = "cuda-provider";

    /// <summary>The manifest a Web package carries (and the server keeps beside its copies): per
    /// platform, the release asset to fetch, its SHA-256, and the publisher's signature.</summary>
    public const string ManifestFileName = "cuda-provider-manifest.json";

    public static string CacheDirectory => Path.Combine(LarisPaths.DataRoot, "vision-native");

    public static string ManifestPath => Path.Combine(CacheDirectory, ManifestFileName);

    public static string FileName(string platform) => PlatformId.IsWindows(platform)
        ? "onnxruntime_providers_cuda.dll"
        : "libonnxruntime_providers_cuda.so";

    /// <summary>The name its signature payload covers (UpdateSignature.NativePayload).</summary>
    public static string SignedName(string platform) =>
        platform == PlatformId.WindowsX64 ? RequestName : $"{RequestName}-{platform}";

    /// <summary>The release asset name ("onnxruntime_providers_cuda-linux-x64.so").</summary>
    public static string AssetName(string platform) => PlatformId.IsWindows(platform)
        ? $"onnxruntime_providers_cuda-{platform}.dll"
        : $"onnxruntime_providers_cuda-{platform}.so";

    /// <summary>Where the server keeps the copy for <paramref name="platform"/>.</summary>
    public static string ServerPath(string platform) => ServerPath(CacheDirectory, platform);

    /// <summary><see cref="ServerPath(string)"/> under another cache folder (tests).</summary>
    public static string ServerPath(string cacheDirectory, string platform) => platform == PlatformId.WindowsX64
        ? Path.Combine(cacheDirectory, FileName(platform))
        : Path.Combine(cacheDirectory, platform, FileName(platform));

    /// <summary>The publisher's signature, written next to a copy.</summary>
    public static string SignaturePath(string filePath) => filePath + ".publisher.sig";
}

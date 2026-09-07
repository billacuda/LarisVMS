namespace LarisVMS.Media;

/// <summary>
/// Cached hover thumbnails and AI-detection snapshot crops are written as WebP where the node can
/// produce it (SkiaSharp always can; ffmpeg needs a libwebp-enabled build — see
/// FfmpegCapabilityProber.HasLibWebpAsync), and as JPEG otherwise. Legacy <c>.jpg</c> files already
/// on disk keep being read and served — there is no re-encode pass; each is replaced by a WebP the
/// next time that preview is regenerated.
///
/// One place for the extension / content-type / "which file exists" rules so the read, write and
/// retention/archive-sweep sites (Node/Program.cs, ThumbnailBackfillService, StorageManager) all
/// agree on them.
/// </summary>
public static class CachedImageFormat
{
    public const string WebpExtension = ".webp";
    public const string JpegExtension = ".jpg";

    /// <summary>Both cache extensions, WebP first — the order a reader probes and a sweep matches.</summary>
    public static readonly string[] Extensions = [WebpExtension, JpegExtension];

    public static string Extension(bool webp) => webp ? WebpExtension : JpegExtension;

    public static string ContentType(string path) =>
        path.EndsWith(WebpExtension, StringComparison.OrdinalIgnoreCase) ? "image/webp" : "image/jpeg";

    /// <summary><paramref name="stem"/> is a cache-file path without its image extension. Returns the
    /// file that exists — a WebP in preference to a legacy JPEG — or null if neither is present.</summary>
    public static string? FindExisting(string stem)
    {
        foreach (var ext in Extensions)
        {
            var candidate = stem + ext;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Directory.EnumerateFiles once per cache extension for a glob that stops before the
    /// extension (e.g. <c>"seg_o*"</c>, <c>"*_span*"</c>, <c>"*"</c>) so a retention or archive sweep
    /// picks up both new WebP files and legacy JPEGs.</summary>
    public static IEnumerable<string> EnumerateFiles(string directory, string stemGlob, SearchOption option)
    {
        foreach (var ext in Extensions)
            foreach (var file in Directory.EnumerateFiles(directory, stemGlob + ext, option))
                yield return file;
    }
}

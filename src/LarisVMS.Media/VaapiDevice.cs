namespace LarisVMS.Media;

/// <summary>
/// The VAAPI render node ffmpeg encodes on, on Linux: the first <c>/dev/dri/renderD*</c> whose GPU is
/// Intel or AMD. Picked by vendor because a machine with an Intel iGPU and an NVIDIA card has a render
/// node for each, and VAAPI doesn't work on the NVIDIA one (NVIDIA encodes with NVENC instead). Null on
/// Windows, or when there's no such GPU, so the VAAPI encoders are then not offered at all.
/// </summary>
public static class VaapiDevice
{
    private static readonly Lazy<string?> Cached = new(() => OperatingSystem.IsLinux() ? Find("/sys/class/drm", "/dev/dri") : null);

    public static string? Path => Cached.Value;

    /// <summary>Parameters are folders so tests can point them at a fake tree.</summary>
    internal static string? Find(string drmRoot, string devRoot)
    {
        if (!Directory.Exists(drmRoot)) return null;
        foreach (var node in Directory.EnumerateFileSystemEntries(drmRoot, "renderD*").Order(StringComparer.Ordinal))
        {
            string vendor;
            try { vendor = File.ReadAllText(System.IO.Path.Combine(node, "device", "vendor")).Trim().ToLowerInvariant(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (vendor is "0x8086" or "0x1002") return System.IO.Path.Combine(devRoot, System.IO.Path.GetFileName(node));
        }
        return null;
    }
}

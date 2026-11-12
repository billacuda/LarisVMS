namespace LarisVMS.Core.Security;

/// <summary>Stream addresses a camera reports (ONVIF GetStreamUri), which the recorder node later
/// hands to ffmpeg as its input. ffmpeg also opens file:, concat:, subfile:, http: and more, so
/// anything but RTSP is refused: otherwise a camera could point the node (running as LocalSystem)
/// at its local files, such as another camera's footage, or at internal hosts.</summary>
public static class StreamUris
{
    public static bool IsAllowed(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsAllowed(parsed);

    public static bool IsAllowed(Uri uri) =>
        uri.Scheme is "rtsp" or "rtsps" && !string.IsNullOrEmpty(uri.Host);

    /// <summary>The reported stream address on the camera's own host (the one it was added with),
    /// keeping the reported port, path, and query. The node sends the camera's password to this
    /// address, so a spoofed or tampered camera can't point it at another machine; it also fixes
    /// cameras that report an internal address they can't be reached at.</summary>
    public static Uri OnCameraHost(Uri reported, string? cameraHost)
    {
        var host = cameraHost?.Trim().TrimStart('[').TrimEnd(']');
        if (string.IsNullOrEmpty(host) || string.Equals(host, reported.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            return reported;
        return new UriBuilder(reported) { Host = host }.Uri;
    }
}

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
}

using Microsoft.AspNetCore.Http;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Which routes serve proxied camera bytes (live WebSocket relay, playback segments/thumbnails,
/// export downloads, on-demand snapshots) rather than the management UI or its config/status APIs —
/// the boundary PortSegmentationMiddleware enforces once a custom live/playback port is configured.
///
/// Kept as one explicit list rather than derived from routing metadata, specifically so it's a
/// single, obvious place to extend: a new /live, /playback-*, /export-download, or camera-media
/// route added to Program.cs needs one line added here, not a metadata attribute threaded through a
/// minimal-API pipeline whose exact middleware ordering this app doesn't otherwise depend on.
/// </summary>
public static class MediaRoutes
{
    private static readonly string[] Prefixes = ["/live", "/playback-segment", "/playback-thumbnail", "/export-download"];

    public static bool IsMediaPath(PathString path)
    {
        if (Prefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        var value = path.Value;
        if (value is null) return false;

        // /api/cameras/{id}/snapshot — a dynamic segment in the middle rules out a plain prefix
        // match, but /api/cameras/ itself also covers non-media JSON endpoints (motion-state,
        // detection-state) that must stay reachable on the management port, so this checks both ends.
        return value.StartsWith("/api/cameras/", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith("/snapshot", StringComparison.OrdinalIgnoreCase);
    }
}

namespace LarisVMS.Node;

/// <summary>
/// The node's media endpoints (/playback-segment, /playback-thumbnail, /snapshot-image, /export)
/// serve a segment file whose path the web tier sends verbatim from the Segment row. Archive storage
/// (phase 2) means that path can be under either this node's primary storage root or its archive
/// root — both keep the identical <c>cam-{cameraId}/main/…</c> layout. This resolves which of the two
/// a requested path belongs to (the security check — the path must sit under a known
/// <c>cam-{cameraId}/main</c> for the camera in the signed token), and hands back the matched
/// <c>main</c> directory plus its sibling <c>thumbs</c>/<c>snapshots</c> cache directories so the
/// existing <c>Path.GetRelativePath(mainDir, …)</c> cache-path derivation works unchanged for an
/// archived segment too.
/// </summary>
internal static class MediaPathResolver
{
    /// <summary>True when <paramref name="fullPath"/> (already GetFullPath'd) sits under this camera's
    /// <c>main</c> directory on the primary root or (if configured) the archive root. Out-params are
    /// the matched <c>main</c> dir (trailing separator) and its sibling <c>thumbs</c>/<c>snapshots</c>
    /// dirs; all empty when the method returns false.</summary>
    internal static bool TryResolve(string fullPath, Guid cameraId, string storageRoot, string? archiveRoot,
        out string mainDir, out string thumbsDir, out string snapshotsDir)
    {
        if (TryMatchRoot(fullPath, cameraId, storageRoot, out mainDir, out thumbsDir, out snapshotsDir))
            return true;
        if (!string.IsNullOrWhiteSpace(archiveRoot)
            && TryMatchRoot(fullPath, cameraId, archiveRoot!, out mainDir, out thumbsDir, out snapshotsDir))
            return true;

        // Fallback: the path is shaped like this camera's recording directory (…\cam-{id}\main\…)
        // but sits under neither currently-configured root — footage recorded while the node's
        // storage root was temporarily pointed elsewhere and then changed back. The signed media
        // token already binds this exact path to this camera and a real Segment row, and the caller
        // still checks File.Exists, so serving it from wherever it actually is keeps that footage
        // reachable across a storage-root change rather than 404-ing every thumbnail and clip.
        var sep = Path.DirectorySeparatorChar;
        var marker = $"{sep}cam-{cameraId}{sep}main{sep}";
        var idx = fullPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            mainDir = fullPath[..(idx + marker.Length)];              // …\cam-{id}\main\
            var cameraDir = Path.GetDirectoryName(mainDir.TrimEnd(sep))!;  // …\cam-{id}
            thumbsDir = Path.Combine(cameraDir, "thumbs");
            snapshotsDir = Path.Combine(cameraDir, "snapshots");
            return true;
        }

        mainDir = thumbsDir = snapshotsDir = "";
        return false;
    }

    /// <summary>The primary-or-archive check without the cache-dir out-params — for /playback-segment
    /// and /export, which only need "is this path allowed".</summary>
    internal static bool IsAllowed(string fullPath, Guid cameraId, string storageRoot, string? archiveRoot)
        => TryResolve(fullPath, cameraId, storageRoot, archiveRoot, out _, out _, out _);

    private static bool TryMatchRoot(string fullPath, Guid cameraId, string root,
        out string mainDir, out string thumbsDir, out string snapshotsDir)
    {
        mainDir = thumbsDir = snapshotsDir = "";
        string cameraRoot, candidateMain;
        try
        {
            cameraRoot = Path.GetFullPath(Path.Combine(root, $"cam-{cameraId}"));
            candidateMain = Path.Combine(cameraRoot, "main") + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!fullPath.StartsWith(candidateMain, StringComparison.OrdinalIgnoreCase)) return false;

        mainDir = candidateMain;
        thumbsDir = Path.Combine(cameraRoot, "thumbs");
        snapshotsDir = Path.Combine(cameraRoot, "snapshots");
        return true;
    }
}

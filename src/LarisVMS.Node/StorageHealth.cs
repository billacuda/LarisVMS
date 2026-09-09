namespace LarisVMS.Node;

/// <summary>
/// Decides whether this node's storage is actually usable right now, before any code is allowed to
/// infer "this file is gone, so delete its database row" from a <see cref="File.Exists"/> miss.
///
/// The distinction matters because <see cref="File.Exists"/> answers <c>false</c> — not an error — for
/// a path on an unreachable SMB share. Without a health check ahead of it, a share that blips during
/// the reconcile sweep makes *every* segment this node owns look deleted at once, and the web tier
/// dutifully drops thousands of rows for footage that is sitting safely on disk. This node's own logs
/// already show real <c>storage I/O error</c> events against its share, so that is a live hazard here,
/// not a theoretical one.
/// </summary>
public static class StorageHealth
{
    /// <summary>Proves the storage root is genuinely readable and writable right now by round-tripping
    /// a small canary file, rather than trusting <see cref="Directory.Exists"/> — which can answer
    /// <c>true</c> from a cached mount entry whose underlying share has since gone away, exactly the
    /// case this guard exists for. Any failure means "unknown", and callers must treat unknown as
    /// "don't infer deletions", never as "everything is missing".</summary>
    public static bool CanReachStorage(string storageRoot)
    {
        // A GUID name rather than a fixed one so two nodes pointed at the same share (or a stale file
        // left by a previous crash) can never collide or make each other's probe fail.
        var probePath = Path.Combine(storageRoot, $".larisvms-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!Directory.Exists(storageRoot)) return false;

            var payload = Guid.NewGuid().ToString("N");
            File.WriteAllText(probePath, payload);
            // Read back, not just write: a half-broken share can accept a write into a local cache and
            // still not be serving reads, which is precisely the state that produces false "missing".
            var readBack = File.ReadAllText(probePath);
            return readBack == payload;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort — swept as a stray temp file later */ }
        }
    }

    // How the once-per-sweep archive-usability probe backs off before concluding the archive volume
    // is unreachable. A dropped SMB session or a USB archive disk that briefly stops responding
    // recovers within a few seconds; without this, a single failed probe disables archiving for the
    // whole sweep and every aged-out or watermark-selected segment from an archive-enabled camera is
    // DELETED instead of moved, which is unrecoverable. ~6.5s total is nothing against the 5-minute
    // sweep cadence and is cheap insurance against that outcome.
    private static readonly TimeSpan[] ReachRetryBackoff =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>The same canary round-trip as <see cref="CanReachStorage"/>, retried a few times over
    /// a few seconds before giving up — for the once-per-sweep "is the archive volume usable this
    /// sweep" gate specifically, where a transient blip being read as "unreachable" sends footage to
    /// deletion rather than the archive. The reconcile guards deliberately keep using the single-shot
    /// probe: they already re-probe on their own schedule and must not be slowed.</summary>
    public static async Task<bool> CanReachStorageWithRetryAsync(
        string storageRoot, CancellationToken ct, IReadOnlyList<TimeSpan>? backoff = null)
    {
        backoff ??= ReachRetryBackoff;
        for (var attempt = 0; ; attempt++)
        {
            if (CanReachStorage(storageRoot)) return true;
            if (attempt >= backoff.Count) return false;
            try { await Task.Delay(backoff[attempt], ct); }
            catch (OperationCanceledException) { return false; }
        }
    }

    /// <summary>A second, independent guard behind <see cref="CanReachStorage"/>, for the case a share
    /// drops *after* the probe passes but *during* the scan: past this fraction of a node's known
    /// segments appearing to vanish at once, a storage fault is overwhelmingly more likely than that
    /// many genuine deletions this sweep never performed itself. Deletions this node actually carried
    /// out are reported directly from the eviction loops and never depend on this inference at all, so
    /// refusing to act here only ever delays cleaning up genuine orphans until the next sweep — the
    /// safe direction, against permanently dropping rows for footage that still exists.</summary>
    public const double MaxPlausibleMissingFraction = 0.10;

    /// <summary>Small absolute counts are exempt from the fraction rule above: a node holding a handful
    /// of segments can legitimately have most of them evicted between sweeps, where the same ratio
    /// across thousands of rows never plausibly means anything but a fault.</summary>
    public const int MinCountForFractionCheck = 50;

    public static bool IsImplausibleMissingCount(int missingCount, int knownCount)
        => knownCount >= MinCountForFractionCheck
           && missingCount > knownCount * MaxPlausibleMissingFraction;
}

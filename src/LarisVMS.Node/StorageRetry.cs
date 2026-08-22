using Microsoft.Extensions.Logging;

namespace LarisVMS.Node;

/// <summary>
/// Retries a storage operation against the shared recording storage a few times with a short backoff
/// before giving up — confirmed live (2026-08-21) as a real, if intermittent, need: this deployment's
/// SMB path to its NAS dropped for a stretch, surfacing as
/// <c>System.IO.IOException: An unexpected network error occurred</c> from a directory enumeration
/// (ThumbnailBackfillService) that killed an entire backfill pass over one transient blip, and
/// plausibly from individual file opens (segment serving, fragment indexing) sharing the same UNC
/// path that simply didn't happen to throw during that particular window rather than being immune to
/// it. The node process itself recovered fine on restart (fresh SMB session); this exists so a
/// similarly brief future blip doesn't need a restart at all.
///
/// Scoped to <see cref="IOException"/> specifically, the one exception type actually observed for
/// this failure class — a genuine "file doesn't exist" is <see cref="FileNotFoundException"/> or
/// <see cref="DirectoryNotFoundException"/> (both non-retriable, and both derive from IOException, so
/// they're deliberately NOT caught more narrowly here... See the doc note below on that trade-off.
///
/// A missing file is expected to surface as `IOException` too by the time these retries are
/// exhausted — three retries (~4.3s total) is short enough that masking a real "file genuinely
/// doesn't exist" behind a few pointless attempts costs nothing meaningful, and there's no reliable
/// way to distinguish "share is down" from "path was deleted" from the exception type alone (Windows
/// reports both as IOException over SMB in different circumstances) — so this trades a few hundred
/// milliseconds of pointless retrying on the rare genuine-404 case for actually recovering the far
/// more disruptive dropped-share case, rather than trying to guess which one it is from the message
/// text.
/// </summary>
public static class StorageRetry
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    public static async Task<T> ExecuteAsync<T>(ILogger logger, string what, Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (IOException ex) when (attempt < Delays.Length)
            {
                logger.LogWarning(ex, "{What} hit a storage I/O error (attempt {Attempt}/{Max}) — retrying in {DelayMs}ms.",
                    what, attempt + 1, Delays.Length + 1, Delays[attempt].TotalMilliseconds);
                await Task.Delay(Delays[attempt], ct);
            }
        }
    }

    /// <summary>Synchronous form — File.OpenRead/Mp4FragmentIndexer.Build/Directory.EnumerateFiles
    /// have no async equivalent worth threading through here, and a Thread.Sleep between attempts is
    /// fine for these short delays on a background-service or low-volume request-thread caller that
    /// isn't blocking anything latency-sensitive by doing so.</summary>
    public static T Execute<T>(ILogger logger, string what, Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException ex) when (attempt < Delays.Length)
            {
                logger.LogWarning(ex, "{What} hit a storage I/O error (attempt {Attempt}/{Max}) — retrying in {DelayMs}ms.",
                    what, attempt + 1, Delays.Length + 1, Delays[attempt].TotalMilliseconds);
                Thread.Sleep(Delays[attempt]);
            }
        }
    }
}

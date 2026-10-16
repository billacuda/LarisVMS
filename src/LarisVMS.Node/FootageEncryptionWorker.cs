using LarisVMS.Core;

namespace LarisVMS.Node;

/// <summary>
/// Encrypts footage at rest for cameras whose Storage.Encrypt is on (FootageStore, FootageCrypto).
///
/// Finished segments are queued right after they've been reported (so their tamper-evidence hash is
/// of the plain file), and talk recordings after each press. A file that can't be done yet — no key
/// (server unreachable), or busy being played back — is retried a minute later.
///
/// A sweep every 30 minutes (and shortly after startup) catches anything the queue missed, such as
/// segments finished just before a restart: plain files from the last 24 hours. When someone asks for
/// a camera's existing footage to be encrypted (EncryptExistingRequestedUtc newer than this node's
/// last full sweep of it), the sweep covers everything that camera has on this node. Files still being
/// written (changed in the last two minutes) are always left alone. Only the gap between a segment
/// finishing and being encrypted is ever plaintext on disk.
/// </summary>
public sealed class FootageEncryptionWorker(FootageStore store, NodeWorker worker, ILogger<FootageEncryptionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SettleAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);
    private const int MaxAttempts = 30;

    private readonly Dictionary<Guid, DateTime> _fullSweepDone = [];
    private readonly List<(FootageEncryptionJob Job, DateTime DueUtc, int Attempts)> _retries = [];
    private readonly Lock _retryGate = new();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) =>
        path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var housekeeping = HousekeepingAsync(stoppingToken);
        try
        {
            await foreach (var job in store.EncryptionQueue.ReadAllAsync(stoppingToken))
            {
                lock (_retryGate) _queued.Remove(job.Path);
                await ProcessAsync(job, attempts: 0, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        await housekeeping;
    }

    private async Task ProcessAsync(FootageEncryptionJob job, int attempts, CancellationToken ct)
    {
        var mode = worker.TryGetCameraConfig(job.CameraId)?.EncryptFootage ?? "Off";
        if (mode == "Off" || (IsImage(job.Path) && mode != "VideoAndImages")) return;
        try
        {
            if (await store.EncryptFileAsync(job.Path, job.CameraId, job.RecordedUtc, ct)) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Encrypting {Path} failed; will retry.", job.Path);
        }
        if (attempts + 1 >= MaxAttempts)
        {
            logger.LogWarning("Gave up encrypting {Path} for now after {Attempts} attempts; the next sweep will try again.", job.Path, attempts + 1);
            return;
        }
        lock (_retryGate) _retries.Add((job, DateTime.UtcNow + RetryDelay, attempts + 1));
    }

    private async Task HousekeepingAsync(CancellationToken ct)
    {
        var nextSweep = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { return; }

            List<(FootageEncryptionJob Job, DateTime DueUtc, int Attempts)> due;
            lock (_retryGate)
            {
                due = _retries.Where(r => r.DueUtc <= DateTime.UtcNow).ToList();
                _retries.RemoveAll(r => r.DueUtc <= DateTime.UtcNow);
            }
            foreach (var r in due)
            {
                try { await ProcessAsync(r.Job, r.Attempts, ct); }
                catch (OperationCanceledException) { return; }
            }

            var fullRequested = worker.CameraConfigs.Any(c => c.EncryptExistingRequestedUtc is { } at
                && (!_fullSweepDone.TryGetValue(c.CameraId, out var done) || done < at));
            if (DateTime.UtcNow < nextSweep && !fullRequested) continue;
            nextSweep = DateTime.UtcNow + SweepInterval;
            try { Sweep(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Footage encryption sweep hit a storage error; will try again next time.");
            }
        }
    }

    /// <summary>Queues every plain footage file of each encrypting camera that's due: the last day's,
    /// or all of them when existing footage was asked for.</summary>
    private void Sweep()
    {
        var roots = new[] { worker.StorageRoot, worker.ArchiveRoot }.Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
        var now = DateTime.UtcNow;
        var queued = 0;
        foreach (var camera in worker.CameraConfigs.Where(c => c.EncryptFootage != "Off"))
        {
            var full = camera.EncryptExistingRequestedUtc is { } at
                && (!_fullSweepDone.TryGetValue(camera.CameraId, out var done) || done < at);
            var since = full ? DateTime.MinValue : now - RecentWindow;
            var images = camera.EncryptFootage == "VideoAndImages";

            foreach (var root in roots)
            {
                var cameraDir = Path.Combine(root!, $"cam-{camera.CameraId}");
                if (!Directory.Exists(cameraDir)) continue;
                var folders = new List<(string Dir, string Pattern)> { ("main", "*.mp4"), ("talk", "*.wav") };
                if (images) folders.AddRange([("thumbs", "*.*"), ("snapshots", "*.*")]);

                foreach (var (folder, pattern) in folders)
                {
                    var dir = Path.Combine(cameraDir, folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        if (folder is "thumbs" or "snapshots" && !IsImage(file)) continue;
                        DateTime modified;
                        try { modified = File.GetLastWriteTimeUtc(file); }
                        catch (IOException) { continue; }
                        if (modified < since || now - modified < SettleAge) continue;
                        if (FootageCrypto.IsEncrypted(file)) continue;
                        lock (_retryGate) { if (!_queued.Add(file)) continue; }
                        store.EnqueueEncryption(camera.CameraId, file, modified);
                        queued++;
                    }
                }
            }
            if (full) _fullSweepDone[camera.CameraId] = now;
        }
        if (queued > 0) logger.LogInformation("Footage encryption sweep queued {Count} file(s).", queued);
    }
}

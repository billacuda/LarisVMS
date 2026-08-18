using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>
/// Re-probes every enabled camera once a day at an admin-chosen local time, so a camera that gains,
/// loses, or re-encodes a stream is picked up without anyone remembering to press Re-probe. Probing
/// already carries per-stream IsEnabled and CustomName forward (see CameraService.ReplaceStreamsAsync)
/// and matches by ProfileToken, so a routine re-probe of an unchanged camera is a no-op rather than a
/// reset.
///
/// Local time, not UTC: "run at 3am" means the operator's 3am, which is the same decision Schedule
/// recording mode already made. The schedule is evaluated on a one-minute tick rather than by sleeping
/// until the next occurrence, because the configured time can change under it at any moment and a
/// long sleep would keep the old one until the app restarted.
/// </summary>
public class CameraReprobeService(IServiceScopeFactory scopeFactory, ILogger<CameraReprobeService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    public const string EnabledKey = "Cameras.DailyReprobeEnabled";
    public const string AtLocalTimeKey = "Cameras.DailyReprobeAtLocalTime";
    public const bool DefaultEnabled = true;
    public const string DefaultAtLocalTime = "03:00";

    /// <summary>The last local date this actually ran, so a restart mid-window doesn't re-run and a
    /// tick that lands on the same minute twice doesn't either. Deliberately in memory: a missed run
    /// because the app was down at 3am is not worth persisting state to catch up on — the next day's
    /// run does the same work.</summary>
    private DateOnly? _lastRunLocalDate;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Daily camera re-probe tick failed — will retry on the next tick.");
            }

            try { await Task.Delay(TickInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();

        if (!await settings.GetAsync(EnabledKey, DefaultEnabled, ct: ct)) return;

        var scheduled = ParseTimeOfDay(await settings.GetRawAsync(AtLocalTimeKey, ct: ct));
        var now = DateTime.Now;
        if (!ShouldRun(_lastRunLocalDate, now, scheduled)) return;

        // Stamped before the work, not after: a re-probe pass over many cameras can outlast the tick
        // interval, and this is what stops a second tick starting the same pass concurrently.
        _lastRunLocalDate = DateOnly.FromDateTime(now);

        var cameraService = scope.ServiceProvider.GetRequiredService<ICameraService>();
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var cameras = (await cameraService.ListAsync(ct)).Where(c => c.IsEnabled).ToList();
        logger.LogInformation("Daily re-probe starting for {Count} enabled camera(s).", cameras.Count);

        var succeeded = 0;
        var failed = new List<string>();
        foreach (var camera in cameras)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Sequential on purpose. Probing opens real ONVIF conversations with a device, and a
                // fleet-wide burst of them at 3am is the kind of thing that makes cheap cameras drop
                // their other connections — including the RTSP session being recorded.
                var summary = await cameraService.ProbeAsync(camera.Id, ct);
                if (summary.Error is null) succeeded++;
                else failed.Add($"{camera.Name}: {summary.Error}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable camera must not end the pass for every camera after it.
                failed.Add($"{camera.Name}: {ex.Message}");
                logger.LogWarning(ex, "Daily re-probe failed for camera {CameraId} ({Name}).", camera.Id, camera.Name);
            }
        }

        var details = failed.Count == 0
            ? $"{succeeded} camera(s) re-probed successfully"
            : $"{succeeded} succeeded, {failed.Count} failed — {string.Join("; ", failed)}";
        logger.LogInformation("Daily re-probe finished: {Details}", details);

        // Actor is null — nobody triggered this. The audit trail should still carry it, since a
        // re-probe can change what a camera records.
        await auditService.LogAsync("Camera.DailyReprobe", null, "System", null, details);
    }

    /// <summary>"HH:mm" (24-hour) if it parses, otherwise the default. Never throws — a value typed
    /// into an admin field or edited in the database must not be able to stop the service.</summary>
    internal static TimeOnly ParseTimeOfDay(string? value)
        => TimeOnly.TryParse(value, out var parsed) ? parsed : TimeOnly.Parse(DefaultAtLocalTime);

    /// <summary>Run when the local clock has reached the scheduled time and today's run hasn't
    /// happened yet. Catch-up is deliberate: a tick at 03:07 still runs a 03:00 schedule, so a
    /// missed minute (GC pause, a slow previous tick, the app starting at 03:04) doesn't silently
    /// skip the whole day. The consequence is that starting the app at 5pm runs a 3am schedule
    /// immediately that first time, which is a one-off re-probe rather than a correctness problem.</summary>
    internal static bool ShouldRun(DateOnly? lastRunLocalDate, DateTime nowLocal, TimeOnly scheduled)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        if (lastRunLocalDate == today) return false;
        return TimeOnly.FromDateTime(nowLocal) >= scheduled;
    }
}

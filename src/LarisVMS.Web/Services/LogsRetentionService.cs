using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Web.Services;

/// <summary>M11: sweeps FileLoggerProvider's own daily-rolling files out of ".\logs" once they're
/// past the configured retention window — same "app-" prefix scope Program.cs's provider registration
/// uses, so this never touches IIS's own "stdout_*" files sitting in the same directory.
///
/// Retention is read from the `Logs.RetentionDays` global setting on every sweep rather than captured
/// once at startup, so a change on Admin &gt; Settings takes effect on the next cycle without an app
/// pool recycle. 0 (or any non-positive value) means keep forever — the same "0 = unlimited"
/// convention the rest of this app's retention settings use. LarisVMS.Node's own sweep
/// (StorageManager.SweepLogsDirectory) still uses its own fixed window; wiring this setting through
/// to nodes needs a NodeConfig field and a node release, so it is deliberately not part of this
/// change.</summary>
public class LogsRetentionService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<LogsRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    public const string RetentionDaysKey = "Logs.RetentionDays";
    public const int DefaultRetentionDays = 14;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // A scope per cycle, matching ExportJobDispatcher's own pattern — a BackgroundService
                // is a singleton and can't hold a scoped ISettingsResolver/DbContext across cycles.
                using var scope = scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
                var days = await settings.GetAsync(RetentionDaysKey, DefaultRetentionDays, ct: ct);
                Sweep(days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "LogsRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Internal rather than private so the retention decision itself is unit-testable
    /// without a file system — see ShouldDelete.</summary>
    internal void Sweep(int retentionDays)
    {
        if (retentionDays <= 0) return; // keep forever

        var logsDir = LogPaths.AppLogsDirectory(configuration);
        if (!Directory.Exists(logsDir)) return;

        var now = DateTime.UtcNow;
        foreach (var path in Directory.EnumerateFiles(logsDir, "app-*.log", SearchOption.TopDirectoryOnly))
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (IOException) { continue; }

            if (!ShouldDelete(info.LastWriteTimeUtc, now, retentionDays)) continue;
            try { File.Delete(path); }
            catch (IOException ex) { logger.LogWarning(ex, "Could not delete expired log file {Path}", path); }
        }
    }

    /// <summary>Pure retention decision: is a file last written at <paramref name="lastWriteUtc"/>
    /// older than the window? A non-positive window keeps everything, which is what makes 0 mean
    /// "never delete" rather than "delete immediately" — the dangerous reading of the same value.</summary>
    internal static bool ShouldDelete(DateTime lastWriteUtc, DateTime nowUtc, int retentionDays)
        => retentionDays > 0 && nowUtc - lastWriteUtc > TimeSpan.FromDays(retentionDays);
}

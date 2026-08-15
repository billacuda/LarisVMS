namespace LarisVMS.Web.Services;

/// <summary>M11: sweeps FileLoggerProvider's own daily-rolling files out of ".\logs" once they're
/// past a fixed retention window — same "app-" prefix scope Program.cs's provider registration uses,
/// so this never touches IIS's own "stdout_*" files sitting in the same directory. Fixed (not
/// settings-driven) for the same reason LarisVMS.Node's own sweep (StorageManager.SweepLogsDirectory)
/// is: a small text-file cleanup has none of Backup's admin-configurable-retention stakes.</summary>
public class LogsRetentionService(ILogger<LogsRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Sweep();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "LogsRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Sweep()
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsDir)) return;

        var now = DateTime.UtcNow;
        foreach (var path in Directory.EnumerateFiles(logsDir, "app-*.log", SearchOption.TopDirectoryOnly))
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (IOException) { continue; }

            if (now - info.LastWriteTimeUtc <= Retention) continue;
            try { File.Delete(path); }
            catch (IOException ex) { logger.LogWarning(ex, "Could not delete expired log file {Path}", path); }
        }
    }
}

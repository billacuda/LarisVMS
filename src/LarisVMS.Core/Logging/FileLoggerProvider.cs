using Microsoft.Extensions.Logging;

namespace LarisVMS.Core.Logging;

/// <summary>The log levels an operator can pick from the Admin &gt; Settings &gt; Logs dropdown, and
/// the one place a stored/pushed level string is turned back into a <see cref="LogLevel"/>.</summary>
public static class LogLevels
{
    public static readonly string[] Selectable = ["Trace", "Debug", "Information", "Warning", "Error"];

    /// <summary>Parses a stored/config level string; anything unrecognised → Information.</summary>
    public static LogLevel Parse(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) && level != LogLevel.None
            ? level
            : LogLevel.Information;
}

/// <summary>M11: minimal rolling-file ILoggerProvider — both LarisVMS.Web (IIS-hosted) and
/// LarisVMS.Node (a Windows Service) rely on Generic Host's console-only default today, which is
/// invisible once neither runs attached to a terminal (Node in particular: a Windows Service has no
/// console at all). Deliberately simple: one file per calendar day, opened lazily and re-opened when
/// the date rolls over, a lock around each write rather than a background queue/channel — this app's
/// actual log volume (a handful of cameras, not a hyperscale service) doesn't need async batching,
/// and a lock keeps the failure mode obvious (a slow disk blocks the logging call, not silently drops
/// a line). Lives in Core, not Infrastructure, since LarisVMS.Node has no reference to Infrastructure
/// at all but both tiers need this.</summary>
public sealed class FileLoggerProvider(string directory, string filePrefix, LogLevel minLevel) : ILoggerProvider
{
    private readonly Lock _writeLock = new();
    private string? _openDate;
    private StreamWriter? _writer;

    /// <summary>The minimum level written to file. Settable at runtime so the node/vision service can
    /// follow the deployment-wide `Logging.Level` setting without a restart (M20 pass 4). A plain
    /// volatile field is enough — a level change taking effect one log call late is fine.</summary>
    private volatile LogLevel _minLevel = minLevel;
    public LogLevel MinLevel { get => _minLevel; set => _minLevel = value; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level >= _minLevel;

    internal void Write(string line)
    {
        lock (_writeLock)
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            if (_openDate != today || _writer is null)
            {
                _writer?.Dispose();
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"{filePrefix}-{today}.log");
                // FileShare.ReadWrite so the viewer page (Web tier) can read today's file while this
                // same process is still appending to it.
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                _openDate = today;
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

internal sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(logLevel)}] {categoryName}: {formatter(state, exception)}";
        if (exception is not null) line += Environment.NewLine + exception;
        provider.Write(line);
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => level.ToString().ToUpperInvariant()
    };
}

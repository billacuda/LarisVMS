namespace LarisVMS.Web.Helpers;

/// <summary>
/// Where this tier's own application log files live — one source of truth for the FileLoggerProvider
/// registration (Program.cs), the retention sweep (LogsRetentionService), and the viewer
/// (Pages/Logs/SystemLogs), which previously each independently computed
/// <c>Path.Combine(AppContext.BaseDirectory, "logs")</c>.
///
/// Deliberately read from configuration (appsettings.json's <c>Logs:Path</c>, or the
/// <c>LarisVMS__Logs__Path</c> environment variable) rather than the database. FileLoggerProvider is
/// registered on <c>builder.Logging</c> before <c>builder.Build()</c> — before the DI container, the
/// DbContext, or ISettingsResolver exist — specifically so that a startup failure (a bad connection
/// string, a missing migration) still gets written to disk. A DB-backed setting can't be read that
/// early, so this can't become a normal <c>Admin &rarr; Settings</c> field the way most other
/// settings are; it's shown there read-only instead, with the config key that changes it.
/// </summary>
public static class LogPaths
{
    public const string ConfigKey = "Logs:Path";

    public static string AppLogsDirectory(IConfiguration configuration)
        => configuration[ConfigKey] is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "logs");
}

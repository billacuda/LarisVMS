using Microsoft.Extensions.Logging;

namespace LarisVMS.Core.Logging;

/// <summary>Log-level predicates for the noisy framework categories — ASP.NET Core's per-request
/// pipeline chatter ("Request starting / Executing endpoint / … / Request finished"), the
/// <c>HttpClient</c> play-by-play, and the <c>Microsoft.Hosting.Lifetime</c> startup banner. Off the
/// localhost detection poll loop these fire several times per second per camera and bury the app's
/// own lines at Information. Rather than pin them at Warning forever, gate them on the file logger's
/// live minimum level: Warning and above always pass; Information/Debug pass only once an operator has
/// turned the deployment-wide <c>Logging.Level</c> down to Debug or Trace to actually ask for that
/// detail.</summary>
public static class FrameworkLogFilter
{
    /// <summary>A category filter that hides Information/Debug lines unless <paramref name="provider"/>'s
    /// current <see cref="FileLoggerProvider.MinLevel"/> is Debug or lower. Warning and above always
    /// pass. Evaluated per message, so a runtime level change takes effect without a restart.</summary>
    public static Func<LogLevel, bool> HiddenUnlessDebug(FileLoggerProvider provider) =>
        level => level >= LogLevel.Warning || provider.MinLevel <= LogLevel.Debug;
}

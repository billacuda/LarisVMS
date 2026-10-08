using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Logging;

namespace LarisVMS.Web.Services;

/// <summary>Holds the Web tier's own <see cref="FileLoggerProvider"/> so the Admin &gt; Settings
/// &gt; Logs page can retune its level at runtime, and so a hosted service can apply the stored
/// <c>Logging.Level</c> once at startup. The provider is created in Program.cs before the DI
/// container exists, which is why this is a plain static rather than a DI singleton.</summary>
public static class WebLogLevel
{
    public static FileLoggerProvider? Provider { get; set; }

    public static void Apply(string? level)
    {
        if (Provider is not null) Provider.MinLevel = LogLevels.Parse(level);
    }
}

/// <summary>Applies the stored deployment-wide <c>Logging.Level</c> to the Web tier's file logger
/// once the app has started (the setting lives in the database, unreadable at the point Program.cs
/// builds the logger). <see cref="ISettingsResolver"/> is scoped, so it's resolved from a scope
/// created here rather than injected into this singleton.</summary>
public sealed class WebLogLevelInitializer(IServiceScopeFactory scopeFactory, ILogger<WebLogLevelInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
            var level = await settings.GetAsync("Logging.Level", "Information", ct: cancellationToken);
            WebLogLevel.Apply(level);
            logger.LogInformation("Web log level set to {Level} from the Logging.Level setting.", LogLevels.Parse(level).ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the Logging.Level setting at startup — staying at Information.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace LarisVMS.Web.Services;

/// <summary>
/// The database work that has to finish before the app serves anything: migrations (upgrades migrate
/// here rather than from a build machine), bundled node/proxy build registration, and the role and
/// camera-group seeds. Registered as the first hosted service, so it runs before Kestrel and every
/// other background service start — but after the Windows Service has connected to the SCM. Run
/// inline before <c>app.Run()</c> instead, a slow database connection (domain service account,
/// Kerberos, network) counted against SCM's 30-second start timeout, the service was killed mid-start
/// with nothing logged, and the MSI looped on "Starting services".
///
/// A failure stops startup on purpose — running on a half-migrated schema is worse than not running,
/// and SCM recovery restarts the service.
/// </summary>
public sealed class StartupDatabaseTasks(
    IServiceScopeFactory scopeFactory,
    IHostLifetime hostLifetime,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ILogger<StartupDatabaseTasks> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // If the SCM still has the service in START_PENDING while this runs, keep asking for more time
        // so a long migration isn't killed. Once the service reports Running, ServiceBase refuses the
        // request, and there is nothing left to extend.
        using var keepAlive = OperatingSystem.IsWindows() ? StartKeepAlive() : null;

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var total = Stopwatch.StartNew();

        // A brand-new deployment gets here before the setup wizard has configured a database at all.
        // An unreachable one lands on the wizard too, since the configured check swallows the error.
        var configured = await Timed("database check", () => services.GetRequiredService<ISetupService>().IsDatabaseConfiguredAsync(cancellationToken));
        if (!configured)
        {
            if (!string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
                logger.LogWarning("Cannot reach SQL Server '{Server}' as {Identity} - showing the setup wizard until the database is reachable.",
                    ServerName(), Identity);
            return;
        }

        var db = services.GetRequiredService<ApplicationDbContext>();
        List<string> pending;
        try
        {
            pending = (await Timed("pending migrations", () => db.Database.GetPendingMigrationsAsync(cancellationToken))).ToList();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            LogUnreachable(ex);
            throw;
        }
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            try
            {
                await Timed("migrations", async () => { await db.Database.MigrateAsync(cancellationToken); return true; });
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Database migration failed - the service will not start");
                throw new InvalidOperationException("Database migration failed - the service will not start.", ex);
            }
        }

        await Timed("bundled builds", async () =>
        {
            await services.GetRequiredService<IBundledBuildRegistrar>().RegisterAsync(Path.Combine(environment.ContentRootPath, "packages"), cancellationToken);
            return true;
        });
        // Seeds the built-in roles' RoleProfile rows (renaming Administrator/Viewer to Super
        // Admin/Guest-Viewer in place along the way) — idempotent, see RoleSeedService.
        await Timed("role seed", async () => { await services.GetRequiredService<IRoleSeedService>().SeedAsync(cancellationToken); return true; });
        await Timed("camera group seed", async () => { await services.GetRequiredService<ICameraGroupSeedService>().SeedAsync(cancellationToken); return true; });

        // A site that already has users is set up, even if it predates the Setup.IsComplete flag or
        // was interrupted before the wizard's last step. Mark it, so the wizard pages are closed
        // (SetupMiddleware) — otherwise deploying over an existing site reopened /Setup, where the
        // Database and Branding steps could repoint or overwrite it.
        var setup = services.GetRequiredService<ISetupService>();
        if (await setup.IsAdminCreatedAsync(cancellationToken) && !await setup.IsSetupFinalizedAsync(cancellationToken))
        {
            await setup.FinalizeSetupAsync(cancellationToken);
            logger.LogInformation("Startup: existing users found - setup marked complete and the setup wizard closed.");
        }

        logger.LogInformation("Startup: database tasks finished in {Seconds:0.0} s.", total.Elapsed.TotalSeconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<T> Timed<T>(string phase, Func<Task<T>> work)
    {
        var sw = Stopwatch.StartNew();
        var result = await work();
        // Only worth a line when it's slow enough to matter — a healthy start takes well under a second.
        if (sw.Elapsed > TimeSpan.FromSeconds(2))
            logger.LogWarning("Startup: {Phase} took {Seconds:0.0} s.", phase, sw.Elapsed.TotalSeconds);
        return result;
    }

    private void LogUnreachable(Exception ex) =>
        logger.LogCritical(ex, "Cannot reach SQL Server '{Server}' as {Identity} - the service will not start: {Message}",
            ServerName(), Identity, ex.Message);

    private static string Identity => Environment.UserDomainName + "\\" + Environment.UserName;

    private string ServerName()
    {
        try { return new SqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection")).DataSource; }
        catch (ArgumentException) { return "?"; } // unparseable connection string
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private Timer? StartKeepAlive()
    {
        if (hostLifetime is not ServiceBase service) return null;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            try { service.RequestAdditionalTime(30_000); }
            catch (InvalidOperationException) { timer?.Dispose(); }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        return timer;
    }
}

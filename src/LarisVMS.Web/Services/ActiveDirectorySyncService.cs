using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>Wakes ActiveDirectorySyncService for an immediate sync — "Sync now", and adding or
/// removing a group link. Requests made while one is already pending collapse into it.</summary>
public sealed class ActiveDirectorySyncTrigger
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _channel.Writer.TryWrite(true);

    internal ChannelReader<bool> Reader => _channel.Reader;
}

/// <summary>
/// Syncs AD group membership to LarisVMS accounts and roles every SyncIntervalMinutes (5 min–24 h,
/// default 30), or right away when triggered. Ticks every minute and reads the settings row fresh each
/// time, so enabling AD or changing the interval needs no restart. One loop, so syncs never overlap.
/// A failed sync (AD unreachable) changes nothing and is retried after RetryAfterFailure.
/// </summary>
public class ActiveDirectorySyncService(
    IServiceScopeFactory scopeFactory,
    ActiveDirectorySyncTrigger trigger,
    ILogger<ActiveDirectorySyncService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var forced = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(forced, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Active Directory sync tick failed — will retry on the next tick.");
            }

            forced = await WaitForNextTickAsync(stoppingToken);
        }
    }

    /// <summary>Returns true when woken by a trigger rather than the timer.</summary>
    private async Task<bool> WaitForNextTickAsync(CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(TickInterval, delayCts.Token);
        var triggered = trigger.Reader.WaitToReadAsync(ct).AsTask();
        try
        {
            var first = await Task.WhenAny(delay, triggered);
            if (first != triggered) return false;
            while (trigger.Reader.TryRead(out _)) { /* drain */ }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            await delayCts.CancelAsync();
        }
    }

    private async Task TickAsync(bool forced, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = await db.ActiveDirectorySettings.FirstOrDefaultAsync(ct);
        if (settings is not { IsEnabled: true }) return;

        var now = DateTime.UtcNow;
        if (!forced && !IsDue(settings, now)) return;

        settings.LastSyncStartedAt = now;
        await db.SaveChangesAsync(ct);

        var sync = scope.ServiceProvider.GetRequiredService<ActiveDirectoryUserSync>();
        var directory = scope.ServiceProvider.GetRequiredService<IActiveDirectoryService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        try
        {
            var result = await sync.RunFullSyncAsync(settings, directory, ct);
            settings.LastSyncCompletedAt = DateTime.UtcNow;
            settings.LastSyncError = null;
            settings.LastSyncSummary = result.Summary;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Active Directory sync finished: {Summary}", result.Summary);
            if (result.Created + result.Updated + result.Disabled + result.Enabled + result.Failed > 0)
                await audit.LogAsync("AD.Sync", null, "System", null, result.Summary, ct);
        }
        catch (ActiveDirectoryException ex)
        {
            settings.LastSyncError = ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Active Directory sync failed; no accounts were changed.");
            await audit.LogAsync("AD.SyncFailed", null, "System", null, ex.Message, ct);
        }
    }

    /// <summary>Scheduled from the last attempt, not the last success — a failure retries after
    /// RetryAfterFailure (or the interval, if shorter) instead of every minute.</summary>
    internal static bool IsDue(ActiveDirectorySettings settings, DateTime utcNow)
    {
        if (settings.LastSyncStartedAt is not { } started) return true;
        var interval = TimeSpan.FromMinutes(ActiveDirectorySettings.ClampSyncInterval(settings.SyncIntervalMinutes));
        var failed = settings.LastSyncError is not null || settings.LastSyncCompletedAt is not { } completed || completed < started;
        if (failed && RetryAfterFailure < interval) interval = RetryAfterFailure;
        return utcNow >= started + interval;
    }
}

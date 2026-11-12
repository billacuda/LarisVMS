using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Keeps <see cref="CameraCertificatePins.Shared"/> in step with the setting and the saved pins
/// (every minute, and right after a change on Settings → Security), saves the pins it learns, and
/// logs a refused certificate (once an hour per camera address).
/// </summary>
public sealed class CameraCertificatePinService(IServiceScopeFactory scopeFactory, ILogger<CameraCertificatePinService> logger) : BackgroundService
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefusalLogInterval = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim ReloadNow = new(0, 1);

    private readonly Channel<CameraCertificatePin> _learned = Channel.CreateBounded<CameraCertificatePin>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Refusals to record (from this server's camera clients, or reported by nodes).</summary>
    private static readonly Channel<(string Host, string Presented, string Where)> Refusals =
        Channel.CreateBounded<(string, string, string)>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropWrite });
    private static readonly ConcurrentDictionary<string, DateTime> LastRefusalRecorded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A camera certificate was refused under pinning, here or on a node (<paramref name="where"/>):
    /// recorded in the audit log and sent to Super Admins, at most once an hour per camera address.</summary>
    public static void ReportRefusal(string host, string presented, string where)
    {
        var now = DateTime.UtcNow;
        if (LastRefusalRecorded.TryGetValue(host, out var last) && now - last < RefusalLogInterval) return;
        LastRefusalRecorded[host] = now;
        if (LastRefusalRecorded.Count > 1000) LastRefusalRecorded.Clear();
        Refusals.Writer.TryWrite((host, presented, where));
    }
    private readonly ConcurrentDictionary<string, DateTime> _lastRefusalLogged = new();

    /// <summary>Settings → Security calls this after saving, so a change applies at once.</summary>
    public static void RequestReload()
    {
        if (ReloadNow.CurrentCount == 0) ReloadNow.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CameraCertificatePins.Shared.LearnUnknown = true;
        CameraCertificatePins.Shared.Learned += OnLearned;
        CameraCertificatePins.Shared.Refused += OnRefused;
        try
        {
            var saver = SaveLearnedAsync(stoppingToken);
            var recorder = RecordRefusalsAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReloadAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not load camera certificate pins; keeping the current ones.");
                }
                try { await ReloadNow.WaitAsync(ReloadInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            _learned.Writer.TryComplete();
            await saver;
            await recorder;
        }
        finally
        {
            CameraCertificatePins.Shared.Learned -= OnLearned;
            CameraCertificatePins.Shared.Refused -= OnRefused;
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var enabled = await settings.GetAsync(CameraPinningSettings.EnabledKey, false, ct: ct);
        var pins = await db.CameraCertificatePins.AsNoTracking().ToDictionaryAsync(p => p.Host, p => p.Thumbprint, ct);
        CameraCertificatePins.Shared.Replace(enabled, pins);
    }

    private void OnLearned(string host, string thumbprint)
    {
        logger.LogInformation("Remembered the certificate of camera address {Host} ({Thumbprint}).", host, thumbprint);
        _learned.Writer.TryWrite(new CameraCertificatePin { Host = host, Thumbprint = thumbprint, FirstSeenUtc = DateTime.UtcNow });
    }

    private void OnRefused(string host, string pinned, string presented)
    {
        var now = DateTime.UtcNow;
        if (_lastRefusalLogged.TryGetValue(host, out var last) && now - last < RefusalLogInterval) return;
        _lastRefusalLogged[host] = now;
        logger.LogWarning("Refused camera address {Host}: its certificate ({Presented}) isn't the one remembered ({Pinned}). " +
            "If the camera's certificate really changed, forget it under Settings → Security.", host, presented, pinned);
        ReportRefusal(host, presented, Environment.MachineName); // the web server, by its computer name like a node
    }

    private async Task RecordRefusalsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (host, presented, where) in Refusals.Reader.ReadAllAsync(ct))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAsync("Camera.CertificateRefused", null, "System", null,
                        $"{host}: certificate {presented} isn't the one remembered (refused by {where}).", ct);
                    await scope.ServiceProvider.GetRequiredService<INotificationService>().NotifySuperAdminsAsync(
                        () => LarisVMS.Core.Localization.Loc.T("settings.security.certificateRefusedTitle"),
                        () => LarisVMS.Core.Localization.Loc.T("settings.security.certificateRefused", new { host, where }),
                        "/Admin/Settings/Security", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not record the refused certificate of camera address {Host}.", host);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task SaveLearnedAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var pin in _learned.Reader.ReadAllAsync(ct))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    if (await db.CameraCertificatePins.AnyAsync(p => p.Host == pin.Host, ct)) continue;
                    db.CameraCertificatePins.Add(pin);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not save the certificate pin for camera address {Host}.", pin.Host);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}

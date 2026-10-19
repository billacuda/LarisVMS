using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Alerts;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Ticks every minute (same interval as CameraReprobeService), re-evaluates every enabled AlertRule
/// against current camera/node state, and dispatches through each of its enabled Deliveries when the
/// condition is true and the cooldown has elapsed. One rule's delivery failing (a dead webhook URL, a
/// misconfigured Pushover token) is logged and skipped rather than aborting the tick — the other
/// rules, and the other deliveries on the same rule, must still get their chance.
/// </summary>
public class AlertEvaluatorService(IServiceScopeFactory scopeFactory, ILogger<AlertEvaluatorService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Alert evaluator tick failed — will retry on the next tick.");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var senderFactory = scope.ServiceProvider.GetRequiredService<AlertChannelSenderFactory>();
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var rules = await db.AlertRules.Include(r => r.Deliveries).Where(r => r.IsEnabled).ToListAsync(ct);
        if (rules.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            if (ct.IsCancellationRequested) break;
            if (rule.ConditionType is AlertConditionType.EvidenceLockExpiringSoon or AlertConditionType.EvidenceLockRemoved) continue;

            var message = await EvaluateAsync(db, rule, now, ct);
            if (message is null) continue;
            if (!AlertEvaluationPolicy.ShouldFire(rule.LastFiredAt, now, rule.CooldownMinutes)) continue;

            // Stamped before dispatching, not after — a delivery that throws must not leave the rule
            // able to re-fire on the very next tick.
            rule.LastFiredAt = now;
            await db.SaveChangesAsync(ct);

            Guid? cameraId = rule.CameraId;
            string? url = null;
            if (rule.CameraId is { } cam) url = $"/Playback?cameraId={cam}&atUtc={now:o}";
            else if (rule.NodeId is not null) url = "/Admin/Nodes";
            await DispatchAsync(rule, message, senderFactory, auditService, logger, ct, notifications, cameraId, url);
        }

        await EvaluateEvidenceLockRulesAsync(db, rules, now, senderFactory, auditService, notifications, logger, ct);
    }

    /// <summary>Evidence-lock notifications fire once per lock event rather than on a cooldown: each
    /// lock ending soon, and each removal, goes to every matching rule once, then the lock row is
    /// stamped so it isn't announced again. "Ending soon" means within 24 hours — or within half the
    /// lock's length for a short lock, so a 1-day lock doesn't announce its end as it's placed.</summary>
    internal static async Task EvaluateEvidenceLockRulesAsync(ApplicationDbContext db, List<Core.Entities.AlertRule> rules, DateTime now,
        AlertChannelSenderFactory senderFactory, IAuditService auditService, INotificationService? notifications, ILogger logger, CancellationToken ct)
    {
        var expiringRules = rules.Where(r => r.ConditionType == AlertConditionType.EvidenceLockExpiringSoon).ToList();
        var removedRules = rules.Where(r => r.ConditionType == AlertConditionType.EvidenceLockRemoved).ToList();

        var horizon = now.AddHours(24);
        var expiring = (await db.EvidenceLocks.Include(l => l.Camera)
                .Where(l => l.RemovedAtUtc == null && l.ExpiryWarnedAtUtc == null && l.ExpiresAtUtc != null
                    && l.ExpiresAtUtc > now && l.ExpiresAtUtc <= horizon)
                .ToListAsync(ct))
            .Where(l => l.ExpiresAtUtc!.Value - now <= (l.ExpiresAtUtc.Value - l.CreatedAtUtc) / 2)
            .ToList();

        var removed = await db.EvidenceLocks.Include(l => l.Camera)
            .Where(l => l.RemovedAtUtc != null && l.RemovalNotifiedAtUtc == null)
            .ToListAsync(ct);

        foreach (var l in expiring)
        {
            var hours = Math.Max(1, (int)Math.Round((l.ExpiresAtUtc!.Value - now).TotalHours));
            var message = $"The evidence lock on \"{l.Camera.Name}\" ends in about {hours} hour{(hours == 1 ? "" : "s")}. " +
                          $"Reason: {l.Reason}. Export the footage if it needs to be kept longer.";
            foreach (var rule in expiringRules.Where(r => CoversCamera(r, l.Camera)))
                await DispatchAsync(rule, message, senderFactory, auditService, logger, ct, notifications, l.CameraId, "/EvidenceLocks");
            l.ExpiryWarnedAtUtc = now;
        }

        foreach (var l in removed)
        {
            var message = $"{l.RemovedByUserName ?? "Someone"} removed the evidence lock on \"{l.Camera.Name}\" " +
                          $"(placed by {l.CreatedByUserName ?? "unknown"}). Reason: {l.RemovalReason}. " +
                          "The footage stays protected for 24 hours, and the lock can be restored until then.";
            foreach (var rule in removedRules.Where(r => CoversCamera(r, l.Camera)))
                await DispatchAsync(rule, message, senderFactory, auditService, logger, ct, notifications, l.CameraId, "/EvidenceLocks");
            l.RemovalNotifiedAtUtc = now;
        }

        if (expiring.Count > 0 || removed.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>A rule's scope: its camera, or every camera on its node, or every camera.</summary>
    internal static bool CoversCamera(Core.Entities.AlertRule rule, Core.Entities.Camera camera)
        => rule.CameraId is { } cameraId ? cameraId == camera.Id
         : rule.NodeId is not { } nodeId || camera.NodeId == nodeId;

    /// <summary>Logs, audits and sends one firing through every enabled delivery of the rule.
    /// Shared with DetectionAlertService. A delivery that throws is logged and skipped so the
    /// others still go out.</summary>
    internal static async Task DispatchAsync(Core.Entities.AlertRule rule, string message,
        AlertChannelSenderFactory senderFactory, IAuditService auditService, ILogger logger, CancellationToken ct,
        INotificationService? notifications = null, Guid? cameraId = null, string? url = null)
    {
        logger.LogInformation("Alert rule {RuleId} ({Name}) fired: {Message}", rule.Id, rule.Name, message);

        // Personal notifications to the rule's subscribers (browser and email), then the team channels.
        var notified = 0;
        if (notifications is not null)
        {
            try { notified = await notifications.NotifySubscribersAsync(rule, cameraId, message, DateTime.UtcNow, url, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notifying subscribers of rule {RuleId} ({Name}) failed.", rule.Id, rule.Name);
            }
        }
        var notifiedNote = "";
        if (notified > 0) notifiedNote = $" Notified {notified} subscriber{(notified == 1 ? "" : "s")}.";
        await auditService.LogAsync("Alert.Fired", null, "System", null, $"{rule.Name}: {message}{notifiedNote}", ct);

        foreach (var delivery in rule.Deliveries.Where(d => d.IsEnabled))
        {
            try
            {
                var sender = senderFactory.Get(delivery.Channel);
                await sender.SendAsync(delivery.ConfigJson, rule.Name, message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Alert delivery failed for rule {RuleId} ({Name}), channel {Channel}.",
                    rule.Id, rule.Name, delivery.Channel);
            }
        }
    }

    /// <summary>Null when the condition isn't true; otherwise the human-readable message to send.
    /// CameraId/NodeId not resolving to a real row (deleted since the rule was created) is treated as
    /// "condition not true" rather than an error — a dangling rule shouldn't spam an admin about
    /// something that no longer exists, and the admin UI is where that gets cleaned up.</summary>
    private static async Task<string?> EvaluateAsync(ApplicationDbContext db, Core.Entities.AlertRule rule, DateTime nowUtc, CancellationToken ct)
    {
        switch (rule.ConditionType)
        {
            case AlertConditionType.CameraNotReporting:
            {
                if (rule.CameraId is not { } cameraId) return null;
                var camera = await db.Cameras.Where(c => c.Id == cameraId && c.IsEnabled)
                    .Select(c => new { c.Name, MainHealth = c.Streams.Where(s => s.Role == Core.Enums.CameraStreamRole.Main).Select(s => s.HealthReportedAt).FirstOrDefault() })
                    .FirstOrDefaultAsync(ct);
                if (camera is null) return null;

                if (!AlertEvaluationPolicy.IsCameraNotReporting(camera.MainHealth, nowUtc)) return null;
                var reportedSince = camera.MainHealth is { } reported ? $"{reported:u}." : "it was added.";
                return $"Camera \"{camera.Name}\" hasn't reported health since {reportedSince}";
            }

            case AlertConditionType.NodeOffline:
            {
                if (rule.NodeId is not { } nodeId) return null;
                var node = await db.Nodes.Where(n => n.Id == nodeId)
                    .Select(n => new { n.Name, n.LastSeenAt }).FirstOrDefaultAsync(ct);
                if (node is null) return null;

                if (!AlertEvaluationPolicy.IsNodeOffline(node.LastSeenAt, nowUtc)) return null;
                var seenSince = node.LastSeenAt is { } seen ? $"{seen:u}." : "it registered.";
                return $"Node \"{node.Name}\" hasn't been seen since {seenSince}";
            }

            case AlertConditionType.NodeStorageLow:
            {
                if (rule.NodeId is not { } storageNodeId || rule.ThresholdPercent is not { } threshold) return null;
                var node = await db.Nodes.Where(n => n.Id == storageNodeId)
                    .Select(n => new { n.Name, n.StorageFreeBytes, n.StorageTotalBytes }).FirstOrDefaultAsync(ct);
                if (node is null) return null;

                if (!AlertEvaluationPolicy.IsNodeStorageLow(node.StorageFreeBytes, node.StorageTotalBytes, threshold)) return null;
                var freePercent = node.StorageFreeBytes!.Value * 100.0 / node.StorageTotalBytes!.Value;
                return $"Node \"{node.Name}\" has {freePercent:F1}% free storage, below the {threshold}% threshold.";
            }

            case AlertConditionType.NodeFailoverActivated:
            {
                // Failover plan phase 3: fires while the node is FailedOverAway (quorum or
                // maintenance) — RecordingFailoverService owns that flag. De-duplication of repeat
                // deliveries is the caller's job, same as every other condition here.
                if (rule.NodeId is not { } failoverNodeId) return null;
                var node = await db.Nodes.Where(n => n.Id == failoverNodeId)
                    .Select(n => new { n.Name, n.FailoverState, n.FailoverReason, n.FailoverSinceUtc }).FirstOrDefaultAsync(ct);
                if (node is null || node.FailoverState != Core.Enums.NodeFailoverState.FailedOverAway) return null;

                var why = node.FailoverReason == Core.Enums.NodeFailoverReason.Maintenance
                    ? "was put into maintenance" : "was found down by a quorum of health checks";
                return $"Node \"{node.Name}\" {why}" +
                       (node.FailoverSinceUtc is { } since ? $" at {since:u}" : "") +
                       " — its cameras are now recording on its backup node.";
            }

            default:
                return null;
        }
    }
}

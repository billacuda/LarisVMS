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

        var rules = await db.AlertRules.Include(r => r.Deliveries).Where(r => r.IsEnabled).ToListAsync(ct);
        if (rules.Count == 0) return;

        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            if (ct.IsCancellationRequested) break;

            var message = await EvaluateAsync(db, rule, now, ct);
            if (message is null) continue;
            if (!AlertEvaluationPolicy.ShouldFire(rule.LastFiredAt, now, rule.CooldownMinutes)) continue;

            // Stamped before dispatching, not after — a delivery that throws must not leave the rule
            // able to re-fire on the very next tick.
            rule.LastFiredAt = now;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Alert rule {RuleId} ({Name}) fired: {Message}", rule.Id, rule.Name, message);
            await auditService.LogAsync("Alert.Fired", null, "System", null, $"{rule.Name}: {message}", ct);

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

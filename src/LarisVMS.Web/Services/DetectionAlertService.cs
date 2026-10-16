using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Alerts;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Real-time detection alerts (ObjectDetected / SpeedOver). NodeService publishes every recorded
/// AI-detection span here (<see cref="IDetectionEventSink"/>) — on open, each checkpoint and close —
/// and this evaluates them on its own task, so recording never waits on alert delivery.
///
/// A span is reported several times and a color or speed can arrive on a later report, so each rule
/// remembers what it has already handled: (rule, span) for ObjectDetected, (rule, span, track) for
/// SpeedOver. Something matched during the cooldown is marked handled too, so it doesn't fire late
/// when the cooldown ends. The memory is in-process; after a restart a still-running span could alert
/// once more, which the persisted cooldown normally holds back.
///
/// Rules are cached and reloaded every 30 s, or straight away after the alert editor saves
/// (<see cref="InvalidateRules"/>).
/// </summary>
public sealed class DetectionAlertService(IServiceScopeFactory scopeFactory, ILogger<DetectionAlertService> logger)
    : BackgroundService, IDetectionEventSink
{
    private static readonly TimeSpan RulesMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandledRetention = TimeSpan.FromHours(3);

    private readonly Channel<DetectionEvent> _queue = Channel.CreateBounded<DetectionEvent>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private List<DetectionRule> _rules = [];
    private DateTime _rulesLoadedUtc = DateTime.MinValue;
    private volatile bool _rulesStale = true;
    private readonly Dictionary<string, DateTime> _handled = [];
    private DateTime _handledPrunedUtc = DateTime.UtcNow;

    public void Publish(DetectionEvent detection) => _queue.Writer.TryWrite(detection);

    /// <summary>Reload rules before the next event — called after a rule is saved or deleted.</summary>
    public void InvalidateRules() => _rulesStale = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var detection in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleAsync(detection, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Detection alert evaluation failed for span {SpanId}.", detection.SpanId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task HandleAsync(DetectionEvent e, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_rulesStale || now - _rulesLoadedUtc > RulesMaxAge) await LoadRulesAsync(ct);
        if (_rules.Count == 0) return;
        PruneHandled(now);

        var local = DateTime.Now;
        foreach (var rule in _rules)
        {
            if (!DetectionAlertMatcher.MatchesEvent(rule, e)) continue;
            if (!DetectionAlertMatcher.InSchedule(rule.ScheduleDays, rule.ScheduleStart, rule.ScheduleEnd, local)) continue;

            if (rule.Condition == AlertConditionType.ObjectDetected)
            {
                var key = $"{rule.Id}:{e.SpanId}";
                if (_handled.ContainsKey(key)) continue;
                var matched = DetectionAlertMatcher.MatchingObjects(rule, e);
                if (matched.Count == 0) continue;
                _handled[key] = now;
                var what = DetectionAlertMatcher.Describe(e.Category, e.Label, matched[0]);
                var count = matched.Count > 1 ? $" ({matched.Count} at once)" : "";
                await FireAsync(rule, e.CameraId, null,
                    (camera, _, _) => $"{what} detected on camera \"{camera}\"{count}.", now, ct);
            }
            else if (rule.Condition == AlertConditionType.SpeedOver && rule.SpeedThresholdKph is { } threshold)
            {
                var fast = e.Objects
                    .Where(o => o.SpeedKph >= threshold && DetectionAlertMatcher.ObjectPasses(rule, e, o)
                        && !_handled.ContainsKey($"{rule.Id}:{e.SpanId}:{o.TrackId}"))
                    .OrderByDescending(o => o.SpeedKph)
                    .ToList();
                if (fast.Count == 0) continue;
                foreach (var o in fast) _handled[$"{rule.Id}:{e.SpanId}:{o.TrackId}"] = now;
                var top = fast[0];
                var what = DetectionAlertMatcher.Describe(e.Category, e.Label, top);
                await FireAsync(rule, e.CameraId, top.SpeedTrapId, (camera, units, trap) =>
                    $"{what} at {SpeedUnits.Format(top.SpeedKph!.Value, units)} on camera \"{camera}\"{trap}" +
                    $" — at or over the {SpeedUnits.Format(threshold, units)} limit.", now, ct);
            }
        }
    }

    /// <summary>Re-reads the rule (cooldown, deliveries) and fires it if the cooldown allows.
    /// <paramref name="message"/> gets the camera name, the speed units setting and a " (trap name)"
    /// suffix (empty when there's no trap).</summary>
    private async Task FireAsync(DetectionRule rule, Guid cameraId, Guid? speedTrapId,
        Func<string, string, string, string> message, DateTime now, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entity = await db.AlertRules.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == rule.Id && r.IsEnabled, ct);
        if (entity is null) return;
        if (!AlertEvaluationPolicy.ShouldFire(entity.LastFiredAt, now, entity.CooldownMinutes)) return;

        // Stamped before dispatching, the same as AlertEvaluatorService: a delivery that throws
        // must not leave the rule able to fire again on the very next detection.
        entity.LastFiredAt = now;
        await db.SaveChangesAsync(ct);

        var cameraName = await db.Cameras.Where(c => c.Id == cameraId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "(deleted camera)";
        var units = await scope.ServiceProvider.GetRequiredService<ISettingsResolver>()
            .GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct);
        var trapName = speedTrapId is { } trapId
            ? await db.SpeedTraps.Where(t => t.Id == trapId).Select(t => t.Name).FirstOrDefaultAsync(ct)
            : null;
        var text = message(cameraName, units, trapName is null ? "" : $" ({trapName})");

        await AlertEvaluatorService.DispatchAsync(entity, text,
            scope.ServiceProvider.GetRequiredService<AlertChannelSenderFactory>(),
            scope.ServiceProvider.GetRequiredService<IAuditService>(), logger, ct);
    }

    private async Task LoadRulesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _rulesStale = false;
        var rules = await db.AlertRules.AsNoTracking()
            .Where(r => r.IsEnabled && (r.ConditionType == AlertConditionType.ObjectDetected || r.ConditionType == AlertConditionType.SpeedOver))
            .ToListAsync(ct);

        var zoneIds = rules.Where(r => r.ZoneId is not null).Select(r => r.ZoneId!.Value).Distinct().ToList();
        var zones = zoneIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Zones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => z.PolygonJson, ct);

        _rules = rules.Select(r => new DetectionRule(r.Id, r.Name, r.ConditionType, r.CameraId,
            r.CategoryFilter, r.LabelFilter, r.ColorFilter,
            HasZoneFilter: r.ZoneId is not null,
            ZonePolygon: r.ZoneId is { } zid ? DetectionAlertMatcher.ParsePolygon(zones.GetValueOrDefault(zid)) : null,
            r.SpeedThresholdKph, r.ScheduleDays, r.ScheduleStart, r.ScheduleEnd)).ToList();
        _rulesLoadedUtc = DateTime.UtcNow;
    }

    private void PruneHandled(DateTime now)
    {
        if (now - _handledPrunedUtc < TimeSpan.FromMinutes(10)) return;
        _handledPrunedUtc = now;
        foreach (var (key, at) in _handled)
        {
            if (now - at > HandledRetention) _handled.Remove(key);
        }
    }
}

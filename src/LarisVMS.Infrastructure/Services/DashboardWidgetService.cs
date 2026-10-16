using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Backs the dashboard's configurable widgets. Gathers only the data the requested widgets
/// need, ranks it with <see cref="DashboardWidgetRanker"/>, and narrows camera rows to what the viewer
/// may see.
///
/// The system-wide inputs are cached for a short while in IMemoryCache, shared across users: every
/// open dashboard polls each minute, and the heavier queries (storage use sums every segment) shouldn't
/// run once per viewer per poll. Camera access is applied per viewer after the cache.</summary>
public class DashboardWidgetService(ApplicationDbContext db, ICameraService cameraService, ICameraAccessService cameraAccess,
    INodeService nodeService, ITimelineService timeline, INodeBuildService nodeBuilds, ISettingsResolver settings,
    IMemoryCache cache) : IDashboardWidgetService
{
    public const int TopCount = 10;
    public const int MaxReportPageSize = 100;

    private static readonly TimeSpan LiveTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeavyTtl = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> NeedsStorage = [DashboardWidgetCatalog.CameraStorage, DashboardWidgetCatalog.CameraQuota];
    private static readonly HashSet<string> NeedsExtents = [DashboardWidgetCatalog.CameraRetention, DashboardWidgetCatalog.CameraLastRecording];
    private static readonly HashSet<string> NeedsEvents =
        [DashboardWidgetCatalog.CameraHuman, DashboardWidgetCatalog.CameraVehicle, DashboardWidgetCatalog.CameraAnimal, DashboardWidgetCatalog.CameraObject];

    public IReadOnlyList<DashboardWidgetDefinition> GetAvailable(DashboardViewer viewer) =>
        DashboardWidgetCatalog.All.Where(w => IsAllowed(w, viewer)).ToList();

    private static bool IsAllowed(DashboardWidgetDefinition w, DashboardViewer viewer) =>
        w.RequiredPolicy is null || (w.RequiredPolicy == "Alerts.Edit" && viewer.CanViewAlerts);

    public async Task<List<DashboardWidgetResult>> GetTopAsync(DashboardViewer viewer,
        IReadOnlyList<(string Key, DashboardWidgetOptions Options)> widgets, CancellationToken ct = default)
    {
        var wanted = widgets
            .Select(w => (Def: DashboardWidgetCatalog.Find(w.Key), w.Options))
            .Where(w => w.Def is not null && IsAllowed(w.Def, viewer)
                && w.Def.Kind is DashboardWidgetKind.TopList or DashboardWidgetKind.Snapshots)
            .ToList();
        if (wanted.Count == 0) return [];

        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(viewer.User, CameraAccessActions.View, ct);
        var rankedKeys = wanted.Where(w => w.Def!.Kind == DashboardWidgetKind.TopList
                && w.Def.Key is not (DashboardWidgetCatalog.RecentAlerts or DashboardWidgetCatalog.SpeedLog))
            .Select(w => w.Def!.Key).ToHashSet();
        var data = rankedKeys.Count > 0 ? await LoadAsync(rankedKeys, accessible, viewer.CanOpenNodes, ct) : null;

        var results = new List<DashboardWidgetResult>();
        foreach (var (def, options) in wanted)
        {
            switch (def!.Key)
            {
                case DashboardWidgetCatalog.RecentAlerts:
                {
                    var (rows, total) = await GetAlertsAsync(1, TopCount, null, ct);
                    results.Add(new DashboardWidgetResult(def.Key, total, rows, def.EmptyMessage));
                    break;
                }
                case DashboardWidgetCatalog.SpeedLog:
                {
                    var (rows, total) = await GetSpeedLogAsync(accessible, 1, TopCount, null, null, true, ct);
                    results.Add(new DashboardWidgetResult(def.Key, total, rows, def.EmptyMessage));
                    break;
                }
                case DashboardWidgetCatalog.LatestDetections:
                {
                    var snapshots = await GetDetectionsAsync(accessible, options.Kinds, 1, 12, ct);
                    results.Add(new DashboardWidgetResult(def.Key, -1, [], def.EmptyMessage, snapshots.Items));
                    break;
                }
                default:
                {
                    var rows = DashboardWidgetRanker.Rank(def.Key, data!, options);
                    results.Add(new DashboardWidgetResult(def.Key, rows.Count, rows.Take(TopCount).ToList(), def.EmptyMessage));
                    break;
                }
            }
        }
        return results;
    }

    public async Task<DashboardReportPage?> GetReportAsync(DashboardViewer viewer, string key, DashboardWidgetOptions options,
        int page, int pageSize, string? query, Guid? nodeId, string? sort, bool descending, CancellationToken ct = default)
    {
        var def = DashboardWidgetCatalog.Find(key);
        if (def is null || !IsAllowed(def, viewer) || def.Kind is not (DashboardWidgetKind.TopList or DashboardWidgetKind.Snapshots))
            return null;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxReportPageSize);

        // These two page in the database — their full history can be far larger than a camera list.
        if (key == DashboardWidgetCatalog.RecentAlerts)
        {
            var (rows, total) = await GetAlertsAsync(page, pageSize, query, ct);
            return new DashboardReportPage(key, def.Title, def.ValueHeader, total, page, pageSize, TotalPages(total, pageSize), rows);
        }
        if (key == DashboardWidgetCatalog.SpeedLog)
        {
            var speedAccessible = await cameraAccess.GetAccessibleCameraIdsAsync(viewer.User, CameraAccessActions.View, ct);
            var speedSort = sort is not null && SpeedLogQuery.SortKeys.Contains(sort) ? sort : null;
            var (rows, total) = await GetSpeedLogAsync(speedAccessible, page, pageSize, query, speedSort, speedSort is null || descending, ct);
            return new DashboardReportPage(key, def.Title, def.ValueHeader, total, page, pageSize, TotalPages(total, pageSize), rows,
                SpeedLogColumns);
        }
        if (key == DashboardWidgetCatalog.LatestDetections)
        {
            var accessibleIds = await cameraAccess.GetAccessibleCameraIdsAsync(viewer.User, CameraAccessActions.View, ct);
            var snapshots = await GetDetectionsAsync(accessibleIds, options.Kinds, page, pageSize, ct);
            var rows = snapshots.Items.Select(s => DetectionRow(s, DateTime.UtcNow)).ToList();
            return new DashboardReportPage(key, def.Title, def.ValueHeader, -1, snapshots.CurrentPage, pageSize, snapshots.TotalPages, rows);
        }

        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(viewer.User, CameraAccessActions.View, ct);
        var data = await LoadAsync(new HashSet<string> { key }, accessible, viewer.CanOpenNodes, ct);
        var all = FilterAndSort(DashboardWidgetRanker.Rank(key, data, options), query, nodeId, sort, descending);
        var totalPages = TotalPages(all.Count, pageSize);
        page = Math.Min(page, totalPages);
        return new DashboardReportPage(key, def.Title, def.ValueHeader, all.Count, page, pageSize, totalPages,
            all.Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }

    private static int TotalPages(int total, int pageSize) => Math.Max(1, (total + pageSize - 1) / pageSize);

    /// <summary>The report popup's filter box and column sort. Free text matches the name, node or
    /// note; with no sort column the widget's own ranking order is kept.</summary>
    internal static List<DashboardWidgetRow> FilterAndSort(List<DashboardWidgetRow> rows, string? query, Guid? nodeId,
        string? sort, bool descending)
    {
        IEnumerable<DashboardWidgetRow> result = rows;
        if (nodeId is { } node) result = result.Where(r => r.NodeId == node);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            result = result.Where(r => r.AssetName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.NodeName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Badge?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        result = sort switch
        {
            "name" => descending ? result.OrderByDescending(r => r.AssetName, StringComparer.OrdinalIgnoreCase) : result.OrderBy(r => r.AssetName, StringComparer.OrdinalIgnoreCase),
            "node" => descending ? result.OrderByDescending(r => r.NodeName ?? "", StringComparer.OrdinalIgnoreCase) : result.OrderBy(r => r.NodeName ?? "", StringComparer.OrdinalIgnoreCase),
            "value" => descending ? result.OrderByDescending(r => r.Value ?? double.MinValue) : result.OrderBy(r => r.Value ?? double.MaxValue),
            _ => result,
        };
        return result.ToList();
    }

    // ── Data loading ────────────────────────────────────────────────────────

    private async Task<DashboardWidgetData> LoadAsync(IReadOnlySet<string> keys, HashSet<Guid>? accessible, bool canOpenNodes, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var allCameras = await Cached("cameras", LiveTtl, () => cameraService.ListAsync(ct));
        var nodes = await Cached("nodes", LiveTtl, async () => (await nodeService.ListAsync(ct))
            .Where(n => n.Status is not (NodeStatus.Pending or NodeStatus.Disabled)).ToList());

        var storage = keys.Overlaps(NeedsStorage)
            ? await Cached("storage", HeavyTtl, () => cameraService.GetStorageUsageAsync(ct)) : [];
        var extents = keys.Overlaps(NeedsExtents)
            ? await Cached("extents", LiveTtl, () => timeline.GetRecordingExtentsAsync(ct)) : [];
        var modes = keys.Contains(DashboardWidgetCatalog.CameraLastRecording)
            ? await Cached("modes", HeavyTtl, () => RecordingModesAsync(allCameras, ct)) : [];
        var events = keys.Overlaps(NeedsEvents)
            ? await Cached("events", HeavyTtl, () => timeline.GetEventCountsAsync(null, now, ct)) : [];
        var motion = keys.Contains(DashboardWidgetCatalog.CameraMotion)
            ? await Cached("motion", HeavyTtl, () => timeline.GetPlainMotionCountsAsync(null, now, ct)) : [];
        var days = keys.Contains(DashboardWidgetCatalog.NodeDaysRemaining)
            ? await Cached("days", HeavyTtl, () => nodeService.GetEstimatedDaysRemainingAsync(ct)) : [];
        var builds = keys.Contains(DashboardWidgetCatalog.NodeHealth)
            ? await Cached("builds", HeavyTtl, () => LatestBuildsAsync(nodes, ct)) : [];

        var failoverStateOf = nodes.ToDictionary(n => n.Id, n => n.FailoverState);
        var defaultBackupOf = nodes.ToDictionary(n => n.Id, n => n.BackupNodeId);
        var disabledAiByNode = nodes.ToDictionary(n => n.Id, n => n.DisableAiObjectDetection);

        var cameras = accessible is null ? allCameras : allCameras.Where(c => accessible.Contains(c.Id)).ToList();
        var dashCameras = cameras.Select(c =>
        {
            var main = c.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
            var extent = extents.TryGetValue(c.Id, out var e) ? e : ((DateTime, DateTime)?)null;
            return new DashCamera(c.Id, c.Name, c.IsEnabled, c.NodeId, c.Node?.Name,
                RecordingNodeResolver.Resolve(c.NodeId, c.BackupNodeIdOverride, failoverStateOf, defaultBackupOf),
                main?.Fps, main?.BitrateKbps, main?.ReconnectCount, main?.HealthReportedAt, main?.Width, main?.Height,
                main?.IsEngineBuilding, main?.EngineBuildFailed,
                AiOn: c.AiDetectionEnabled && c.NodeId is { } home && !disabledAiByNode.GetValueOrDefault(home),
                CreatedAt: c.CreatedAt,
                StorageBytes: storage.GetValueOrDefault(c.Id), QuotaBytes: c.QuotaBytes,
                OldestRecordingUtc: extent?.Item1, LatestRecordingUtc: extent?.Item2,
                RecordingMode: modes.GetValueOrDefault(c.Id));
        }).ToList();

        var dashNodes = nodes.Select(n => new DashNode(n.Id, n.Name, n.LastSeenAt,
            n.CpuPercent, n.NetReceiveBytesPerSec, n.NetSendBytesPerSec, n.HostStatsUpdatedAt,
            n.StorageFreeBytes, n.StorageTotalBytes, days.GetValueOrDefault(n.Id),
            n.StoragePressureActive, n.StoragePressureSince, n.ClockSkewSeconds,
            n.Version, builds.GetValueOrDefault(n.Platform ?? "win-x64"),
            n.FailoverState, n.FailoverReason, n.FailoverSinceUtc)).ToList();

        var visibleIds = dashCameras.Select(c => c.Id).ToHashSet();
        return new DashboardWidgetData(now, dashCameras, dashNodes,
            events.Where(r => visibleIds.Contains(r.CameraId)).ToList(),
            motion.Where(r => visibleIds.Contains(r.CameraId)).ToList(),
            canOpenNodes);
    }

    private async Task<T> Cached<T>(string name, TimeSpan ttl, Func<Task<T>> load)
    {
        var key = "dashboard-widgets:" + name;
        if (cache.TryGetValue(key, out T? hit) && hit is not null) return hit;
        var value = await load();
        cache.Set(key, value, ttl);
        return value;
    }

    /// <summary>Each camera's effective Recording.Mode (camera → node → global). SettingsResolver
    /// caches every lookup, so this is cheap after the first call.</summary>
    private async Task<Dictionary<Guid, string>> RecordingModesAsync(List<Camera> cameras, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var c in cameras)
            result[c.Id] = await settings.GetAsync("Recording.Mode", nameof(RecordingMode.Continuous), cameraId: c.Id, nodeId: c.NodeId, ct: ct);
        return result;
    }

    /// <summary>Newest approved node build per platform in use — what a node's version is compared
    /// against for the "outdated" health issue.</summary>
    private async Task<Dictionary<string, string>> LatestBuildsAsync(List<Node> nodes, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var platform in nodes.Select(n => n.Platform ?? "win-x64").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var build = await nodeBuilds.GetLatestForPlatformAsync(platform, ct);
            if (build is not null) result[platform] = build.Version;
        }
        return result;
    }

    private static readonly IReadOnlyList<DashboardReportColumn> SpeedLogColumns =
    [
        new("time", "When", "time", "time"),
        new("camera", "Camera", SortKey: "camera"),
        new("trap", "Speed trap", SortKey: "trap"),
        new("object", "Object", SortKey: "object"),
        new("color", "Color"),
        new("speed", "Speed", "number", "speed"),
        new("limit", "Limit"),
        new("kph", "Speed (km/h)", "number", CsvOnly: true),
    ];

    /// <summary>The speed log as widget/report rows: the object (with its color) as the name, when /
    /// where / which trap as the note, the speed in the configured unit as the value, a badge when
    /// it beat the trap's limit, and a link to Playback a few seconds before the measurement.</summary>
    private async Task<(List<DashboardWidgetRow> Rows, int Total)> GetSpeedLogAsync(IReadOnlySet<Guid>? accessible,
        int page, int pageSize, string? query, string? sort, bool descending, CancellationToken ct)
    {
        var (entries, total) = await SpeedLogQuery.RunAsync(db, accessible, page, pageSize, query, sort, descending, ct);
        var units = await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rows = entries.Select(e =>
        {
            var what = (e.Label.Length > 0 ? char.ToUpperInvariant(e.Label[0]) + e.Label[1..] : "Object")
                + (e.Color is null ? "" : $" ({e.Color})");
            var where = e.TrapName.Length > 0 ? $"{e.CameraName} · {e.TrapName}" : e.CameraName;
            var speed = SpeedUnits.Format(e.SpeedKph, units);
            var link = $"/Playback?cameraId={e.CameraId}&atUtc={Uri.EscapeDataString(e.AtUtc.AddSeconds(-5).ToString("o", inv))}";
            return new DashboardWidgetRow(e.Id.ToString(inv), what, "speed", link, null, null, e.SpeedKph, speed,
                Note: "{time} · " + where,
                Badge: e.OverLimit ? "Over limit" : null, BadgeClass: e.OverLimit ? "text-bg-danger" : null,
                NoteTimeUtc: e.AtUtc,
                Cells: new Dictionary<string, string?>
                {
                    ["time"] = DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc).ToString("o", inv),
                    ["camera"] = e.CameraName,
                    ["trap"] = e.TrapName,
                    ["object"] = e.Label,
                    ["color"] = e.Color,
                    ["speed"] = speed,
                    ["limit"] = e.LimitKph is { } limit ? SpeedUnits.Format(limit, units) : null,
                    ["kph"] = e.SpeedKph.ToString("0.0", inv),
                });
        }).ToList();
        return (rows, total);
    }

    /// <summary>Fired alerts, newest first, from the audit log (AlertEvaluatorService writes one
    /// "Alert.Fired" entry per firing, as "{rule name}: {message}").</summary>
    private async Task<(List<DashboardWidgetRow> Rows, int Total)> GetAlertsAsync(int page, int pageSize, string? query, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking().Where(a => a.Action == "Alert.Fired");
        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            q = q.Where(a => a.Details != null && a.Details.Contains(text));
        }

        var total = await q.CountAsync(ct);
        var entries = await q.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new { a.Id, a.Details, a.OccurredAt })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var rows = entries.Select(a =>
        {
            var details = a.Details ?? "";
            var split = details.IndexOf(": ", StringComparison.Ordinal);
            var rule = split > 0 ? details[..split] : "Alert";
            var message = split > 0 ? details[(split + 2)..] : details;
            var ago = now - a.OccurredAt;
            return new DashboardWidgetRow(a.Id.ToString(), rule, "alert", "/Admin/Alerts", null, null,
                ago.TotalSeconds, DashboardWidgetRanker.FormatDuration(ago) + " ago", message);
        }).ToList();
        return (rows, total);
    }

    /// <summary>Kinds null means the widget's default (every detection type, no plain motion); an
    /// explicit empty list means the user unticked everything. The Motion token turns plain motion
    /// on; the rest go to GetSnapshotsAsync's own type filter.</summary>
    private async Task<SnapshotPageDto> GetDetectionsAsync(HashSet<Guid>? accessible, IReadOnlyList<string>? kinds,
        int page, int pageSize, CancellationToken ct)
    {
        if (accessible is { Count: 0 } || kinds is { Count: 0 }) return new SnapshotPageDto([], 1, 1);
        var includeMotion = kinds?.Contains(DashboardWidgetCatalog.MotionKindToken, StringComparer.OrdinalIgnoreCase) ?? false;
        return await timeline.GetSnapshotsAsync(accessible?.ToList(), null, null, page, pageSize, ct,
            kinds: kinds, includePlainMotion: includeMotion);
    }

    private static DashboardWidgetRow DetectionRow(SnapshotDto s, DateTime now)
    {
        var labels = s.Badges.Count > 0 ? string.Join(", ", s.Badges.Select(b => b.Label)) : s.Label;
        var ago = now - s.AtUtc;
        return new DashboardWidgetRow(s.Id.ToString(), s.CameraName, "detection",
            $"/Playback?cameraId={s.CameraId}&atUtc={Uri.EscapeDataString(s.PlayFromUtc.ToString("o"))}",
            null, null, ago.TotalSeconds, DashboardWidgetRanker.FormatDuration(ago) + " ago", labels);
    }
}

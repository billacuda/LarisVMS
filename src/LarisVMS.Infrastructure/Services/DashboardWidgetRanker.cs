using System.Globalization;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;

namespace LarisVMS.Infrastructure.Services;

/// <summary>One camera as the dashboard widgets see it — everything the rankers need, flattened so
/// they're pure functions over plain data (unit-testable without a database).
/// RecordingNodeId is where the camera records right now (its backup during a failover).
/// OldestRecordingUtc/LatestRecordingUtc come from the camera's segments; StorageBytes sums them.</summary>
public sealed record DashCamera(Guid Id, string Name, bool Enabled, Guid? NodeId, string? NodeName, Guid? RecordingNodeId,
    int? Fps, int? BitrateKbps, int? ReconnectCount, DateTime? HealthReportedAt, int? Width, int? Height,
    bool? EngineBuilding, bool? EngineFailed, bool AiOn, DateTime CreatedAt,
    long StorageBytes = 0, long? QuotaBytes = null,
    DateTime? OldestRecordingUtc = null, DateTime? LatestRecordingUtc = null, string? RecordingMode = null);

/// <summary>One node as the dashboard widgets see it. HostStatsUpdatedAt gates CPU/network the same
/// way the node cards do (stale load figures aren't shown). LatestBuild is the newest approved node
/// build for this node's platform, if any — "outdated" means that build is newer than Version.</summary>
public sealed record DashNode(Guid Id, string Name, DateTime? LastSeenAt,
    double? CpuPercent, long? NetReceiveBytesPerSec, long? NetSendBytesPerSec, DateTime? HostStatsUpdatedAt,
    long? StorageFreeBytes, long? StorageTotalBytes, double? DaysRemaining,
    bool StoragePressureActive, DateTime? StoragePressureSince, double? ClockSkewSeconds,
    string? Version, string? LatestBuild,
    NodeFailoverState FailoverState, NodeFailoverReason? FailoverReason, DateTime? FailoverSinceUtc);

/// <summary>Everything one widget poll ranks over. EventCounts is TimelineService.GetEventCountsAsync's
/// output and MotionCounts its plain-motion counterpart; either is empty when no requested widget needed
/// it. CanOpenNodes decides whether node rows link to the node's admin page.</summary>
public sealed record DashboardWidgetData(DateTime NowUtc, IReadOnlyList<DashCamera> Cameras, IReadOnlyList<DashNode> Nodes,
    IReadOnlyList<CameraEventCountRow> EventCounts, IReadOnlyList<CameraEventCountRow> MotionCounts, bool CanOpenNodes);

/// <summary>Turns <see cref="DashboardWidgetData"/> into the ordered rows of one widget's full report.
/// The top 10 shown on the dashboard is just the first ten of these.</summary>
public static class DashboardWidgetRanker
{
    /// <summary>"Should be recording" fires once a Continuous camera's newest footage is this old.
    /// Segments are 60s, and land in the database shortly after they close, so a few minutes of slack
    /// keeps a healthy camera from flickering into the warning.</summary>
    public static readonly TimeSpan ContinuousGapWarning = TimeSpan.FromMinutes(5);

    /// <summary>Same threshold Admin/Nodes uses for its clock-skew warning.</summary>
    public const double ClockSkewWarningSeconds = 60;

    public const string DefaultWindow = "24h";

    public static List<DashboardWidgetRow> Rank(string key, DashboardWidgetData d, DashboardWidgetOptions? options = null)
    {
        var o = options ?? new DashboardWidgetOptions();
        return key switch
        {
            DashboardWidgetCatalog.CameraStorage => CameraStorage(d),
            DashboardWidgetCatalog.CameraFps => CameraFps(d, o.Ascending),
            DashboardWidgetCatalog.CameraBitrate => CameraBitrate(d, o.Ascending),
            DashboardWidgetCatalog.CameraRetention => CameraRetention(d),
            DashboardWidgetCatalog.CameraOffline => CameraOffline(d),
            DashboardWidgetCatalog.CameraMotion => CameraEvents(d, d.MotionCounts, _ => true, objects: false, o.Window),
            DashboardWidgetCatalog.CameraHuman => CameraEvents(d, d.EventCounts, IsHuman, objects: true, o.Window),
            DashboardWidgetCatalog.CameraVehicle => CameraEvents(d, d.EventCounts, IsVehicle, objects: true, o.Window),
            DashboardWidgetCatalog.CameraAnimal => CameraEvents(d, d.EventCounts, IsAnimal, objects: true, o.Window),
            DashboardWidgetCatalog.CameraObject => CameraEvents(d, d.EventCounts, IsObject, objects: true, o.Window),
            DashboardWidgetCatalog.CameraLastRecording => CameraLastRecording(d),
            DashboardWidgetCatalog.CameraResolution => CameraResolution(d, o.Ascending),
            DashboardWidgetCatalog.CameraReconnects => CameraReconnects(d),
            DashboardWidgetCatalog.CameraQuota => CameraQuota(d),
            DashboardWidgetCatalog.CameraAiEngine => CameraAiEngine(d),
            DashboardWidgetCatalog.NodeCameras => NodeCameras(d),
            DashboardWidgetCatalog.NodeOfflineCameras => NodeOfflineCameras(d),
            DashboardWidgetCatalog.NodeStorage => NodeStorage(d),
            DashboardWidgetCatalog.NodeFps => NodeFps(d),
            DashboardWidgetCatalog.NodeBandwidth => NodeBandwidth(d),
            DashboardWidgetCatalog.NodeCpu => NodeCpu(d),
            DashboardWidgetCatalog.NodeDaysRemaining => NodeDaysRemaining(d),
            DashboardWidgetCatalog.NodeHealth => NodeHealth(d),
            _ => [],
        };
    }

    // ── Cameras ─────────────────────────────────────────────────────────────

    private static bool HealthFresh(DashCamera c, DateTime now) =>
        c.HealthReportedAt is { } r && now - r < DashboardService.HealthFreshWindow;

    private static DashboardWidgetRow CameraRow(DashCamera c, double? value, string display,
        string? note = null, string? badge = null, string? badgeClass = null, DateTime? noteTimeUtc = null) =>
        new(c.Id.ToString(), c.Name, "camera", $"/Cameras/Edit/{c.Id}", c.NodeId, c.NodeName,
            value, display, note, badge, badgeClass, noteTimeUtc);

    private static List<DashboardWidgetRow> CameraStorage(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.StorageBytes > 0)
            .OrderByDescending(c => c.StorageBytes).ThenBy(c => c.Name)
            .Select(c => CameraRow(c, c.StorageBytes, FormatBytes(c.StorageBytes)))
            .ToList();

    private static List<DashboardWidgetRow> CameraFps(DashboardWidgetData d, bool ascending) =>
        OrderByValue(d.Cameras.Where(c => c.Enabled && HealthFresh(c, d.NowUtc) && c.Fps is not null), c => c.Fps!.Value, ascending)
            .Select(c => CameraRow(c, c.Fps, c.Fps!.Value.ToString(CultureInfo.InvariantCulture)))
            .ToList();

    private static List<DashboardWidgetRow> CameraBitrate(DashboardWidgetData d, bool ascending) =>
        OrderByValue(d.Cameras.Where(c => c.Enabled && HealthFresh(c, d.NowUtc) && c.BitrateKbps is not null), c => c.BitrateKbps!.Value, ascending)
            .Select(c => CameraRow(c, c.BitrateKbps, FormatKbps(c.BitrateKbps!.Value)))
            .ToList();

    private static List<DashboardWidgetRow> CameraResolution(DashboardWidgetData d, bool ascending) =>
        OrderByValue(d.Cameras.Where(c => c.Width is > 0 && c.Height is > 0), c => (long)c.Width!.Value * c.Height!.Value, ascending)
            .Select(c => CameraRow(c, (double)c.Width!.Value * c.Height!.Value, $"{c.Width}×{c.Height}",
                note: FormatMegapixels((long)c.Width!.Value * c.Height!.Value)))
            .ToList();

    /// <summary>Oldest footage first — the cameras keeping the longest history.</summary>
    private static List<DashboardWidgetRow> CameraRetention(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.OldestRecordingUtc is not null)
            .OrderBy(c => c.OldestRecordingUtc).ThenBy(c => c.Name)
            .Select(c =>
            {
                var age = d.NowUtc - c.OldestRecordingUtc!.Value;
                return CameraRow(c, age.TotalSeconds, FormatDuration(age), note: "since " + TimePlaceholder, noteTimeUtc: c.OldestRecordingUtc);
            })
            .ToList();

    /// <summary>Enabled cameras on a node that haven't reported health within the fresh window,
    /// longest offline first. A camera that never reported counts from when it was added.</summary>
    private static List<DashboardWidgetRow> CameraOffline(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.Enabled && c.NodeId is not null && !HealthFresh(c, d.NowUtc))
            .Select(c => (Camera: c, Since: c.HealthReportedAt ?? c.CreatedAt))
            .OrderBy(x => x.Since).ThenBy(x => x.Camera.Name)
            .Select(x =>
            {
                var gap = d.NowUtc - x.Since;
                return CameraRow(x.Camera, gap.TotalSeconds, FormatDuration(gap),
                    note: x.Camera.HealthReportedAt is null ? "never reported" : "since " + TimePlaceholder,
                    noteTimeUtc: x.Camera.HealthReportedAt is null ? null : x.Since);
            })
            .ToList();

    /// <summary>Enabled cameras by how long ago their newest footage ended, longest first; never
    /// recorded counts from when the camera was added. A Continuous camera past
    /// <see cref="ContinuousGapWarning"/> is flagged — it's supposed to always be recording.</summary>
    private static List<DashboardWidgetRow> CameraLastRecording(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.Enabled)
            .Select(c => (Camera: c, Since: c.LatestRecordingUtc ?? c.CreatedAt))
            .OrderBy(x => x.Since).ThenBy(x => x.Camera.Name)
            .Select(x =>
            {
                var gap = d.NowUtc - x.Since;
                if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;
                var continuous = string.Equals(x.Camera.RecordingMode, nameof(RecordingMode.Continuous), StringComparison.OrdinalIgnoreCase);
                var flagged = continuous && gap > ContinuousGapWarning;
                var note = (x.Camera.LatestRecordingUtc is null ? "never recorded" : TimePlaceholder) +
                           (x.Camera.RecordingMode is { } mode ? " · " + mode : "");
                return CameraRow(x.Camera, gap.TotalSeconds, x.Camera.LatestRecordingUtc is null ? "never" : FormatDuration(gap) + " ago",
                    note, flagged ? "Should be recording" : null, flagged ? "text-bg-danger" : null,
                    x.Camera.LatestRecordingUtc);
            })
            .ToList();

    private static List<DashboardWidgetRow> CameraReconnects(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.ReconnectCount is > 0)
            .OrderByDescending(c => c.ReconnectCount).ThenBy(c => c.Name)
            .Select(c => CameraRow(c, c.ReconnectCount, c.ReconnectCount!.Value.ToString("N0", CultureInfo.InvariantCulture)))
            .ToList();

    /// <summary>Cameras with a quota set, by how full it is. Badged from 90%.</summary>
    private static List<DashboardWidgetRow> CameraQuota(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.QuotaBytes is > 0)
            .Select(c => (Camera: c, Percent: 100.0 * c.StorageBytes / c.QuotaBytes!.Value))
            .OrderByDescending(x => x.Percent).ThenBy(x => x.Camera.Name)
            .Select(x => CameraRow(x.Camera, x.Percent, x.Percent.ToString("0", CultureInfo.InvariantCulture) + "%",
                note: $"{FormatBytes(x.Camera.StorageBytes)} of {FormatBytes(x.Camera.QuotaBytes!.Value)}",
                badge: x.Percent >= 100 ? "Full" : x.Percent >= 90 ? "Nearly full" : null,
                badgeClass: x.Percent >= 100 ? "text-bg-danger" : x.Percent >= 90 ? "text-bg-warning" : null))
            .ToList();

    /// <summary>Failed engines first, then ones still building.</summary>
    private static List<DashboardWidgetRow> CameraAiEngine(DashboardWidgetData d) =>
        d.Cameras.Where(c => c.EngineFailed == true || c.EngineBuilding == true)
            .OrderByDescending(c => c.EngineFailed == true).ThenBy(c => c.Name)
            .Select(c => c.EngineFailed == true
                ? CameraRow(c, 2, "Failed", note: "check the vision log", badge: "Failed", badgeClass: "text-bg-danger")
                : CameraRow(c, 1, "Starting", note: "building the detection engine", badge: "Starting", badgeClass: "text-bg-info"))
            .ToList();

    internal static bool IsHuman(CameraEventCountRow r) => r.Name is "Human" or nameof(DetectionKind.Human) or nameof(DetectionKind.Face);
    internal static bool IsVehicle(CameraEventCountRow r) => r.Name is "Vehicle" or nameof(DetectionKind.Vehicle);
    internal static bool IsAnimal(CameraEventCountRow r) => r.Name is "Animal" or nameof(DetectionKind.Animal);
    internal static bool IsObject(CameraEventCountRow r) =>
        r.Name is "Object" or nameof(DetectionKind.ObjectAppeared) or nameof(DetectionKind.ObjectMissing);

    /// <summary>Per-camera counts for one look-back window, with the same one-source-per-camera rule
    /// as the node cards: AI detections for a camera running AI detection, the camera's own analytics
    /// otherwise. Plain motion rows (Kind "Motion") always count for the motion widget.</summary>
    private static List<DashboardWidgetRow> CameraEvents(DashboardWidgetData d, IReadOnlyList<CameraEventCountRow> counts,
        Func<CameraEventCountRow, bool> include, bool objects, string? window)
    {
        var byCamera = counts.ToLookup(r => r.CameraId);
        var label = WindowLabel(window);
        return d.Cameras
            .Select(c =>
            {
                var rows = byCamera[c.Id].Where(r => r.Kind == "Motion" || (r.Kind == "Ai") == c.AiOn && r.Kind != "Tag");
                return (Camera: c, Count: PickWindow(DashboardService.Windows(rows, include, objects), window));
            })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Camera.Name)
            .Select(x => CameraRow(x.Camera, x.Count, x.Count.ToString("N0", CultureInfo.InvariantCulture), note: "last " + label))
            .ToList();
    }

    // ── Nodes ───────────────────────────────────────────────────────────────

    private static DashboardWidgetRow NodeRow(DashboardWidgetData d, DashNode n, double? value, string display,
        string? note = null, string? badge = null, string? badgeClass = null) =>
        new(n.Id.ToString(), n.Name, "node", d.CanOpenNodes ? $"/Admin/Nodes/{n.Id}" : null, n.Id, n.Name,
            value, display, note, badge, badgeClass);

    private static bool HostFresh(DashNode n, DateTime now) =>
        n.HostStatsUpdatedAt is { } at && now - at < DashboardService.NodeOnlineWindow;

    /// <summary>Cameras assigned to the node, or recording on it right now because of a failover.</summary>
    private static List<DashCamera> CamerasOn(DashboardWidgetData d, DashNode n) =>
        d.Cameras.Where(c => c.NodeId == n.Id || c.RecordingNodeId == n.Id).ToList();

    /// <summary>Nodes with no cameras are left out of the camera-derived widgets, where their zeros
    /// would only push real figures down the list.</summary>
    private static IEnumerable<(DashNode Node, List<DashCamera> Cameras)> NodesWithCameras(DashboardWidgetData d) =>
        d.Nodes.Select(n => (Node: n, Cameras: CamerasOn(d, n))).Where(x => x.Cameras.Count > 0);

    private static List<DashboardWidgetRow> NodeCameras(DashboardWidgetData d) =>
        NodesWithCameras(d)
            .OrderByDescending(x => x.Cameras.Count).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, x.Cameras.Count, x.Cameras.Count.ToString(CultureInfo.InvariantCulture),
                note: $"{x.Cameras.Count(c => c.Enabled)} enabled"))
            .ToList();

    private static List<DashboardWidgetRow> NodeOfflineCameras(DashboardWidgetData d) =>
        NodesWithCameras(d)
            .Select(x => (x.Node, Offline: x.Cameras.Count(c => c.Enabled && !HealthFresh(c, d.NowUtc)), Total: x.Cameras.Count(c => c.Enabled)))
            .Where(x => x.Offline > 0)
            .OrderByDescending(x => x.Offline).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, x.Offline, x.Offline.ToString(CultureInfo.InvariantCulture), note: $"of {x.Total} enabled"))
            .ToList();

    /// <summary>Disk use is reported for every node, cameras or not — a full disk matters either way.</summary>
    private static List<DashboardWidgetRow> NodeStorage(DashboardWidgetData d) =>
        d.Nodes.Where(n => n.StorageTotalBytes is > 0 && n.StorageFreeBytes is not null)
            .Select(n => (Node: n, Used: n.StorageTotalBytes!.Value - n.StorageFreeBytes!.Value))
            .OrderByDescending(x => x.Used).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, x.Used, FormatBytes(x.Used),
                note: $"{100.0 * x.Used / x.Node.StorageTotalBytes!.Value:0}% of {FormatBytes(x.Node.StorageTotalBytes.Value)}"))
            .ToList();

    /// <summary>Main-stream fps of the cameras the node is recording right now.</summary>
    private static List<DashboardWidgetRow> NodeFps(DashboardWidgetData d) =>
        NodesWithCameras(d)
            .Select(x =>
            {
                var fresh = d.Cameras.Where(c => c.Enabled && c.RecordingNodeId == x.Node.Id && HealthFresh(c, d.NowUtc)).ToList();
                return (x.Node, Fps: fresh.Sum(c => c.Fps ?? 0), Reporting: fresh.Count);
            })
            .Where(x => x.Reporting > 0)
            .OrderByDescending(x => x.Fps).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, x.Fps, x.Fps.ToString("N0", CultureInfo.InvariantCulture), note: $"from {x.Reporting} camera(s)"))
            .ToList();

    /// <summary>The node's own network load (receive + send) from its last heartbeat.</summary>
    private static List<DashboardWidgetRow> NodeBandwidth(DashboardWidgetData d) =>
        d.Nodes.Where(n => HostFresh(n, d.NowUtc) && (n.NetReceiveBytesPerSec is not null || n.NetSendBytesPerSec is not null))
            .Select(n => (Node: n, Rx: n.NetReceiveBytesPerSec ?? 0, Tx: n.NetSendBytesPerSec ?? 0))
            .OrderByDescending(x => x.Rx + x.Tx).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, (x.Rx + x.Tx) * 8 / 1_000_000.0, Mbits(x.Rx + x.Tx),
                note: $"↓ {Mbits(x.Rx)} · ↑ {Mbits(x.Tx)}"))
            .ToList();

    private static List<DashboardWidgetRow> NodeCpu(DashboardWidgetData d) =>
        d.Nodes.Where(n => HostFresh(n, d.NowUtc) && n.CpuPercent is not null)
            .OrderByDescending(n => n.CpuPercent).ThenBy(n => n.Name)
            .Select(n => NodeRow(d, n, n.CpuPercent, n.CpuPercent!.Value.ToString("0", CultureInfo.InvariantCulture) + "%",
                badge: n.CpuPercent >= 90 ? "High" : null, badgeClass: n.CpuPercent >= 90 ? "text-bg-danger" : null))
            .ToList();

    /// <summary>Least time left first. Nodes without cameras, or without a write rate to estimate
    /// from, have no estimate and are left out.</summary>
    private static List<DashboardWidgetRow> NodeDaysRemaining(DashboardWidgetData d) =>
        NodesWithCameras(d)
            .Where(x => x.Node.DaysRemaining is not null)
            .OrderBy(x => x.Node.DaysRemaining).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, x.Node.DaysRemaining, FormatDays(x.Node.DaysRemaining!.Value),
                note: x.Node.StorageFreeBytes is { } free ? FormatBytes(free) + " free" : null,
                badge: x.Node.DaysRemaining < 3 ? "Low" : null, badgeClass: x.Node.DaysRemaining < 3 ? "text-bg-danger" : null))
            .ToList();

    /// <summary>One row per problem, worst first: offline, failed over, storage pressure, clock skew,
    /// outdated build. Within a severity, the longest-running problem first.</summary>
    private static List<DashboardWidgetRow> NodeHealth(DashboardWidgetData d)
    {
        var issues = new List<(DashNode Node, int Severity, DateTime? Since, string Issue, string Badge, string BadgeClass)>();
        foreach (var n in d.Nodes)
        {
            var online = n.LastSeenAt is { } seen && d.NowUtc - seen < DashboardService.NodeOnlineWindow;
            if (!online)
                issues.Add((n, 5, n.LastSeenAt, n.LastSeenAt is null ? "never connected" : "not seen", "Offline", "text-bg-danger"));
            if (n.FailoverState == NodeFailoverState.FailedOverAway)
                issues.Add((n, 4, n.FailoverSinceUtc,
                    n.FailoverReason == NodeFailoverReason.Maintenance ? "in maintenance — cameras on backup" : "failed over — cameras on backup",
                    n.FailoverReason == NodeFailoverReason.Maintenance ? "Maintenance" : "Failed over", "text-bg-warning"));
            if (n.StoragePressureActive)
                issues.Add((n, 3, n.StoragePressureSince, "disk over the storage limit — deleting or archiving early", "Storage pressure", "text-bg-warning"));
            if (n.ClockSkewSeconds is { } skew && Math.Abs(skew) > ClockSkewWarningSeconds)
                issues.Add((n, 2, null, $"clock {FormatDuration(TimeSpan.FromSeconds(Math.Abs(skew)))} {(skew > 0 ? "behind" : "ahead")}", "Clock skew", "text-bg-warning"));
            if (n.LatestBuild is { } latest && NodeVersionComparer.IsNewer(latest, n.Version))
                issues.Add((n, 1, null, $"running {n.Version ?? "unknown"}, {latest} available", "Outdated", "text-bg-secondary"));
        }

        return issues
            .OrderByDescending(x => x.Severity).ThenBy(x => x.Since ?? DateTime.MaxValue).ThenBy(x => x.Node.Name)
            .Select(x => NodeRow(d, x.Node, x.Severity,
                x.Since is { } since ? FormatDuration(d.NowUtc - since) : "—", x.Issue, x.Badge, x.BadgeClass))
            .ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IEnumerable<T> OrderByValue<T>(IEnumerable<T> items, Func<T, long> value, bool ascending) where T : notnull =>
        ascending ? items.OrderBy(value) : items.OrderByDescending(value);

    internal static int PickWindow(WindowCounts c, string? window) => NormalizeWindow(window) switch
    {
        "1h" => c.LastHour,
        "7d" => c.Last7Days,
        "30d" => c.Last30Days,
        _ => c.Last24Hours,
    };

    public static string NormalizeWindow(string? window) =>
        window is "1h" or "24h" or "7d" or "30d" ? window : DefaultWindow;

    private static string WindowLabel(string? window) => NormalizeWindow(window) switch
    {
        "1h" => "hour",
        "7d" => "7 days",
        "30d" => "30 days",
        _ => "24 hours",
    };

    internal static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }

    private static string FormatDays(double days) =>
        days >= 2 ? days.ToString("0", CultureInfo.InvariantCulture) + " days"
        : FormatDuration(TimeSpan.FromDays(days));

    /// <summary>Binary units with GB/TB labels — the same convention the node cards already use.</summary>
    internal static string FormatBytes(long bytes)
    {
        const double Gb = 1073741824d, Tb = Gb * 1024, Mb = 1048576d;
        if (bytes >= Tb) return (bytes / Tb).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
        if (bytes >= Gb) return (bytes / Gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        return (bytes / Mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    private static string FormatKbps(int kbps) =>
        kbps >= 1000 ? (kbps / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbit/s"
        : kbps.ToString(CultureInfo.InvariantCulture) + " kbit/s";

    private static string Mbits(long bytesPerSec) =>
        (bytesPerSec * 8 / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbit/s";

    private static string FormatMegapixels(long pixels) =>
        (pixels / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " MP";

    /// <summary>Stands in for a time in a row's Note; the browser replaces it with NoteTimeUtc in the
    /// viewer's local time (dashboard-widgets.js / dashboard-report.js).</summary>
    public const string TimePlaceholder = "{time}";
}

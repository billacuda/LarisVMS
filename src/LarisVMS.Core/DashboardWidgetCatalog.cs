using System.Text.Json.Serialization;
using LarisVMS.Core.Localization;

namespace LarisVMS.Core;

/// <summary>How a dashboard widget draws: a ranked top-10 list (the common case, served by
/// GET /api/dashboard/widgets), or one of the original fixed sections that dashboard.js still renders
/// from GET /api/dashboard (summary tiles, node cards, camera health table), or a thumbnail strip.
/// Serialized by name — dashboard-widgets.js switches on it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardWidgetKind>))]
public enum DashboardWidgetKind { TopList, Summary, NodeCards, CameraTable, Snapshots, Heatmap }

/// <summary>One entry in the dashboard's widget picker. AssetKind is what each row points at
/// ("camera", "node", "alert", "detection") — the report popup's filter and links key off it.
/// HasWindow adds a 1h/24h/7d/30d selector; HasDirectionToggle lets the user flip highest/lowest
/// first; HasKindFilter adds a settings button for choosing which detection types to show.
/// RequiredPolicy hides the widget from anyone the named authorization policy denies.</summary>
public record DashboardWidgetDefinition(string Key, string Title, string Group, DashboardWidgetKind Kind,
    string AssetKind, string ValueHeader, int DefaultW, int DefaultH,
    bool HasWindow = false, bool HasDirectionToggle = false, string? RequiredPolicy = null,
    string? EmptyMessage = null, bool HasKindFilter = false);

/// <summary>A widget's place on one user's dashboard, as stored in the dashboardLayout preference.</summary>
public record DashboardLayoutItem(string Key, int X, int Y, int W, int H);

/// <summary>Every widget the dashboard offers. Keys are persisted in users' saved layouts, so a key
/// must never be renamed — retire it and add a new one instead (dashboard.js drops unknown keys).</summary>
public static class DashboardWidgetCatalog
{
    public const string Summary = "summary";
    public const string NodeCards = "node-cards";
    public const string CameraTable = "camera-table";

    public const string CameraStorage = "cam-storage";
    public const string CameraFps = "cam-fps";
    public const string CameraBitrate = "cam-bitrate";
    public const string CameraRetention = "cam-retention";
    public const string CameraOffline = "cam-offline";
    public const string CameraMotion = "cam-motion";
    public const string CameraHuman = "cam-human";
    public const string CameraVehicle = "cam-vehicle";
    public const string CameraAnimal = "cam-animal";
    public const string CameraObject = "cam-object";
    public const string CameraLastRecording = "cam-last-recording";
    public const string CameraResolution = "cam-resolution";
    public const string CameraReconnects = "cam-reconnects";
    public const string CameraQuota = "cam-quota";
    public const string CameraAiEngine = "cam-ai-engine";

    public const string NodeCameras = "node-cameras";
    public const string NodeOfflineCameras = "node-offline-cameras";
    public const string NodeStorage = "node-storage";
    public const string NodeFps = "node-fps";
    public const string NodeBandwidth = "node-bandwidth";
    public const string NodeCpu = "node-cpu";
    public const string NodeDaysRemaining = "node-days-remaining";
    public const string NodeHealth = "node-health";

    public const string RecentAlerts = "recent-alerts";
    public const string LatestDetections = "latest-detections";
    public const string CameraActivity = "cam-activity";
    public const string SpeedLog = "speed-log";

    private const int ListW = 4, ListH = 6;

    /// <summary>A property, not a field: titles and messages are built per call, in the viewer's language.</summary>
    public static IReadOnlyList<DashboardWidgetDefinition> All =>
    [
        new(Summary, Loc.T("widget.summary.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.Summary, "none", "", 12, 3),
        new(CameraTable, Loc.T("widget.cameraTable.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.CameraTable, "camera", "", 12, 9),
        new(NodeCards, Loc.T("widget.nodeCards.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.NodeCards, "node", "", 12, 6),
        new(LatestDetections, Loc.T("widget.latestDetections.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.Snapshots, "detection", Loc.T("widget.latestDetections.value"), 12, 4,
            EmptyMessage: Loc.T("widget.latestDetections.empty"), HasKindFilter: true),
        // One camera's 7-day activity heatmap; the camera is picked in the widget's own title bar.
        new(CameraActivity, Loc.T("widget.cameraActivity.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.Heatmap, "camera", "", 6, 5,
            EmptyMessage: Loc.T("widget.cameraActivity.empty")),
        new(RecentAlerts, Loc.T("widget.recentAlerts.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.TopList, "alert", Loc.T("widget.recentAlerts.value"), ListW, ListH,
            RequiredPolicy: "Alerts.Edit", EmptyMessage: Loc.T("widget.recentAlerts.empty")),
        // Latest speed-trap measurements; the report is the full log with its own columns.
        new(SpeedLog, Loc.T("widget.speedLog.title"), Loc.T("widget.group.overview"), DashboardWidgetKind.TopList, "speed", Loc.T("widget.speedLog.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.speedLog.empty")),

        new(CameraOffline, Loc.T("widget.cameraOffline.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraOffline.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraOffline.empty")),
        new(CameraLastRecording, Loc.T("widget.cameraLastRecording.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraLastRecording.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraLastRecording.empty")),
        new(CameraStorage, Loc.T("widget.cameraStorage.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraStorage.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraStorage.empty")),
        new(CameraRetention, Loc.T("widget.cameraRetention.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraRetention.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraRetention.empty")),
        new(CameraFps, Loc.T("widget.cameraFps.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraFps.value"), ListW, ListH,
            HasDirectionToggle: true, EmptyMessage: Loc.T("widget.cameraFps.empty")),
        new(CameraBitrate, Loc.T("widget.cameraBitrate.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraBitrate.value"), ListW, ListH,
            HasDirectionToggle: true, EmptyMessage: Loc.T("widget.cameraBitrate.empty")),
        new(CameraResolution, Loc.T("widget.cameraResolution.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraResolution.value"), ListW, ListH,
            HasDirectionToggle: true, EmptyMessage: Loc.T("widget.cameraResolution.empty")),
        new(CameraMotion, Loc.T("widget.cameraMotion.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraMotion.value"), ListW, ListH,
            HasWindow: true, EmptyMessage: Loc.T("widget.cameraMotion.empty")),
        new(CameraHuman, Loc.T("widget.cameraHuman.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraHuman.value"), ListW, ListH,
            HasWindow: true, EmptyMessage: Loc.T("widget.cameraHuman.empty")),
        new(CameraVehicle, Loc.T("widget.cameraVehicle.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraVehicle.value"), ListW, ListH,
            HasWindow: true, EmptyMessage: Loc.T("widget.cameraVehicle.empty")),
        new(CameraAnimal, Loc.T("widget.cameraAnimal.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraAnimal.value"), ListW, ListH,
            HasWindow: true, EmptyMessage: Loc.T("widget.cameraAnimal.empty")),
        new(CameraObject, Loc.T("widget.cameraObject.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraObject.value"), ListW, ListH,
            HasWindow: true, EmptyMessage: Loc.T("widget.cameraObject.empty")),
        new(CameraReconnects, Loc.T("widget.cameraReconnects.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraReconnects.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraReconnects.empty")),
        new(CameraQuota, Loc.T("widget.cameraQuota.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraQuota.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraQuota.empty")),
        new(CameraAiEngine, Loc.T("widget.cameraAiEngine.title"), Loc.T("widget.group.cameras"), DashboardWidgetKind.TopList, "camera", Loc.T("widget.cameraAiEngine.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.cameraAiEngine.empty")),

        new(NodeHealth, Loc.T("widget.nodeHealth.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeHealth.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeHealth.empty")),
        new(NodeDaysRemaining, Loc.T("widget.nodeDaysRemaining.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeDaysRemaining.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeDaysRemaining.empty")),
        new(NodeCameras, Loc.T("widget.nodeCameras.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeCameras.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeCameras.empty")),
        new(NodeOfflineCameras, Loc.T("widget.nodeOfflineCameras.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeOfflineCameras.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeOfflineCameras.empty")),
        new(NodeStorage, Loc.T("widget.nodeStorage.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeStorage.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeStorage.empty")),
        new(NodeFps, Loc.T("widget.nodeFps.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeFps.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeFps.empty")),
        new(NodeBandwidth, Loc.T("widget.nodeBandwidth.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeBandwidth.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeBandwidth.empty")),
        new(NodeCpu, Loc.T("widget.nodeCpu.title"), Loc.T("widget.group.nodes"), DashboardWidgetKind.TopList, "node", Loc.T("widget.nodeCpu.value"), ListW, ListH,
            EmptyMessage: Loc.T("widget.nodeCpu.empty")),
    ];

    public static DashboardWidgetDefinition? Find(string key) => All.FirstOrDefault(w => string.Equals(w.Key, key, StringComparison.Ordinal));

    /// <summary>The latest-detections widget's type token for plain motion (no object, tag or camera
    /// classification). Off unless the user ticks it.</summary>
    public const string MotionKindToken = "Motion";

    /// <summary>Parses one widget request, "key" or "key|option|option" — an option is a look-back
    /// window (1h/24h/7d/30d), "asc" for lowest-first, or "k=Human,Vehicle,Motion" for the detection
    /// types to show ("k=" alone means none). Unrecognized options are ignored.</summary>
    public static (string Key, Dtos.DashboardWidgetOptions Options) ParseSpec(string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return ("", new Dtos.DashboardWidgetOptions());
        string? window = null;
        var ascending = false;
        IReadOnlyList<string>? kinds = null;
        foreach (var option in parts.Skip(1))
        {
            if (option is "1h" or "24h" or "7d" or "30d") window = option;
            else if (option == "asc") ascending = true;
            else if (option.StartsWith("k=", StringComparison.Ordinal)) kinds = ParseKinds(option[2..]);
        }
        return (parts[0], new Dtos.DashboardWidgetOptions(window, ascending, kinds));
    }

    /// <summary>A comma-separated detection type list; empty text is an explicit empty list.</summary>
    public static IReadOnlyList<string> ParseKinds(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();

    /// <summary>What a user sees before they've customized anything.</summary>
    public static readonly IReadOnlyList<DashboardLayoutItem> DefaultLayout =
    [
        new(Summary, 0, 0, 12, 3),
        new(CameraOffline, 0, 3, 4, ListH),
        new(CameraLastRecording, 4, 3, 4, ListH),
        new(NodeDaysRemaining, 8, 3, 4, ListH),
        new(CameraStorage, 0, 9, 4, ListH),
        new(NodeHealth, 4, 9, 4, ListH),
        new(NodeCpu, 8, 9, 4, ListH),
        new(CameraTable, 0, 15, 12, 9),
    ];
}

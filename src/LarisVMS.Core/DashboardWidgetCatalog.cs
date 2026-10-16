using System.Text.Json.Serialization;

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

    public static readonly IReadOnlyList<DashboardWidgetDefinition> All =
    [
        new(Summary, "Status summary", "Overview", DashboardWidgetKind.Summary, "none", "", 12, 3),
        new(CameraTable, "Camera health", "Overview", DashboardWidgetKind.CameraTable, "camera", "", 12, 9),
        new(NodeCards, "Node overview", "Overview", DashboardWidgetKind.NodeCards, "node", "", 12, 6),
        new(LatestDetections, "Latest detections", "Overview", DashboardWidgetKind.Snapshots, "detection", "When", 12, 4,
            EmptyMessage: "No recent detections", HasKindFilter: true),
        // One camera's 7-day activity heatmap; the camera is picked in the widget's own title bar.
        new(CameraActivity, "Camera activity", "Overview", DashboardWidgetKind.Heatmap, "camera", "", 6, 5,
            EmptyMessage: "No events on this camera in the last 7 days"),
        new(RecentAlerts, "Recent alerts", "Overview", DashboardWidgetKind.TopList, "alert", "When", ListW, ListH,
            RequiredPolicy: "Alerts.Edit", EmptyMessage: "No recent alerts"),
        // Latest speed-trap measurements; the report is the full log with its own columns.
        new(SpeedLog, "Speed log", "Overview", DashboardWidgetKind.TopList, "speed", "Speed", ListW, ListH,
            EmptyMessage: "No speeds measured yet. Set up a speed trap on a camera's Speed traps page."),

        new(CameraOffline, "Cameras offline", "Cameras", DashboardWidgetKind.TopList, "camera", "Offline for", ListW, ListH,
            EmptyMessage: "All cameras online"),
        new(CameraLastRecording, "Time since last recording", "Cameras", DashboardWidgetKind.TopList, "camera", "Last recorded", ListW, ListH,
            EmptyMessage: "No cameras have recorded yet"),
        new(CameraStorage, "Storage used", "Cameras", DashboardWidgetKind.TopList, "camera", "Used", ListW, ListH,
            EmptyMessage: "No footage stored"),
        new(CameraRetention, "Oldest footage", "Cameras", DashboardWidgetKind.TopList, "camera", "Oldest", ListW, ListH,
            EmptyMessage: "No footage stored"),
        new(CameraFps, "Frame rate", "Cameras", DashboardWidgetKind.TopList, "camera", "FPS", ListW, ListH,
            HasDirectionToggle: true, EmptyMessage: "No cameras reporting"),
        new(CameraBitrate, "Bandwidth", "Cameras", DashboardWidgetKind.TopList, "camera", "Bitrate", ListW, ListH,
            HasDirectionToggle: true, EmptyMessage: "No cameras reporting"),
        new(CameraResolution, "Resolution", "Cameras", DashboardWidgetKind.TopList, "camera", "Resolution", ListW, ListH,
            HasDirectionToggle: true, EmptyMessage: "No resolutions known yet"),
        new(CameraMotion, "Motion events", "Cameras", DashboardWidgetKind.TopList, "camera", "Events", ListW, ListH,
            HasWindow: true, EmptyMessage: "No motion events"),
        new(CameraHuman, "Human detections", "Cameras", DashboardWidgetKind.TopList, "camera", "Humans", ListW, ListH,
            HasWindow: true, EmptyMessage: "No humans detected"),
        new(CameraVehicle, "Vehicle detections", "Cameras", DashboardWidgetKind.TopList, "camera", "Vehicles", ListW, ListH,
            HasWindow: true, EmptyMessage: "No vehicles detected"),
        new(CameraAnimal, "Animal detections", "Cameras", DashboardWidgetKind.TopList, "camera", "Animals", ListW, ListH,
            HasWindow: true, EmptyMessage: "No animals detected"),
        new(CameraObject, "Object detections", "Cameras", DashboardWidgetKind.TopList, "camera", "Objects", ListW, ListH,
            HasWindow: true, EmptyMessage: "No objects detected"),
        new(CameraReconnects, "Most reconnects", "Cameras", DashboardWidgetKind.TopList, "camera", "Reconnects", ListW, ListH,
            EmptyMessage: "No reconnects"),
        new(CameraQuota, "Near storage quota", "Cameras", DashboardWidgetKind.TopList, "camera", "Of quota", ListW, ListH,
            EmptyMessage: "No cameras have a storage quota"),
        new(CameraAiEngine, "AI detection problems", "Cameras", DashboardWidgetKind.TopList, "camera", "State", ListW, ListH,
            EmptyMessage: "All detection engines ready"),

        new(NodeHealth, "Node health issues", "Nodes", DashboardWidgetKind.TopList, "node", "Since", ListW, ListH,
            EmptyMessage: "All nodes healthy"),
        new(NodeDaysRemaining, "Least storage time left", "Nodes", DashboardWidgetKind.TopList, "node", "Time left", ListW, ListH,
            EmptyMessage: "No estimate yet — needs a day of recording"),
        new(NodeCameras, "Cameras per node", "Nodes", DashboardWidgetKind.TopList, "node", "Cameras", ListW, ListH,
            EmptyMessage: "No nodes have cameras"),
        new(NodeOfflineCameras, "Offline cameras per node", "Nodes", DashboardWidgetKind.TopList, "node", "Offline", ListW, ListH,
            EmptyMessage: "All cameras online"),
        new(NodeStorage, "Node storage used", "Nodes", DashboardWidgetKind.TopList, "node", "Used", ListW, ListH,
            EmptyMessage: "No storage figures yet"),
        new(NodeFps, "Node frame rate", "Nodes", DashboardWidgetKind.TopList, "node", "FPS", ListW, ListH,
            EmptyMessage: "No cameras reporting"),
        new(NodeBandwidth, "Node bandwidth", "Nodes", DashboardWidgetKind.TopList, "node", "Mbit/s", ListW, ListH,
            EmptyMessage: "No load figures yet"),
        new(NodeCpu, "Node CPU", "Nodes", DashboardWidgetKind.TopList, "node", "CPU", ListW, ListH,
            EmptyMessage: "No load figures yet"),
    ];

    private static readonly Dictionary<string, DashboardWidgetDefinition> ByKey =
        All.ToDictionary(w => w.Key, StringComparer.Ordinal);

    public static DashboardWidgetDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

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

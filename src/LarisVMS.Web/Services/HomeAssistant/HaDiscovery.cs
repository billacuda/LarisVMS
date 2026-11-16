using System.Text.Json;
using System.Text.Json.Nodes;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Services.HomeAssistant;

/// <summary>A camera as Home Assistant sees it: one HA device.</summary>
public sealed record HaCamera(Guid Id, string Name, Guid? NodeId);

/// <summary>A recording node: one HA device that its cameras hang off ("connected via").</summary>
public sealed record HaNode(Guid Id, string Name);

/// <summary>One MQTT message to publish. Payload null publishes an empty retained message, which is how
/// a discovery config (and its entity) is removed from Home Assistant.</summary>
public sealed record HaMessage(string Topic, string? Payload, bool Retain = true);

/// <summary>What Home Assistant may do on a camera, from the bridge role's permissions.</summary>
public sealed record HaCameraAbilities(bool CanRecord, bool CanBookmark);

/// <summary>
/// MQTT topic layout for one LarisVMS server:
///   {prefix}/status                          online / offline (retained; the last will sets offline)
///   {prefix}/camera/{id}/{item}              camera state (motion, person, online, snapshot, …)
///   {prefix}/camera/{id}/recording/set       ON / OFF from Home Assistant
///   {prefix}/camera/{id}/bookmark/press      PRESS from Home Assistant
///   {prefix}/node/{id}/{item}                node state (online, storage_free)
///   {discovery}/{component}/larisvms_{id}/{item}/config   Home Assistant discovery
/// Ids are GUIDs without dashes.
/// </summary>
public sealed class HaTopics(string prefix, string discoveryPrefix)
{
    public string Prefix { get; } = Clean(prefix, Core.Entities.HomeAssistantSettings.DefaultTopicPrefix);
    public string DiscoveryPrefix { get; } = Clean(discoveryPrefix, Core.Entities.HomeAssistantSettings.DefaultDiscoveryPrefix);

    public string Availability => $"{Prefix}/status";

    /// <summary>Home Assistant's own birth/last-will topic: "online" means it restarted and wants
    /// discovery again.</summary>
    public string HomeAssistantStatus => $"{DiscoveryPrefix}/status";

    public string CommandFilter => $"{Prefix}/camera/+/+/+";

    public string Camera(Guid cameraId, string item) => $"{Prefix}/camera/{cameraId:N}/{item}";

    public string Node(Guid nodeId, string item) => $"{Prefix}/node/{nodeId:N}/{item}";

    public string Config(string component, string objectId, string item) => $"{DiscoveryPrefix}/{component}/{objectId}/{item}/config";

    /// <summary>A camera command topic's camera and command: "recording/set" or "bookmark/press".</summary>
    public bool TryParseCommand(string topic, out Guid cameraId, out string command)
    {
        cameraId = Guid.Empty;
        command = "";
        var start = $"{Prefix}/camera/";
        if (!topic.StartsWith(start, StringComparison.Ordinal)) return false;
        var parts = topic[start.Length..].Split('/');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out cameraId)) return false;
        command = parts[1] + "/" + parts[2];
        return command is HaDiscovery.RecordingCommand or HaDiscovery.BookmarkCommand;
    }

    /// <summary>A topic prefix is a few path segments of letters, digits, '-' and '_'; anything else
    /// (wildcards, empty segments, spaces) falls back to the default.</summary>
    internal static string Clean(string? value, string fallback)
    {
        var trimmed = value?.Trim().Trim('/') ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 100) return fallback;
        return trimmed.Split('/').All(s => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            ? trimmed
            : fallback;
    }
}

/// <summary>
/// Builds Home Assistant MQTT discovery messages. Pure: no broker and no database, so the payloads are
/// unit tested as they are. Entity names are in the server's default language (Home Assistant shows
/// them as given). See https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery.
/// </summary>
public static class HaDiscovery
{
    public const string On = "ON";
    public const string Off = "OFF";
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Press = "PRESS";
    public const string RecordingCommand = "recording/set";
    public const string BookmarkCommand = "bookmark/press";

    /// <summary>The detection binary sensors, in the order they're listed.</summary>
    public static readonly IReadOnlyList<string> DetectionItems = ["person", "vehicle", "animal"];

    /// <summary>Every per-camera discovery item, so a camera Home Assistant may no longer see can be
    /// removed completely: (component, item).</summary>
    public static readonly IReadOnlyList<(string Component, string Item)> CameraEntities =
    [
        ("binary_sensor", "motion"), ("binary_sensor", "person"), ("binary_sensor", "vehicle"), ("binary_sensor", "animal"),
        ("binary_sensor", "online"), ("image", "snapshot"), ("sensor", "last_detection"), ("sensor", "last_speed"),
        ("sensor", "last_event"), ("switch", "recording"), ("button", "bookmark"),
    ];

    public static readonly IReadOnlyList<(string Component, string Item)> NodeEntities =
    [
        ("binary_sensor", "online"), ("sensor", "storage_free"),
    ];

    public static string CameraObjectId(Guid cameraId) => $"larisvms_{cameraId:N}";

    public static string NodeObjectId(Guid nodeId) => $"larisvms_node_{nodeId:N}";

    /// <summary>Which detection sensor a span's category belongs to (an AI category name such as
    /// "Vehicle", or a camera's own DetectionKind), or null for one Home Assistant doesn't get a
    /// sensor for.</summary>
    public static string? DetectionItem(string? category, DetectionKind? kind = null)
    {
        if (kind is DetectionKind.Human || string.Equals(category, CocoCategoryMap.Human, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Person", StringComparison.OrdinalIgnoreCase)) return "person";
        if (kind is DetectionKind.Vehicle || string.Equals(category, CocoCategoryMap.Vehicle, StringComparison.OrdinalIgnoreCase)) return "vehicle";
        if (kind is DetectionKind.Animal || string.Equals(category, CocoCategoryMap.Animal, StringComparison.OrdinalIgnoreCase)) return "animal";
        return null;
    }

    /// <summary>The discovery configs for one camera. The recording switch and bookmark button are
    /// only offered when the role may use them; otherwise their configs are removed.</summary>
    public static IEnumerable<HaMessage> CameraConfigs(HaTopics topics, HaCamera camera, HaCameraAbilities abilities, string speedUnit)
    {
        var objectId = CameraObjectId(camera.Id);
        JsonObject Device()
        {
            var device = new JsonObject
            {
                ["identifiers"] = new JsonArray(objectId),
                ["name"] = camera.Name,
                ["manufacturer"] = "LarisVMS",
                ["model"] = EntityName("camera"),
            };
            if (camera.NodeId is { } node) device["via_device"] = NodeObjectId(node);
            return device;
        }

        JsonObject Base(string item) => new()
        {
            ["name"] = EntityName(item),
            ["unique_id"] = $"{objectId}_{item}",
            ["object_id"] = $"{Slug(camera.Name)}_{item}",
            ["availability_topic"] = topics.Availability,
            ["device"] = Device(),
        };

        HaMessage Config(string component, string item, JsonObject payload) =>
            new(topics.Config(component, objectId, item), payload.ToJsonString(Json));

        var motion = Base("motion");
        motion["state_topic"] = topics.Camera(camera.Id, "motion");
        motion["device_class"] = "motion";
        yield return Config("binary_sensor", "motion", motion);

        foreach (var item in DetectionItems)
        {
            var detection = Base(item);
            detection["state_topic"] = topics.Camera(camera.Id, item);
            if (item == "person") detection["device_class"] = "occupancy";
            else detection["icon"] = item == "vehicle" ? "mdi:car" : "mdi:paw";
            yield return Config("binary_sensor", item, detection);
        }

        var online = Base("online");
        online["state_topic"] = topics.Camera(camera.Id, "online");
        online["device_class"] = "connectivity";
        online["entity_category"] = "diagnostic";
        yield return Config("binary_sensor", "online", online);

        var snapshot = Base("snapshot");
        snapshot["image_topic"] = topics.Camera(camera.Id, "snapshot");
        snapshot["content_type"] = "image/jpeg";
        yield return Config("image", "snapshot", snapshot);

        var label = Base("last_detection");
        label["state_topic"] = topics.Camera(camera.Id, "last_detection");
        label["icon"] = "mdi:magnify";
        yield return Config("sensor", "last_detection", label);

        var speed = Base("last_speed");
        speed["state_topic"] = topics.Camera(camera.Id, "last_speed");
        speed["device_class"] = "speed";
        speed["unit_of_measurement"] = SpeedUnits.Normalize(speedUnit) == SpeedUnits.Mph ? "mph" : "km/h";
        yield return Config("sensor", "last_speed", speed);

        var lastEvent = Base("last_event");
        lastEvent["state_topic"] = topics.Camera(camera.Id, "last_event");
        lastEvent["device_class"] = "timestamp";
        yield return Config("sensor", "last_event", lastEvent);

        if (abilities.CanRecord)
        {
            var recording = Base("recording");
            recording["state_topic"] = topics.Camera(camera.Id, "recording");
            recording["command_topic"] = topics.Camera(camera.Id, RecordingCommand);
            recording["icon"] = "mdi:record-rec";
            yield return Config("switch", "recording", recording);
        }
        else yield return new HaMessage(topics.Config("switch", objectId, "recording"), null);

        if (abilities.CanBookmark)
        {
            var bookmark = Base("bookmark");
            bookmark["command_topic"] = topics.Camera(camera.Id, BookmarkCommand);
            bookmark["payload_press"] = Press;
            bookmark["icon"] = "mdi:bookmark-plus";
            yield return Config("button", "bookmark", bookmark);
        }
        else yield return new HaMessage(topics.Config("button", objectId, "bookmark"), null);
    }

    /// <summary>The discovery configs for one recording node.</summary>
    public static IEnumerable<HaMessage> NodeConfigs(HaTopics topics, HaNode node)
    {
        var objectId = NodeObjectId(node.Id);
        JsonObject Base(string item) => new()
        {
            ["name"] = EntityName(item),
            ["unique_id"] = $"{objectId}_{item}",
            ["object_id"] = $"{Slug(node.Name)}_{item}",
            ["availability_topic"] = topics.Availability,
            ["entity_category"] = "diagnostic",
            ["device"] = new JsonObject
            {
                ["identifiers"] = new JsonArray(objectId),
                ["name"] = node.Name,
                ["manufacturer"] = "LarisVMS",
                ["model"] = EntityName("node"),
            },
        };

        var online = Base("online");
        online["state_topic"] = topics.Node(node.Id, "online");
        online["device_class"] = "connectivity";
        yield return new HaMessage(topics.Config("binary_sensor", objectId, "online"), online.ToJsonString(Json));

        var storage = Base("storage_free");
        storage["state_topic"] = topics.Node(node.Id, "storage_free");
        storage["unit_of_measurement"] = "%";
        storage["state_class"] = "measurement";
        storage["icon"] = "mdi:harddisk";
        yield return new HaMessage(topics.Config("sensor", objectId, "storage_free"), storage.ToJsonString(Json));
    }

    /// <summary>Empty retained configs that remove a camera's entities from Home Assistant.</summary>
    public static IEnumerable<HaMessage> RemoveCamera(HaTopics topics, Guid cameraId) =>
        CameraEntities.Select(e => new HaMessage(topics.Config(e.Component, CameraObjectId(cameraId), e.Item), null));

    public static IEnumerable<HaMessage> RemoveNode(HaTopics topics, Guid nodeId) =>
        NodeEntities.Select(e => new HaMessage(topics.Config(e.Component, NodeObjectId(nodeId), e.Item), null));

    /// <summary>Entity and device-model names in the current language. Literal keys, so the unused-key
    /// check can see them.</summary>
    public static string EntityName(string item) => item switch
    {
        "motion" => Loc.T("homeAssistant.entity.motion"),
        "person" => Loc.T("homeAssistant.entity.person"),
        "vehicle" => Loc.T("homeAssistant.entity.vehicle"),
        "animal" => Loc.T("homeAssistant.entity.animal"),
        "online" => Loc.T("homeAssistant.entity.online"),
        "snapshot" => Loc.T("homeAssistant.entity.snapshot"),
        "last_detection" => Loc.T("homeAssistant.entity.lastDetection"),
        "last_speed" => Loc.T("homeAssistant.entity.lastSpeed"),
        "last_event" => Loc.T("homeAssistant.entity.lastEvent"),
        "recording" => Loc.T("homeAssistant.entity.recording"),
        "bookmark" => Loc.T("homeAssistant.entity.bookmark"),
        "storage_free" => Loc.T("homeAssistant.entity.storageFree"),
        "camera" => Loc.T("homeAssistant.entity.camera"),
        "node" => Loc.T("homeAssistant.entity.node"),
        _ => item,
    };

    /// <summary>A lower-case ASCII slug for Home Assistant's suggested entity ids ("Front door" →
    /// "front_door"). Names with no ASCII letters or digits fall back to "camera".</summary>
    internal static string Slug(string name)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) chars.Append(c);
            else if (chars.Length > 0 && chars[^1] != '_') chars.Append('_');
        }
        var slug = chars.ToString().Trim('_');
        return slug.Length == 0 ? "camera" : slug;
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

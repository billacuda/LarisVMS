using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LarisVMS.Core;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Alerts.Channels;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;
using LarisVMS.Web.Services.HomeAssistant;

namespace LarisVMS.Tests;

/// <summary>Home Assistant: MQTT topics and discovery payloads, the webhook's event details, HTTP
/// Basic API keys, and the live MP4 relay.</summary>
public class HomeAssistantTests
{
    private static readonly Guid CameraId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid NodeId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly HaTopics Topics = new("larisvms", "homeassistant");

    private static JsonElement Json(string payload) => JsonDocument.Parse(payload).RootElement;

    // ── Topics ──────────────────────────────────────────────────────────

    [Fact]
    public void CameraTopicsUseTheIdWithoutDashes()
    {
        Assert.Equal("larisvms/camera/11111111222233334444555555555555/motion", Topics.Camera(CameraId, "motion"));
        Assert.Equal("larisvms/status", Topics.Availability);
        Assert.Equal("homeassistant/status", Topics.HomeAssistantStatus);
    }

    [Theory]
    [InlineData("larisvms/camera/11111111222233334444555555555555/recording/set", true, "recording/set")]
    [InlineData("larisvms/camera/11111111222233334444555555555555/bookmark/press", true, "bookmark/press")]
    [InlineData("larisvms/camera/11111111222233334444555555555555/motion/set", false, null)]
    [InlineData("larisvms/camera/not-a-guid/recording/set", false, null)]
    [InlineData("other/camera/11111111222233334444555555555555/recording/set", false, null)]
    [InlineData("larisvms/camera/11111111222233334444555555555555/recording/set/extra", false, null)]
    public void OnlyKnownCommandsAreAccepted(string topic, bool ok, string? command)
    {
        Assert.Equal(ok, Topics.TryParseCommand(topic, out var cameraId, out var parsed));
        if (ok)
        {
            Assert.Equal(CameraId, cameraId);
            Assert.Equal(command, parsed);
        }
    }

    [Theory]
    [InlineData(null, "larisvms")]
    [InlineData("", "larisvms")]
    [InlineData("  /site-a/vms/ ", "site-a/vms")]
    [InlineData("bad#topic", "larisvms")]
    [InlineData("a/+/b", "larisvms")]
    [InlineData("a//b", "larisvms")]
    [InlineData("has space", "larisvms")]
    public void TopicPrefixesAreCleaned(string? value, string expected) =>
        Assert.Equal(expected, HaTopics.Clean(value, "larisvms"));

    // ── Discovery ───────────────────────────────────────────────────────

    [Fact]
    public void ACameraGetsEveryEntityWhenTheRoleAllowsEverything()
    {
        var messages = HaDiscovery.CameraConfigs(Topics, new HaCamera(CameraId, "Front door", NodeId), new HaCameraAbilities(true, true), SpeedUnits.Kmh).ToList();

        Assert.Equal(HaDiscovery.CameraEntities.Count, messages.Count);
        Assert.All(messages, m => Assert.NotNull(m.Payload));
        Assert.All(messages, m => Assert.True(m.Retain));
        Assert.All(messages, m => Assert.StartsWith("homeassistant/", m.Topic));
    }

    [Fact]
    public void TheMotionSensorPointsAtItsStateAndTheCameraDevice()
    {
        var motion = HaDiscovery.CameraConfigs(Topics, new HaCamera(CameraId, "Front door", NodeId), new HaCameraAbilities(false, false), SpeedUnits.Kmh)
            .Single(m => m.Topic == "homeassistant/binary_sensor/larisvms_11111111222233334444555555555555/motion/config");
        var json = Json(motion.Payload!);

        Assert.Equal("larisvms/camera/11111111222233334444555555555555/motion", json.GetProperty("state_topic").GetString());
        Assert.Equal("motion", json.GetProperty("device_class").GetString());
        Assert.Equal("larisvms_11111111222233334444555555555555_motion", json.GetProperty("unique_id").GetString());
        Assert.Equal("front_door_motion", json.GetProperty("object_id").GetString());
        Assert.Equal("larisvms/status", json.GetProperty("availability_topic").GetString());
        var device = json.GetProperty("device");
        Assert.Equal("Front door", device.GetProperty("name").GetString());
        Assert.Equal("larisvms_node_aaaaaaaabbbbccccddddeeeeeeeeeeee", device.GetProperty("via_device").GetString());
    }

    [Fact]
    public void ACameraWithNoNodeHasNoViaDevice()
    {
        var motion = HaDiscovery.CameraConfigs(Topics, new HaCamera(CameraId, "Lobby", null), new HaCameraAbilities(false, false), SpeedUnits.Kmh).First();
        Assert.False(Json(motion.Payload!).GetProperty("device").TryGetProperty("via_device", out _));
    }

    [Fact]
    public void ARoleThatCantRecordOrBookmarkRemovesThoseEntities()
    {
        var messages = HaDiscovery.CameraConfigs(Topics, new HaCamera(CameraId, "Lobby", null), new HaCameraAbilities(false, false), SpeedUnits.Kmh).ToList();

        Assert.Null(messages.Single(m => m.Topic.Contains("/switch/")).Payload);
        Assert.Null(messages.Single(m => m.Topic.Contains("/button/")).Payload);
    }

    [Fact]
    public void TheRecordingSwitchAndBookmarkButtonUseTheCommandTopics()
    {
        var messages = HaDiscovery.CameraConfigs(Topics, new HaCamera(CameraId, "Lobby", null), new HaCameraAbilities(true, true), SpeedUnits.Kmh).ToList();
        var recording = Json(messages.Single(m => m.Topic.Contains("/switch/")).Payload!);
        var bookmark = Json(messages.Single(m => m.Topic.Contains("/button/")).Payload!);

        Assert.True(Topics.TryParseCommand(recording.GetProperty("command_topic").GetString()!, out _, out var recordCommand));
        Assert.Equal(HaDiscovery.RecordingCommand, recordCommand);
        Assert.True(Topics.TryParseCommand(bookmark.GetProperty("command_topic").GetString()!, out _, out var bookmarkCommand));
        Assert.Equal(HaDiscovery.BookmarkCommand, bookmarkCommand);
    }

    [Theory]
    [InlineData(SpeedUnits.Kmh, "km/h")]
    [InlineData(SpeedUnits.Mph, "mph")]
    public void TheSpeedSensorUsesTheConfiguredUnit(string unit, string expected)
    {
        var speed = HaDiscovery.CameraConfigs(Topics, new HaCamera(CameraId, "Road", null), new HaCameraAbilities(false, false), unit)
            .Single(m => m.Topic.EndsWith("/last_speed/config"));
        Assert.Equal(expected, Json(speed.Payload!).GetProperty("unit_of_measurement").GetString());
    }

    [Fact]
    public void RemovingACameraClearsEveryConfig()
    {
        var messages = HaDiscovery.RemoveCamera(Topics, CameraId).ToList();
        Assert.Equal(HaDiscovery.CameraEntities.Count, messages.Count);
        Assert.All(messages, m => Assert.Null(m.Payload));
    }

    [Fact]
    public void ANodeGetsOnlineAndFreeStorage()
    {
        var messages = HaDiscovery.NodeConfigs(Topics, new HaNode(NodeId, "NVR 1")).ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("larisvms/node/aaaaaaaabbbbccccddddeeeeeeeeeeee/storage_free",
            Json(messages[1].Payload!).GetProperty("state_topic").GetString());
    }

    [Theory]
    [InlineData("Human", null, "person")]
    [InlineData("person", null, "person")]
    [InlineData("Vehicle", null, "vehicle")]
    [InlineData("Animal", null, "animal")]
    [InlineData("Object", null, null)]
    [InlineData(null, DetectionKind.Human, "person")]
    [InlineData(null, DetectionKind.Vehicle, "vehicle")]
    [InlineData(null, DetectionKind.Face, null)]
    public void DetectionsMapToTheirSensor(string? category, DetectionKind? kind, string? expected) =>
        Assert.Equal(expected, HaDiscovery.DetectionItem(category, kind));

    [Theory]
    [InlineData("Front Door", "front_door")]
    [InlineData("  Back -- yard (2) ", "back_yard_2")]
    [InlineData("玄関", "camera")]
    public void EntityIdsAreAsciiSlugs(string name, string expected) => Assert.Equal(expected, HaDiscovery.Slug(name));

    // ── Bridge helpers ──────────────────────────────────────────────────

    [Fact]
    public void TimestampsAreUtcIso()
    {
        Assert.Equal("2026-11-15T08:09:10Z", HomeAssistantBridge.Iso(new DateTime(2026, 11, 15, 8, 9, 10, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void CommandsRunAsTheRoleWithNoUser()
    {
        var principal = HomeAssistantBridge.BuildPrincipal("HA role");
        Assert.True(principal.IsInRole("HA role"));
        Assert.Null(principal.FindFirst(ClaimTypes.NameIdentifier));
        Assert.Equal("Home Assistant", principal.Identity!.Name);
    }

    [Fact]
    public void TheConnectionHasALastWillAndTheLogin()
    {
        var settings = new HomeAssistantSettings
        {
            Id = Guid.NewGuid(), BrokerHost = " broker.lan ", BrokerPort = 8883, UseTls = true, Username = "larisvms", Password = "secret",
        };
        var options = HomeAssistantBridge.BuildOptions(settings, Topics);

        Assert.Equal("larisvms/status", options.WillTopic);
        Assert.True(options.WillRetain);
        Assert.Equal("larisvms", options.Credentials!.GetUserName(options));
        Assert.True(options.ChannelOptions.TlsOptions.UseTls);
    }

    // ── Webhook ─────────────────────────────────────────────────────────

    [Fact]
    public void AWebhookWithoutDetailsKeepsTheOriginalShape()
    {
        var json = Json(WebhookAlertChannelSender.BuildPayload("Rule", "Message", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), null));
        Assert.Equal(["rule", "message", "firedAtUtc"], json.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ADetectionWebhookSaysWhatFiredIt()
    {
        var details = new AlertEventDetails(AlertConditionType.ObjectDetected, CameraId, "Front door", NodeId, "NVR 1",
            "Human", "person", 42, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 31.26);
        var json = Json(WebhookAlertChannelSender.BuildPayload("Rule", "Message", DateTime.UtcNow, details));

        Assert.Equal("Rule", json.GetProperty("rule").GetString());
        Assert.Equal("ObjectDetected", json.GetProperty("event").GetString());
        Assert.Equal(CameraId, json.GetProperty("cameraId").GetGuid());
        Assert.Equal("Front door", json.GetProperty("cameraName").GetString());
        Assert.Equal("person", json.GetProperty("label").GetString());
        Assert.Equal(42, json.GetProperty("spanId").GetInt64());
        Assert.Equal(31.3, json.GetProperty("speedKph").GetDouble());
        Assert.Equal($"/api/v1/cameras/{CameraId}/events/42/snapshot", json.GetProperty("snapshotPath").GetString());
    }

    [Fact]
    public void OnlyADetectionHasASnapshotPath()
    {
        Assert.Null(new AlertEventDetails(AlertConditionType.MotionDetected, CameraId, SpanId: 7).SnapshotPath);
        Assert.Null(new AlertEventDetails(AlertConditionType.CameraNotReporting, CameraId).SnapshotPath);
    }

    // ── API keys over HTTP Basic ────────────────────────────────────────

    private static string Basic(string value) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    [Fact]
    public void BasicWithTheApiKeyUserNameCarriesTheKey()
    {
        Assert.Equal("lvk_abc:def", ApiKeyAuthMiddleware.TryReadBasicKey(Basic("apikey:lvk_abc:def")));
        Assert.Equal("lvk_abc", ApiKeyAuthMiddleware.TryReadBasicKey(Basic("APIKEY:lvk_abc")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer token")]
    [InlineData("Basic !!!not-base64")]
    public void OtherAuthorizationHeadersAreIgnored(string? header) => Assert.Null(ApiKeyAuthMiddleware.TryReadBasicKey(header));

    [Fact]
    public void BasicWithAnotherUserNameOrNoKeyIsIgnored()
    {
        Assert.Null(ApiKeyAuthMiddleware.TryReadBasicKey(Basic("admin:lvk_abc")));
        Assert.Null(ApiKeyAuthMiddleware.TryReadBasicKey(Basic("apikey:")));
        Assert.Null(ApiKeyAuthMiddleware.TryReadBasicKey(Basic("apikey")));
    }

    [Fact]
    public void LiveStreamingIsOfferedToApiKeysButGrantedToNoRoleByDefault()
    {
        Assert.Contains(PermissionCatalog.All, e => e is { Resource: "Live", Action: "Stream" });
        Assert.Contains(("Live", "Stream"), ApiKeyScope.ApiPermissions);
    }

    // ── Live MP4 relay ──────────────────────────────────────────────────

    private static byte[] Init(byte marker) => [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', marker];

    private static byte[] Fragment(byte marker) => [0, 0, 0, 8, (byte)'m', (byte)'o', (byte)'o', (byte)'f', marker];

    [Fact]
    public void InitSegmentsAreRecognised()
    {
        Assert.True(LiveHttpRelay.IsInitSegment(Init(1)));
        Assert.False(LiveHttpRelay.IsInitSegment(Fragment(1)));
        Assert.False(LiveHttpRelay.IsInitSegment([1, 2, 3]));
    }

    [Fact]
    public async Task TheRelayWritesTheInitSegmentThenFragments()
    {
        var node = new FakeWebSocket(Fragment(0), Init(1), Fragment(2), Fragment(3));
        using var output = new MemoryStream();

        var written = await LiveHttpRelay.CopyAsync(node, output, CancellationToken.None);

        Assert.Equal(3, written);
        Assert.Equal(Init(1).Concat(Fragment(2)).Concat(Fragment(3)).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task TheRelayStopsAtASecondInitSegment()
    {
        var node = new FakeWebSocket(Init(1), Fragment(2), Init(3), Fragment(4));
        using var output = new MemoryStream();

        var written = await LiveHttpRelay.CopyAsync(node, output, CancellationToken.None);

        Assert.Equal(2, written);
        Assert.Equal(Init(1).Concat(Fragment(2)).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task TheRelayJoinsAMessageSplitAcrossReads()
    {
        var node = new FakeWebSocket(Init(1)) { MaxChunk = 3 };
        using var output = new MemoryStream();

        await LiveHttpRelay.CopyAsync(node, output, CancellationToken.None);

        Assert.Equal(Init(1), output.ToArray());
    }

    /// <summary>Hands out queued binary messages, then a close.</summary>
    private sealed class FakeWebSocket(params byte[][] messages) : WebSocket
    {
        private readonly Queue<byte[]> _messages = new(messages);
        private byte[]? _current;
        private int _offset;
        private WebSocketState _state = WebSocketState.Open;

        public int MaxChunk { get; init; } = int.MaxValue;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_current is null)
            {
                if (!_messages.TryDequeue(out _current))
                {
                    _state = WebSocketState.CloseReceived;
                    return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                }
                _offset = 0;
            }
            var count = Math.Min(Math.Min(buffer.Count, MaxChunk), _current.Length - _offset);
            Array.Copy(_current, _offset, buffer.Array!, buffer.Offset, count);
            _offset += count;
            var end = _offset == _current.Length;
            if (end) _current = null;
            return Task.FromResult(new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end));
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() { }
    }
}

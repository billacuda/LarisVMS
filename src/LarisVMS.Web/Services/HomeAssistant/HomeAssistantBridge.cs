using System.Globalization;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using LarisVMS.Core;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services.HomeAssistant;

public enum HomeAssistantBridgeState { Off, Connecting, Connected, Error }

/// <summary>What Settings → Home Assistant shows about the connection.</summary>
public sealed record HomeAssistantStatus(HomeAssistantBridgeState State, string? LastError, DateTime? SinceUtc, int Cameras);

/// <summary>
/// Publishes LarisVMS to Home Assistant over MQTT (Settings → Home Assistant): a device per camera
/// with motion, person, vehicle and animal sensors, an online sensor, the latest detection's picture,
/// the last detection, speed and event time, and — when the role allows — a manual-recording switch
/// and a bookmark button; plus a device per recording node with online and free-storage sensors.
/// Entities appear on their own through Home Assistant's MQTT discovery.
///
/// Everything is decided by the chosen role, like an API key: only cameras the role can view are
/// published, and commands from Home Assistant run with the role's permissions and per-camera access,
/// audited as "Home Assistant".
///
/// Sensors turn on the moment NodeService reports a detection or motion (<see cref="IDetectionEventSink"/>),
/// and every few seconds all states are recomputed from the database with the same "still active"
/// window the Live page's motion badge uses, which is what turns them off. Only changes are sent.
/// </summary>
public sealed class HomeAssistantBridge(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory,
    ILogger<HomeAssistantBridge> logger) : BackgroundService, IDetectionEventSink
{
    /// <summary>Same as TimelineService's motion-state window: a span is reported about every 15 s
    /// while it lasts, so one that ended within this is still going.</summary>
    internal static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InventoryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SnapshotDelay = TimeSpan.FromSeconds(3);
    internal const string ActorName = "Home Assistant";

    private readonly Channel<object> _inbox = Channel.CreateBounded<object>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private volatile bool _active;
    private bool _sessionConnected;
    private CancellationTokenSource _reload = new();
    private readonly object _reloadLock = new();

    public HomeAssistantStatus Status { get; private set; } = new(HomeAssistantBridgeState.Off, null, null, 0);

    public void Publish(DetectionEvent detection)
    {
        if (_active) _inbox.Writer.TryWrite(detection);
    }

    public void PublishMotion(MotionEvent motion)
    {
        if (_active) _inbox.Writer.TryWrite(motion);
    }

    /// <summary>Reconnect with the saved settings — called after Settings → Home Assistant is saved.</summary>
    public void Reload()
    {
        lock (_reloadLock) _reload.Cancel();
    }

    private CancellationToken ReloadToken
    {
        get
        {
            lock (_reloadLock)
            {
                if (_reload.IsCancellationRequested)
                {
                    _reload.Dispose();
                    _reload = new CancellationTokenSource();
                }
                return _reload.Token;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var reload = ReloadToken;
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reload);
            HomeAssistantSettings? settings;
            try
            {
                using var scope = scopeFactory.CreateScope();
                settings = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .HomeAssistantSettings.AsNoTracking().FirstOrDefaultAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Home Assistant: couldn't read the settings.");
                settings = null;
            }

            if (settings is not { IsEnabled: true } || string.IsNullOrWhiteSpace(settings.BrokerHost) || string.IsNullOrEmpty(settings.RoleId))
            {
                _active = false;
                Status = new HomeAssistantStatus(HomeAssistantBridgeState.Off, null, null, 0);
                await WaitAsync(TimeSpan.FromMinutes(5), session.Token);
                continue;
            }

            try
            {
                _active = true;
                _sessionConnected = false;
                await RunSessionAsync(settings, session.Token);
                failures = 0;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                failures = 0;
            }
            catch (Exception ex)
            {
                // It had connected, so the broker works: the next retry starts from a short wait again.
                failures = _sessionConnected ? 1 : failures + 1;
                Status = new HomeAssistantStatus(HomeAssistantBridgeState.Error, ex.Message, DateTime.UtcNow, 0);
                logger.LogWarning(ex, "Home Assistant: the MQTT connection to {Host}:{Port} failed; retrying.", settings.BrokerHost, settings.BrokerPort);
            }
            if (stoppingToken.IsCancellationRequested) break;
            if (!reload.IsCancellationRequested)
                await WaitAsync(TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(failures, 4)))), session.Token);
        }
        _active = false;
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* reload or shutdown */ }
    }

    /// <summary>The MQTT connection options. TLS optional; the last will marks LarisVMS offline in
    /// Home Assistant if the server goes away without saying so.</summary>
    internal static MqttClientOptions BuildOptions(HomeAssistantSettings settings, HaTopics topics)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.BrokerHost!.Trim(), settings.BrokerPort is > 0 and < 65536 ? settings.BrokerPort : HomeAssistantSettings.DefaultPort)
            .WithClientId($"larisvms-{settings.Id:N}")
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(15))
            .WithWillTopic(topics.Availability)
            .WithWillPayload(HaDiscovery.Offline)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrWhiteSpace(settings.Username))
            builder = builder.WithCredentials(settings.Username.Trim(), settings.Password ?? "");
        if (settings.UseTls)
        {
            builder = builder.WithTlsOptions(tls =>
            {
                tls.UseTls(true);
                if (settings.AllowUntrustedCertificate) tls.WithCertificateValidationHandler(_ => true);
            });
        }
        return builder.Build();
    }

    /// <summary>Connects once and tries the login, for the settings page's Test button.</summary>
    public static async Task<string?> TestAsync(HomeAssistantSettings settings, CancellationToken ct)
    {
        try
        {
            using var client = new MqttClientFactory().CreateMqttClient();
            var options = BuildOptions(settings, new HaTopics(settings.TopicPrefix, settings.DiscoveryPrefix));
            options.WillTopic = null;
            options.ClientId = $"larisvms-test-{Guid.NewGuid():N}";
            var result = await client.ConnectAsync(options, ct);
            if (result.ResultCode != MqttClientConnectResultCode.Success) return result.ResultCode.ToString();
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex.InnerException?.Message ?? ex.Message;
        }
    }

    private sealed record Command(string Topic, string Payload);
    private sealed record Tick(DateTime AtUtc);
    private sealed record PublishLater(IReadOnlyList<HaMessage> Messages, byte[]? Image, string? ImageTopic);

    /// <summary>One connection: publish everything, then follow events, commands and the state timer
    /// until the broker drops the connection or the settings change.</summary>
    private async Task RunSessionAsync(HomeAssistantSettings settings, CancellationToken ct)
    {
        var topics = new HaTopics(settings.TopicPrefix, settings.DiscoveryPrefix);
        string roleName;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            roleName = await db.Roles.AsNoTracking().Where(r => r.Id == settings.RoleId).Select(r => r.Name).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException(Loc.T("settings.homeAssistant.roleMissing"));
        }
        var principal = BuildPrincipal(roleName);

        Status = new HomeAssistantStatus(HomeAssistantBridgeState.Connecting, null, DateTime.UtcNow, 0);
        using var client = new MqttClientFactory().CreateMqttClient();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
        client.ApplicationMessageReceivedAsync += e =>
        {
            _inbox.Writer.TryWrite(new Command(e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? ""));
            return Task.CompletedTask;
        };

        var connect = await client.ConnectAsync(BuildOptions(settings, topics), ct);
        if (connect.ResultCode != MqttClientConnectResultCode.Success)
            throw new InvalidOperationException(connect.ResultCode.ToString());
        logger.LogInformation("Home Assistant: connected to MQTT broker {Host}:{Port} as role {Role}.", settings.BrokerHost, settings.BrokerPort, roleName);

        var state = new SessionState(topics, roleName, principal);
        try
        {
            await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topics.HomeAssistantStatus))
                .WithTopicFilter(f => f.WithTopic(topics.CommandFilter))
                .Build(), ct);
            await SendAsync(client, new HaMessage(topics.Availability, HaDiscovery.Online), ct);
            await RefreshInventoryAsync(client, state, ct);
            await RefreshStatesAsync(client, state, ct);
            Status = new HomeAssistantStatus(HomeAssistantBridgeState.Connected, null, DateTime.UtcNow, state.Cameras.Count);
            _sessionConnected = true;

            using var ticker = new PeriodicTimer(StateInterval);
            _ = TickAsync(ticker, ct);
            var lastInventory = DateTime.UtcNow;
            // Cancelled when this session ends. A read left waiting would take the next session's first
            // message (a command, perhaps) and drop it.
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var stopReading = new CancelOnDispose(reading);
            var readTask = _inbox.Reader.ReadAsync(reading.Token).AsTask();
            while (true)
            {
                var done = await Task.WhenAny(readTask, disconnected.Task);
                if (done == disconnected.Task) throw new InvalidOperationException(Loc.T("settings.homeAssistant.connectionLost"));
                var item = await readTask;
                readTask = _inbox.Reader.ReadAsync(reading.Token).AsTask();
                try
                {
                    switch (item)
                    {
                        case Tick:
                            if (DateTime.UtcNow - lastInventory >= InventoryInterval)
                            {
                                await RefreshInventoryAsync(client, state, ct);
                                lastInventory = DateTime.UtcNow;
                            }
                            await RefreshStatesAsync(client, state, ct);
                            Status = Status with { Cameras = state.Cameras.Count };
                            break;
                        case DetectionEvent detection:
                            await OnDetectionAsync(client, state, detection, ct);
                            break;
                        case MotionEvent motion:
                            await OnMotionAsync(client, state, motion, ct);
                            break;
                        case Command command:
                            await OnCommandAsync(client, state, command, ct);
                            break;
                        case PublishLater later:
                            foreach (var message in later.Messages) await SendAsync(client, state, message, ct);
                            if (later.Image is not null && later.ImageTopic is not null)
                                await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(later.ImageTopic)
                                    .WithPayload(later.Image).WithRetainFlag(true)
                                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build(), ct);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && client.IsConnected)
                {
                    logger.LogWarning(ex, "Home Assistant: handling {Item} failed.", item.GetType().Name);
                }
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await SendAsync(client, new HaMessage(topics.Availability, HaDiscovery.Offline), CancellationToken.None);
                    await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
                }
                catch (Exception ex) { logger.LogDebug(ex, "Home Assistant: disconnecting failed."); }
            }
            Status = Status.State == HomeAssistantBridgeState.Connected
                ? new HomeAssistantStatus(HomeAssistantBridgeState.Off, null, null, 0)
                : Status;
        }
    }

    private sealed class CancelOnDispose(CancellationTokenSource source) : IDisposable
    {
        public void Dispose() => source.Cancel();
    }

    private async Task TickAsync(PeriodicTimer ticker, CancellationToken ct)
    {
        try
        {
            while (await ticker.WaitForNextTickAsync(ct)) _inbox.Writer.TryWrite(new Tick(DateTime.UtcNow));
        }
        catch (OperationCanceledException) { /* session ended */ }
    }

    /// <summary>The identity commands run as: the role, and no user (like an API key).</summary>
    internal static ClaimsPrincipal BuildPrincipal(string roleName) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, ActorName),
            new Claim(ClaimTypes.Role, roleName),
        ], authenticationType: "HomeAssistant"));

    /// <summary>What one connection knows: what's published, and the last payload sent per topic so
    /// only changes go out.</summary>
    private sealed class SessionState(HaTopics topics, string roleName, ClaimsPrincipal principal)
    {
        public HaTopics Topics { get; } = topics;
        public string RoleName { get; } = roleName;
        public ClaimsPrincipal Principal { get; } = principal;
        public Dictionary<Guid, HaCamera> Cameras { get; } = [];
        public Dictionary<Guid, HaCameraAbilities> Abilities { get; } = [];
        public Dictionary<Guid, HaNode> Nodes { get; } = [];
        public Dictionary<string, string?> Sent { get; } = [];
        public Dictionary<long, DateTime> SnapshotsTaken { get; } = [];
        public string SpeedUnit { get; set; } = SpeedUnits.Default;
    }

    private static Task SendAsync(IMqttClient client, HaMessage message, CancellationToken ct) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload ?? "")
            .WithRetainFlag(message.Retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

    /// <summary>Publishes only when the topic's payload changed since it was last sent.</summary>
    private static async Task SendAsync(IMqttClient client, SessionState state, HaMessage message, CancellationToken ct)
    {
        if (state.Sent.TryGetValue(message.Topic, out var previous) && previous == message.Payload) return;
        await SendAsync(client, message, ct);
        state.Sent[message.Topic] = message.Payload;
    }

    /// <summary>Which cameras and nodes are published, and what Home Assistant may do on each — from
    /// the role, fresh every minute, so a change to its access shows up without a reconnect.</summary>
    private async Task RefreshInventoryAsync(IMqttClient client, SessionState state, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cameraAccess = scope.ServiceProvider.GetRequiredService<ICameraAccessService>();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        state.SpeedUnit = await scope.ServiceProvider.GetRequiredService<ISettingsResolver>()
            .GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct);

        var viewable = await cameraAccess.GetAccessibleCameraIdsAsync(state.Principal, CameraAccessActions.View, ct);
        var cameras = await db.Cameras.AsNoTracking()
            .Where(c => c.IsEnabled && (viewable == null || viewable.Contains(c.Id)))
            .Select(c => new HaCamera(c.Id, c.Name, c.NodeId))
            .ToListAsync(ct);

        var canRecord = await permissions.HasPermissionForRoleNameAsync(state.RoleName, "Recording", "Manual", ct);
        var canBookmark = await permissions.HasPermissionForRoleNameAsync(state.RoleName, "Playback", "View", ct)
            && await permissions.HasPermissionForRoleNameAsync(state.RoleName, "Bookmarks", "Edit", ct);
        var recordable = canRecord ? await cameraAccess.GetAccessibleCameraIdsAsync(state.Principal, CameraAccessActions.Record, ct) : [];
        var playable = canBookmark ? await cameraAccess.GetAccessibleCameraIdsAsync(state.Principal, CameraAccessActions.Playback, ct) : [];

        var nodeIds = cameras.Where(c => c.NodeId is not null).Select(c => c.NodeId!.Value).Distinct().ToList();
        var nodes = await db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
            .Select(n => new HaNode(n.Id, n.Name)).ToListAsync(ct);

        foreach (var gone in state.Cameras.Keys.Except(cameras.Select(c => c.Id)).ToList())
        {
            foreach (var message in HaDiscovery.RemoveCamera(state.Topics, gone)) await SendAsync(client, state, message, ct);
            state.Cameras.Remove(gone);
            state.Abilities.Remove(gone);
        }
        foreach (var gone in state.Nodes.Keys.Except(nodes.Select(n => n.Id)).ToList())
        {
            foreach (var message in HaDiscovery.RemoveNode(state.Topics, gone)) await SendAsync(client, state, message, ct);
            state.Nodes.Remove(gone);
        }

        // Entity names in the server's default language: Home Assistant shows them as given.
        var configs = new List<HaMessage>();
        using (Loc.InLanguage(null))
        {
            foreach (var node in nodes)
            {
                state.Nodes[node.Id] = node;
                configs.AddRange(HaDiscovery.NodeConfigs(state.Topics, node));
            }
            foreach (var camera in cameras)
            {
                var abilities = new HaCameraAbilities(
                    canRecord && (recordable is null || recordable.Contains(camera.Id)),
                    canBookmark && (playable is null || playable.Contains(camera.Id)));
                state.Cameras[camera.Id] = camera;
                state.Abilities[camera.Id] = abilities;
                configs.AddRange(HaDiscovery.CameraConfigs(state.Topics, camera, abilities, state.SpeedUnit));
            }
        }
        foreach (var message in configs) await SendAsync(client, state, message, ct);
    }

    /// <summary>Every published sensor's state, recomputed from the database.</summary>
    private async Task RefreshStatesAsync(IMqttClient client, SessionState state, CancellationToken ct)
    {
        if (state.Cameras.Count == 0 && state.Nodes.Count == 0) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var ids = state.Cameras.Keys.ToList();

        var threshold = now - ActiveWindow;
        var spans = await db.MotionSpans.AsNoTracking()
            .Where(m => ids.Contains(m.CameraId) && m.EndUtc >= threshold)
            .Select(m => new { m.CameraId, m.DetectionKind, Category = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.Name : null })
            .ToListAsync(ct);
        var health = await db.Cameras.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, Main = c.Streams.Where(s => s.Role == CameraStreamRole.Main).Select(s => s.HealthReportedAt).FirstOrDefault() })
            .ToDictionaryAsync(c => c.Id, c => c.Main, ct);
        var recording = (await scope.ServiceProvider.GetRequiredService<IManualRecordingService>().GetStatesAsync(ct))
            .Where(s => s.Manual).Select(s => s.CameraId).ToHashSet();

        foreach (var cameraId in state.Cameras.Keys)
        {
            var mine = spans.Where(s => s.CameraId == cameraId).ToList();
            var active = mine.Select(s => HaDiscovery.DetectionItem(s.Category, s.DetectionKind)).Where(i => i is not null).ToHashSet();
            await SendAsync(client, state, new HaMessage(state.Topics.Camera(cameraId, "motion"), mine.Count > 0 ? HaDiscovery.On : HaDiscovery.Off), ct);
            foreach (var item in HaDiscovery.DetectionItems)
                await SendAsync(client, state, new HaMessage(state.Topics.Camera(cameraId, item), active.Contains(item) ? HaDiscovery.On : HaDiscovery.Off), ct);
            var online = !AlertEvaluationPolicy.IsCameraNotReporting(health.GetValueOrDefault(cameraId), now);
            await SendAsync(client, state, new HaMessage(state.Topics.Camera(cameraId, "online"), online ? HaDiscovery.On : HaDiscovery.Off), ct);
            if (state.Abilities.GetValueOrDefault(cameraId)?.CanRecord == true)
                await SendAsync(client, state, new HaMessage(state.Topics.Camera(cameraId, "recording"), recording.Contains(cameraId) ? HaDiscovery.On : HaDiscovery.Off), ct);
        }

        if (state.Nodes.Count > 0)
        {
            var nodeIds = state.Nodes.Keys.ToList();
            var nodes = await db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
                .Select(n => new { n.Id, n.LastSeenAt, n.StorageFreeBytes, n.StorageTotalBytes }).ToListAsync(ct);
            foreach (var node in nodes)
            {
                await SendAsync(client, state, new HaMessage(state.Topics.Node(node.Id, "online"),
                    AlertEvaluationPolicy.IsNodeOffline(node.LastSeenAt, now) ? HaDiscovery.Off : HaDiscovery.On), ct);
                if (node.StorageFreeBytes is { } free && node.StorageTotalBytes is > 0 and var total)
                    await SendAsync(client, state, new HaMessage(state.Topics.Node(node.Id, "storage_free"),
                        Math.Round(free * 100.0 / total, 1).ToString(CultureInfo.InvariantCulture)), ct);
            }
        }

        // Spans' pictures are only fetched once; forget them after a while.
        foreach (var (span, at) in state.SnapshotsTaken.ToList())
            if (now - at > TimeSpan.FromHours(1)) state.SnapshotsTaken.Remove(span);
    }

    private async Task OnDetectionAsync(IMqttClient client, SessionState state, DetectionEvent e, CancellationToken ct)
    {
        if (!state.Cameras.ContainsKey(e.CameraId)) return;
        var topics = state.Topics;
        await SendAsync(client, state, new HaMessage(topics.Camera(e.CameraId, "motion"), HaDiscovery.On), ct);
        if (HaDiscovery.DetectionItem(e.Category) is { } item)
            await SendAsync(client, state, new HaMessage(topics.Camera(e.CameraId, item), HaDiscovery.On), ct);
        await SendAsync(client, state, new HaMessage(topics.Camera(e.CameraId, "last_detection"),
            string.IsNullOrWhiteSpace(e.Label) ? e.Category : e.Label), ct);
        await SendAsync(client, state, new HaMessage(topics.Camera(e.CameraId, "last_event"), Iso(e.StartUtc)), ct);
        if (e.Objects.Where(o => o.SpeedKph is not null).Select(o => o.SpeedKph!.Value).DefaultIfEmpty().Max() is > 0 and var kph)
            await SendAsync(client, state, new HaMessage(topics.Camera(e.CameraId, "last_speed"),
                Math.Round(SpeedUnits.FromKph(kph, state.SpeedUnit)).ToString(CultureInfo.InvariantCulture)), ct);

        if (state.SnapshotsTaken.TryAdd(e.SpanId, DateTime.UtcNow))
            _ = FetchSnapshotLaterAsync(e.CameraId, e.SpanId, topics.Camera(e.CameraId, "snapshot"), ct);
    }

    private static async Task OnMotionAsync(IMqttClient client, SessionState state, MotionEvent m, CancellationToken ct)
    {
        if (!state.Cameras.ContainsKey(m.CameraId)) return;
        await SendAsync(client, state, new HaMessage(state.Topics.Camera(m.CameraId, "motion"), HaDiscovery.On), ct);
        await SendAsync(client, state, new HaMessage(state.Topics.Camera(m.CameraId, "last_event"), Iso(m.StartUtc)), ct);
    }

    internal static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>The detection's cropped picture, a moment after it's first reported (the node crops it
    /// as the span opens). Published through the inbox so only the session loop touches the client's
    /// state. A picture that can't be had is skipped; Home Assistant keeps the previous one.</summary>
    private async Task FetchSnapshotLaterAsync(Guid cameraId, long spanId, string topic, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SnapshotDelay, ct);
            using var scope = scopeFactory.CreateScope();
            var info = await scope.ServiceProvider.GetRequiredService<ITimelineService>().GetSnapshotImageInfoAsync(cameraId, spanId, ct);
            if (info is null || !SnapshotImageRequest.CanRequest(info)) return;
            var http = httpFactory.CreateClient(NodeTlsTargets.HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.SendAsync(SnapshotImageRequest.Create(cameraId, info, info.OffsetSeconds, info.OffsetMs, info.BestFrameTicksUtc), ct);
            if (!response.IsSuccessStatusCode) return;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 0) _inbox.Writer.TryWrite(new PublishLater([], bytes, topic));
        }
        catch (OperationCanceledException) { /* session ended */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Home Assistant: couldn't fetch the picture for span {SpanId}.", spanId);
        }
    }

    /// <summary>A command from Home Assistant, checked against the role's permissions and per-camera
    /// access each time (not just when the entity was offered).</summary>
    private async Task OnCommandAsync(IMqttClient client, SessionState state, Command command, CancellationToken ct)
    {
        if (command.Topic == state.Topics.HomeAssistantStatus)
        {
            // Home Assistant restarted: it needs discovery and every state again.
            if (string.Equals(command.Payload.Trim(), HaDiscovery.Online, StringComparison.OrdinalIgnoreCase))
            {
                state.Sent.Clear();
                await SendAsync(client, new HaMessage(state.Topics.Availability, HaDiscovery.Online), ct);
                await RefreshInventoryAsync(client, state, ct);
                await RefreshStatesAsync(client, state, ct);
            }
            return;
        }

        if (!state.Topics.TryParseCommand(command.Topic, out var cameraId, out var what) || !state.Cameras.TryGetValue(cameraId, out var camera)) return;
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var permissions = services.GetRequiredService<IPermissionService>();
        var cameraAccess = services.GetRequiredService<ICameraAccessService>();
        var audit = services.GetRequiredService<IAuditService>();

        if (what == HaDiscovery.RecordingCommand)
        {
            var start = string.Equals(command.Payload.Trim(), HaDiscovery.On, StringComparison.OrdinalIgnoreCase);
            if (!start && !string.Equals(command.Payload.Trim(), HaDiscovery.Off, StringComparison.OrdinalIgnoreCase)) return;
            if (!await permissions.HasPermissionForRoleNameAsync(state.RoleName, "Recording", "Manual", ct))
            {
                await audit.LogAsync("HomeAssistant.CommandRefused", null, ActorName, null, $"{camera.Name}: recording", ct);
                return;
            }
            var result = await ManualRecordingApi.ChangeAsync(state.Principal, null, [cameraId], start,
                new ManualRecordingApi.Dependencies(services.GetRequiredService<IManualRecordingService>(), cameraAccess, audit,
                    services.GetRequiredService<ApplicationDbContext>(), services.GetRequiredService<NodeControlClient>()), ct);
            if (result is null)
                await audit.LogAsync("HomeAssistant.CommandRefused", null, ActorName, null, $"{camera.Name}: recording", ct);
            state.Sent.Remove(state.Topics.Camera(cameraId, "recording"));
            await RefreshStatesAsync(client, state, ct);
        }
        else if (what == HaDiscovery.BookmarkCommand)
        {
            var playable = await cameraAccess.GetAccessibleCameraIdsAsync(state.Principal, CameraAccessActions.Playback, ct);
            if (!await permissions.HasPermissionForRoleNameAsync(state.RoleName, "Playback", "View", ct)
                || !await permissions.HasPermissionForRoleNameAsync(state.RoleName, "Bookmarks", "Edit", ct)
                || (playable is not null && !playable.Contains(cameraId)))
            {
                await audit.LogAsync("HomeAssistant.CommandRefused", null, ActorName, null, $"{camera.Name}: bookmark", ct);
                return;
            }
            string note;
            using (Loc.InLanguage(null)) note = Loc.T("homeAssistant.bookmarkNote");
            await services.GetRequiredService<IBookmarkService>().CreateAsync(cameraId, DateTime.UtcNow, note, "homeassistant", ActorName, ct);
            await audit.LogAsync("HomeAssistant.Bookmark", null, ActorName, null, camera.Name, ct);
        }
    }
}

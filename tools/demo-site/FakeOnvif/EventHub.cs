using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FakeOnvif;

/// <summary>
/// Fake motion for each camera, served over ONVIF PullPoint subscriptions. Each camera alternates
/// between idle (45–240 s) and motion (8–40 s), so the LarisVMS timeline gets a believable scatter of
/// motion events. LarisVMS classifies "tns1:VideoSource/MotionAlarm" with a State item as motion
/// (LarisVMS.Core.CameraEventClassifier).
/// </summary>
public sealed class EventHub(DemoConfig config, ILogger<EventHub> logger) : BackgroundService
{
    private const string MotionTopic = "tns1:VideoSource/MotionAlarm";
    private static readonly TimeSpan MaxPullWait = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, bool> _motion = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    private sealed record Notice(DateTime UtcTime, bool State, bool Initial);

    private sealed class Subscription(string cameraId, DateTime expiresUtc)
    {
        public string CameraId { get; } = cameraId;
        public DateTime ExpiresUtc { get; set; } = expiresUtc;
        public Channel<Notice> Queue { get; } = Channel.CreateBounded<Notice>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(config.Cameras.Select(c => RunCameraAsync(c, ct)).Append(SweepAsync(ct)));

    private async Task RunCameraAsync(CameraConfig camera, CancellationToken ct)
    {
        var random = new Random(camera.Id.GetHashCode());
        _motion[camera.Id] = false;
        try
        {
            // Stagger the cameras so their first events don't line up.
            await Task.Delay(TimeSpan.FromSeconds(random.Next(10, 90)), ct);
            while (!ct.IsCancellationRequested)
            {
                Publish(camera.Id, true);
                await Task.Delay(TimeSpan.FromSeconds(random.Next(8, 40)), ct);
                Publish(camera.Id, false);
                await Task.Delay(TimeSpan.FromSeconds(random.Next(45, 240)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Publish(string cameraId, bool state)
    {
        _motion[cameraId] = state;
        var notice = new Notice(DateTime.UtcNow, state, Initial: false);
        foreach (var sub in _subscriptions.Values.Where(s => s.CameraId == cameraId))
            sub.Queue.Writer.TryWrite(notice);
        logger.LogInformation("{Camera} motion {State}", cameraId, state ? "started" : "stopped");
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                foreach (var (id, sub) in _subscriptions)
                    if (sub.ExpiresUtc < DateTime.UtcNow) _subscriptions.TryRemove(id, out _);
            }
        }
        catch (OperationCanceledException) { }
    }

    public string CreatePullPoint(CameraConfig camera, TimeSpan? initialTermination)
    {
        var id = Guid.NewGuid().ToString("N");
        var lifetime = initialTermination is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromMinutes(5);
        var sub = new Subscription(camera.Id, DateTime.UtcNow + lifetime);
        // ONVIF sends the current state of each property as an "Initialized" message on a new subscription.
        sub.Queue.Writer.TryWrite(new Notice(DateTime.UtcNow, _motion.GetValueOrDefault(camera.Id), Initial: true));
        _subscriptions[id] = sub;

        var now = DateTime.UtcNow;
        return $"""
            <tev:CreatePullPointSubscriptionResponse>
              <tev:SubscriptionReference>
                <wsa:Address>{camera.EventServiceUrl}/sub/{id}</wsa:Address>
              </tev:SubscriptionReference>
              <wsnt:CurrentTime>{Soap.Time(now)}</wsnt:CurrentTime>
              <wsnt:TerminationTime>{Soap.Time(sub.ExpiresUtc)}</wsnt:TerminationTime>
            </tev:CreatePullPointSubscriptionResponse>
            """;
    }

    /// <summary>Null when the subscription doesn't exist (expired or never created), which the
    /// caller turns into a fault so LarisVMS resubscribes.</summary>
    public async Task<string?> PullAsync(string subscriptionId, TimeSpan timeout, int limit, CancellationToken ct)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var sub)) return null;
        sub.ExpiresUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5); // pulling keeps it alive

        var messages = new List<Notice>();
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(timeout > MaxPullWait || timeout <= TimeSpan.Zero ? MaxPullWait : timeout);
        try
        {
            // Long poll: wait for the first message, then take whatever else is already queued.
            if (await sub.Queue.Reader.WaitToReadAsync(waitCts.Token))
                while (messages.Count < Math.Max(1, limit) && sub.Queue.Reader.TryRead(out var n)) messages.Add(n);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        var now = DateTime.UtcNow;
        var body = string.Concat(messages.Select(m => $"""
              <wsnt:NotificationMessage>
                <wsnt:Topic Dialect="http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet">{MotionTopic}</wsnt:Topic>
                <wsnt:Message>
                  <tt:Message UtcTime="{Soap.Time(m.UtcTime)}" PropertyOperation="{(m.Initial ? "Initialized" : "Changed")}">
                    <tt:Source><tt:SimpleItem Name="Source" Value="VideoSource_1"/></tt:Source>
                    <tt:Data><tt:SimpleItem Name="State" Value="{(m.State ? "true" : "false")}"/></tt:Data>
                  </tt:Message>
                </wsnt:Message>
              </wsnt:NotificationMessage>
            """));
        return $"""
            <tev:PullMessagesResponse>
              <tev:CurrentTime>{Soap.Time(now)}</tev:CurrentTime>
              <tev:TerminationTime>{Soap.Time(sub.ExpiresUtc)}</tev:TerminationTime>
            {body}
            </tev:PullMessagesResponse>
            """;
    }

    public string? Renew(string subscriptionId)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var sub)) return null;
        sub.ExpiresUtc = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        return $"""
            <wsnt:RenewResponse>
              <wsnt:TerminationTime>{Soap.Time(sub.ExpiresUtc)}</wsnt:TerminationTime>
              <wsnt:CurrentTime>{Soap.Time(DateTime.UtcNow)}</wsnt:CurrentTime>
            </wsnt:RenewResponse>
            """;
    }

    public string Unsubscribe(string subscriptionId)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        return "<wsnt:UnsubscribeResponse/>";
    }

    public static string EventProperties() => """
        <tev:GetEventPropertiesResponse>
          <tev:TopicNamespaceLocation>http://www.onvif.org/onvif/ver10/topics/topicns.xml</tev:TopicNamespaceLocation>
          <wsnt:FixedTopicSet>true</wsnt:FixedTopicSet>
          <wstop:TopicSet xmlns:wstop="http://docs.oasis-open.org/wsn/t-1">
            <tns1:VideoSource><MotionAlarm wstop:topic="true"/></tns1:VideoSource>
          </wstop:TopicSet>
        </tev:GetEventPropertiesResponse>
        """;
}

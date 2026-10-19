using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Alerts;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Notifications;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>Personal notifications: the Web Push and Firebase protocol pieces, who a firing reaches
/// (type switched off, muted, no camera access, email-only), the per-camera scope and time zone of
/// detection rules, and the once-per-event evidence-lock notifications.</summary>
public class NotificationTests
{
    // ── Web Push protocol ─────────────────────────────────────────────────

    [Fact]
    public void VapidKeysHaveTheShapesBrowsersExpect()
    {
        var keys = WebPushProtocol.GenerateVapidKeys();
        var publicKey = WebPushProtocol.FromBase64Url(keys.PublicKey);
        Assert.Equal(65, publicKey.Length);
        Assert.Equal(0x04, publicKey[0]);
        Assert.Equal(32, WebPushProtocol.FromBase64Url(keys.PrivateKey).Length);
        Assert.DoesNotContain('=', keys.PublicKey);
    }

    [Fact]
    public void VapidJwtIsSignedByTheServerKeyForThePushServiceOrigin()
    {
        var keys = WebPushProtocol.GenerateVapidKeys();
        var header = WebPushProtocol.VapidAuthorization(new Uri("https://fcm.googleapis.com/fcm/send/abc"), "mailto:ops@example.com", keys, DateTime.UtcNow);

        Assert.StartsWith("vapid t=", header);
        Assert.EndsWith(", k=" + keys.PublicKey, header);
        var jwt = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = jwt.Split('.');
        using var claims = JsonDocument.Parse(WebPushProtocol.FromBase64Url(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@example.com", claims.RootElement.GetProperty("sub").GetString());

        var q = WebPushProtocol.FromBase64Url(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[1..33], Y = q[33..] } });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), WebPushProtocol.FromBase64Url(parts[2]), HashAlgorithmName.SHA256));
    }

    [Fact]
    public void EncryptedPayloadDecryptsOnTheBrowserSide()
    {
        using var browserKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = browserKey.ExportParameters(false);
        var browserPublic = new byte[] { 0x04 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        var authSecret = RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes("""{"title":"Animal detected","body":"Back yard"}""");

        var body = WebPushProtocol.Encrypt(payload, WebPushProtocol.Base64Url(browserPublic), WebPushProtocol.Base64Url(authSecret));

        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        Assert.Equal(65, body[20]);
        Assert.Equal(payload, WebPushProtocol.Decrypt(body, browserKey, authSecret));
    }

    [Fact]
    public void FirebaseAssertionIsSignedByTheServiceAccount()
    {
        using var rsa = RSA.Create(2048);
        var account = new FcmClient.ServiceAccount("my-project", "sa@my-project.iam.gserviceaccount.com", rsa.ExportPkcs8PrivateKeyPem(), null);
        var jwt = FcmClient.BuildAssertion(account, "https://oauth2.googleapis.com/token", DateTime.UtcNow).Split('.');

        using var claims = JsonDocument.Parse(WebPushProtocol.FromBase64Url(jwt[1]));
        Assert.Equal(account.ClientEmail, claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.RootElement.GetProperty("scope").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), WebPushProtocol.FromBase64Url(jwt[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void AServiceAccountWithoutAKeyIsRejectedWithAReadableMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => FcmClient.ParseServiceAccount("""{"project_id":"p","client_email":"e"}"""));
        Assert.Contains("private_key", ex.Message);
        Assert.Throws<ArgumentException>(() => FcmClient.ParseServiceAccount("not json"));
    }

    // ── On-premises hub ───────────────────────────────────────────────────

    [Fact]
    public void HubReachesEveryOpenTabOfTheUserOnly()
    {
        var hub = new NotificationHub();
        using var tab1 = hub.Subscribe("alice");
        var tab2 = hub.Subscribe("alice");
        using var bobTab = hub.Subscribe("bob");

        Assert.Equal(2, hub.Publish("alice", "{}"));
        Assert.True(tab1.Reader.TryRead(out _));
        Assert.False(bobTab.Reader.TryRead(out _));

        tab2.Dispose();
        Assert.Equal(1, hub.Publish("alice", "{}"));
        Assert.Equal(0, hub.Publish("carol", "{}"));
    }

    [Fact]
    public void PastTheLimitTheOldestStreamCloses()
    {
        var hub = new NotificationHub();
        var tabs = Enumerable.Range(0, NotificationHub.MaxStreamsPerUser + 1).Select(_ => hub.Subscribe("alice")).ToList();

        Assert.True(tabs[0].Reader.Completion.IsCompleted);
        Assert.False(tabs[^1].Reader.Completion.IsCompleted);
        Assert.Equal(NotificationHub.MaxStreamsPerUser, hub.Publish("alice", "{}"));
        tabs.ForEach(t => t.Dispose());
    }

    // ── Rule scope and schedule ───────────────────────────────────────────

    [Fact]
    public void ANodeScopedRuleMatchesOnlyThatNodesCameras()
    {
        var onNode = Guid.NewGuid();
        var rule = new DetectionRule(Guid.NewGuid(), "r", AlertConditionType.ObjectDetected, null, null, null, null, false, null, null, null, null, null,
            ScopeCameraIds: new HashSet<Guid> { onNode });

        Assert.True(DetectionAlertMatcher.MatchesEvent(rule, Event(onNode)));
        Assert.False(DetectionAlertMatcher.MatchesEvent(rule, Event(Guid.NewGuid())));
        Assert.True(DetectionAlertMatcher.MatchesEvent(rule with { ScopeCameraIds = null }, Event(Guid.NewGuid())));
    }

    [Fact]
    public void AMotionRuleMatchesTheWholeCameraOrOnlyItsZone()
    {
        var camera = Guid.NewGuid();
        var zone = Guid.NewGuid();
        var rule = new DetectionRule(Guid.NewGuid(), "r", AlertConditionType.MotionDetected, camera, null, null, null, false, null, null, null, null, null);
        MotionEvent Motion(Guid cam, Guid? z) => new(1, cam, z, DateTime.UtcNow, DateTime.UtcNow);

        Assert.True(DetectionAlertMatcher.MatchesMotion(rule, Motion(camera, null)));
        Assert.True(DetectionAlertMatcher.MatchesMotion(rule, Motion(camera, zone)));
        Assert.False(DetectionAlertMatcher.MatchesMotion(rule, Motion(Guid.NewGuid(), zone)));

        var zoned = rule with { MotionZoneId = zone };
        Assert.True(DetectionAlertMatcher.MatchesMotion(zoned, Motion(camera, zone)));
        Assert.False(DetectionAlertMatcher.MatchesMotion(zoned, Motion(camera, Guid.NewGuid())));
        Assert.False(DetectionAlertMatcher.MatchesMotion(zoned, Motion(camera, null)));

        // Only motion rules match motion.
        Assert.False(DetectionAlertMatcher.MatchesMotion(rule with { Condition = AlertConditionType.ObjectDetected }, Motion(camera, null)));
    }

    [Fact]
    public async Task AMotionRuleKeepsItsZoneAndScheduleAndNeedsAServerMotionZone()
    {
        using var db = NewDb();
        var settings = new SettingsResolver(db);
        var camera = Guid.NewGuid();
        var motionZone = new Zone { Id = Guid.NewGuid(), CameraId = camera, Name = "Driveway", Kind = ZoneKind.ServerMotion, PolygonJson = "{}" };
        var ignoreZone = new Zone { Id = Guid.NewGuid(), CameraId = camera, Name = "Tree", Kind = ZoneKind.Ignore, PolygonJson = "{}" };
        db.Zones.AddRange(motionZone, ignoreZone);
        await db.SaveChangesAsync();

        var input = Input(AlertConditionType.MotionDetected, camera, null) with
        {
            ZoneId = motionZone.Id, ScheduleStart = new TimeOnly(22, 0), ScheduleEnd = new TimeOnly(6, 0)
        };
        Assert.Null(await AlertRuleEditing.ValidateAsync(db, input, default));
        Assert.NotNull(await AlertRuleEditing.ValidateAsync(db, input with { ZoneId = ignoreZone.Id }, default));

        var rule = new AlertRule();
        await AlertRuleEditing.ApplyAsync(rule, input, settings, "u", default);
        Assert.Equal(motionZone.Id, rule.ZoneId);
        Assert.Equal(new TimeOnly(22, 0), rule.ScheduleStart);
        Assert.Null(rule.CategoryFilter);
    }

    private static DetectionEvent Event(Guid cameraId) => new(
        SpanId: 1, CameraId: cameraId, Category: "Animal", Label: "dog", StartUtc: DateTime.UtcNow, EndUtc: DateTime.UtcNow,
        BoxX: 0.4, BoxY: 0.4, BoxW: 0.1, BoxH: 0.1, Confidence: 0.9, Objects: []);

    [Fact]
    public void ScheduleIsReadInTheRulesOwnTimeZone()
    {
        var tokyo = DetectionAlertMatcher.ResolveTimeZone("Asia/Tokyo") ?? DetectionAlertMatcher.ResolveTimeZone("Tokyo Standard Time");
        Assert.NotNull(tokyo);
        // 13:00 UTC is 22:00 in Tokyo — inside a 22:00–06:00 night window there, not in UTC.
        var rule = new DetectionRule(Guid.NewGuid(), "r", AlertConditionType.ObjectDetected, null, null, null, null, false, null, null,
            null, new TimeOnly(22, 0), new TimeOnly(6, 0), TimeZone: tokyo);
        var nowUtc = new DateTime(2026, 10, 16, 13, 0, 0, DateTimeKind.Utc);

        Assert.True(DetectionAlertMatcher.InSchedule(rule, nowUtc));
        Assert.False(DetectionAlertMatcher.InSchedule(rule with { TimeZone = TimeZoneInfo.Utc }, nowUtc));
        Assert.Null(DetectionAlertMatcher.ResolveTimeZone("Not/AZone"));
    }

    [Fact]
    public async Task EditorKeepsANodeScopeOnlyWhenNoCameraIsChosen()
    {
        using var db = NewDb();
        var settings = new SettingsResolver(db);
        var node = Guid.NewGuid();
        var rule = new AlertRule();

        await AlertRuleEditing.ApplyAsync(rule, Input(AlertConditionType.ObjectDetected, camera: null, node: node), settings, "u", default);
        Assert.Equal(node, rule.NodeId);

        await AlertRuleEditing.ApplyAsync(rule, Input(AlertConditionType.ObjectDetected, camera: Guid.NewGuid(), node: node), settings, "u", default);
        Assert.Null(rule.NodeId);

        await AlertRuleEditing.ApplyAsync(rule, Input(AlertConditionType.CameraNotReporting, camera: Guid.NewGuid(), node: node), settings, "u", default);
        Assert.Null(rule.NodeId);
    }

    private static AlertRuleInput Input(AlertConditionType type, Guid? camera, Guid? node) => new(
        "n", true, type, camera, node, 10, 15, null, null, null, null, 50, [], null, null, "UTC");

    // ── Delivery to subscribers ───────────────────────────────────────────

    private sealed class FakeEmail(bool configured = true) : IEmailService
    {
        public List<string> SentTo { get; } = [];
        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(configured);
        public Task SendAsync(string to, string subject, string htmlBody, string? textBody = null, CancellationToken ct = default)
        {
            SentTo.Add(to);
            return Task.CompletedTask;
        }
    }

    /// <summary>Grants View on exactly the cameras listed (null = everything).</summary>
    private sealed class FakeCameraAccess(HashSet<Guid>? viewable) : ICameraAccessService
    {
        public Task<HashSet<Guid>?> GetAccessibleCameraIdsAsync(ClaimsPrincipal user, CameraAccessActions action, CancellationToken ct = default)
            => Task.FromResult(viewable);
    }

    private sealed class FixedPermissions(bool granted) : IPermissionService
    {
        public Task<bool> HasPermissionAsync(string userId, string resource, string action, CancellationToken ct = default) => Task.FromResult(granted);
        public Task<bool> HasPermissionForRoleNameAsync(string roleName, string resource, string action, CancellationToken ct = default) => Task.FromResult(granted);
        public Task<PermissionSet> GetGrantedAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(new PermissionSet(granted, new HashSet<(string, string)>()));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No push transport is on in these tests.");
    }

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static (NotificationService Service, NotificationHub Hub, FakeEmail Email, ApplicationDbContext Db, AlertRule Rule) Setup(
        HashSet<Guid>? viewable = null, Action<AlertSubscription>? configure = null, bool seesNodes = true)
    {
        var db = NewDb();
        db.Users.Add(new ApplicationUser { Id = "alice", UserName = "alice", Email = "alice@example.com" });
        var rule = new AlertRule { Id = Guid.NewGuid(), Name = "Animals at night", ConditionType = AlertConditionType.ObjectDetected };
        db.AlertRules.Add(rule);
        var sub = new AlertSubscription { Id = Guid.NewGuid(), AlertRuleId = rule.Id, UserId = "alice", Browser = true };
        configure?.Invoke(sub);
        db.AlertSubscriptions.Add(sub);
        db.SaveChanges();

        var hub = new NotificationHub();
        var email = new FakeEmail();
        var service = new NotificationService(db, new SettingsResolver(db), new FakeCameraAccess(viewable), new FixedPermissions(seesNodes), email, hub,
            new FcmClient(() => throw new InvalidOperationException()), new NoHttp(), NullLogger<NotificationService>.Instance);
        return (service, hub, email, db, rule);
    }

    [Theory]
    [InlineData(true, true, true)]     // sees node health, a camera on the node
    [InlineData(false, true, false)]   // lost node-health access
    [InlineData(true, false, false)]   // no camera on that node any more
    public async Task ANodeNotificationNeedsNodeAccessAtSendTime(bool seesNodes, bool cameraOnNode, bool delivered)
    {
        var camera = Guid.NewGuid();
        var (service, hub, _, db, rule) = Setup(viewable: [camera], seesNodes: seesNodes);
        var node = Guid.NewGuid();
        rule.ConditionType = AlertConditionType.NodeOffline;
        rule.NodeId = node;
        db.Cameras.Add(new Camera { Id = camera, Name = "Back yard", NodeId = cameraOnNode ? node : Guid.NewGuid() });
        db.SaveChanges();
        using var tab = hub.Subscribe("alice");

        await service.NotifySubscribersAsync(rule, null, "Node offline", DateTime.UtcNow, null);
        Assert.Equal(delivered, tab.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ASubscriberWithAnOpenTabGetsIt()
    {
        var (service, hub, _, _, rule) = Setup();
        using var tab = hub.Subscribe("alice");

        Assert.Equal(1, await service.NotifySubscribersAsync(rule, Guid.NewGuid(), "Dog on Back yard", DateTime.UtcNow, null));
        Assert.True(tab.Reader.TryRead(out var json));
        Assert.Contains("Dog on Back yard", json);
    }

    [Fact]
    public async Task NothingGoesOutWhenTheTypeIsTurnedOff()
    {
        var (service, hub, _, db, rule) = Setup();
        var settings = new SettingsResolver(db);
        var key = NotificationSettings.TypeEnabledKey(AlertConditionType.ObjectDetected);
        await settings.SetGlobalAsync(key, "false", "admin");
        try
        {
            using var tab = hub.Subscribe("alice");
            Assert.Equal(0, await service.NotifySubscribersAsync(rule, null, "x", DateTime.UtcNow, null));
            Assert.False(tab.Reader.TryRead(out _));
        }
        finally
        {
            // SettingsResolver caches values process-wide; put it back so other tests read their own default.
            await settings.SetGlobalAsync(key, "true", "admin");
        }
    }

    [Fact]
    public async Task MutedSubscriptionsAndCamerasTheUserCantViewAreSkipped()
    {
        var (muted, mutedHub, _, _, mutedRule) = Setup(configure: s => s.MutedUntilUtc = DateTime.UtcNow.AddHours(1));
        using var t1 = mutedHub.Subscribe("alice");
        Assert.Equal(0, await muted.NotifySubscribersAsync(mutedRule, null, "x", DateTime.UtcNow, null));

        var allowed = Guid.NewGuid();
        var (scoped, scopedHub, _, _, scopedRule) = Setup(viewable: [allowed]);
        using var t2 = scopedHub.Subscribe("alice");
        Assert.Equal(0, await scoped.NotifySubscribersAsync(scopedRule, Guid.NewGuid(), "x", DateTime.UtcNow, null));
        Assert.Equal(1, await scoped.NotifySubscribersAsync(scopedRule, allowed, "x", DateTime.UtcNow, null));
    }

    [Fact]
    public async Task AnEmailOnlySubscriberGetsAnEmail()
    {
        var (service, _, email, _, rule) = Setup(configure: s => { s.Browser = false; s.Email = true; });

        Assert.Equal(1, await service.NotifySubscribersAsync(rule, null, "x", DateTime.UtcNow, null));
        Assert.Equal(new[] { "alice@example.com" }, email.SentTo);
    }

    // ── Evidence-lock notifications ───────────────────────────────────────

    private sealed class RecordingNotifications : INotificationService
    {
        public List<string> Messages { get; } = [];
        public Task<int> NotifySubscribersAsync(AlertRule rule, Guid? cameraId, string message, DateTime atUtc, string? url, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.FromResult(1);
        }
        public Task<NotificationTestResult> SendTestAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> NotifySuperAdminsAsync(string title, string message, string? url, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NullAudit : IAuditService
    {
        public Task LogAsync(string action, string? userId, string? userName, string? ipAddress, string? details = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task LockRemovalAndEndingSoonAreAnnouncedOnce()
    {
        using var db = NewDb();
        var cam = new Camera { Id = Guid.NewGuid(), Name = "Back yard", Host = "h", DeviceServiceUri = "http://h/onvif/device_service" };
        db.Cameras.Add(cam);
        var now = DateTime.UtcNow;
        db.EvidenceLocks.AddRange(
            new EvidenceLock { Id = Guid.NewGuid(), CameraId = cam.Id, FromUtc = now.AddDays(-9), ToUtc = now.AddDays(-9).AddMinutes(5), Reason = "case 1",
                CreatedByUserId = "a", CreatedAtUtc = now.AddDays(-6), ExpiresAtUtc = now.AddHours(10) },            // 7-day lock, ending soon
            new EvidenceLock { Id = Guid.NewGuid(), CameraId = cam.Id, FromUtc = now.AddDays(-2), ToUtc = now.AddDays(-2).AddMinutes(5), Reason = "case 2",
                CreatedByUserId = "a", CreatedAtUtc = now.AddHours(-1), ExpiresAtUtc = now.AddHours(23) },           // just placed 1-day lock: not yet
            new EvidenceLock { Id = Guid.NewGuid(), CameraId = cam.Id, FromUtc = now.AddDays(-3), ToUtc = now.AddDays(-3).AddMinutes(5), Reason = "case 3",
                CreatedByUserId = "a", RemovedAtUtc = now.AddMinutes(-2), RemovedByUserName = "Bob", RemovalReason = "closed" });
        var rules = new List<AlertRule>
        {
            new() { Id = Guid.NewGuid(), Name = "Locks ending", ConditionType = AlertConditionType.EvidenceLockExpiringSoon, IsEnabled = true },
            new() { Id = Guid.NewGuid(), Name = "Locks removed", ConditionType = AlertConditionType.EvidenceLockRemoved, IsEnabled = true, NodeId = Guid.NewGuid() },
            new() { Id = Guid.NewGuid(), Name = "Locks removed here", ConditionType = AlertConditionType.EvidenceLockRemoved, IsEnabled = true, CameraId = cam.Id },
        };
        await db.SaveChangesAsync();
        var notifications = new RecordingNotifications();
        var senders = new AlertChannelSenderFactory([]);

        await AlertEvaluatorService.EvaluateEvidenceLockRulesAsync(db, rules, now, senders, new NullAudit(), notifications, NullLogger.Instance, default);
        await AlertEvaluatorService.EvaluateEvidenceLockRulesAsync(db, rules, now.AddMinutes(1), senders, new NullAudit(), notifications, NullLogger.Instance, default);

        // One "ending" (case 1), one "removed" — the node-scoped rule doesn't cover a camera on no node.
        Assert.Equal(2, notifications.Messages.Count);
        Assert.Contains(notifications.Messages, m => m.Contains("case 1"));
        Assert.Contains(notifications.Messages, m => m.Contains("Bob removed") && m.Contains("closed"));
    }
}

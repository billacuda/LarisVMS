using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Notifications;

namespace LarisVMS.Web.Services;

/// <summary>One notification as every transport carries it. Tag is shared across transports, so a
/// browser that gets it twice (an open tab and a push) shows it once.</summary>
public sealed record NotificationPayload(string Id, string Title, string Body, string? Url, string Tag, DateTime AtUtc);

/// <summary>A browser registering for push: Kind "WebPush" (endpoint plus keys) or "Fcm" (token in Endpoint).</summary>
public sealed record RegisterPushDeviceRequest(string Kind, string Endpoint, string? P256dh, string? Auth, string? DeviceLabel);

public sealed record NotificationTestResult(int OpenTabs, int PushDevices, int PushFailures, bool EmailSent, IReadOnlyList<string> Problems);

/// <summary>
/// Personal notifications: an AlertRule firing goes to every subscriber who wants it — in the browser
/// (open tabs through NotificationHub, closed browsers through Web Push or Firebase, whichever an admin
/// turned on) and/or by email. Each firing re-checks that the notification type is switched on, the
/// subscription isn't muted, the user is still active, and the user can view the camera involved.
/// </summary>
public interface INotificationService
{
    /// <summary>Returns the number of users notified.</summary>
    Task<int> NotifySubscribersAsync(AlertRule rule, Guid? cameraId, string message, DateTime atUtc, string? url, CancellationToken ct = default);

    Task<NotificationTestResult> SendTestAsync(string userId, CancellationToken ct = default);

    /// <summary>A security notice for every active Super Admin (browser, and email when set up) — a
    /// change only they should hear about, like where backups go or a node waiting for approval.
    /// Returns the number of admins reached.</summary>
    Task<int> NotifySuperAdminsAsync(string title, string message, string? url, CancellationToken ct = default);
}

public sealed class NotificationService(
    ApplicationDbContext db, ISettingsResolver settings, ICameraAccessService cameraAccess, IPermissionService permissions,
    IEmailService email, NotificationHub hub, FcmClient fcm, IHttpClientFactory httpClientFactory, ILogger<NotificationService> logger) : INotificationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> NotifySubscribersAsync(AlertRule rule, Guid? cameraId, string message, DateTime atUtc, string? url, CancellationToken ct = default)
    {
        if (!await NotificationSettings.IsTypeEnabledAsync(settings, rule.ConditionType, ct)) return 0;

        var now = DateTime.UtcNow;
        var subscriptions = await db.AlertSubscriptions.AsNoTracking()
            .Where(s => s.AlertRuleId == rule.Id && (s.Browser || s.Email) && (s.MutedUntilUtc == null || s.MutedUntilUtc < now))
            .ToListAsync(ct);
        if (subscriptions.Count == 0) return 0;

        var config = await NotificationSettings.LoadAsync(settings, ct);
        var payload = new NotificationPayload(Guid.NewGuid().ToString("N"), rule.Name, message, url,
            $"rule-{rule.Id:N}-{cameraId?.ToString("N") ?? "all"}", atUtc);

        var notified = 0;
        foreach (var sub in subscriptions)
        {
            try
            {
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sub.UserId, ct);
                if (user is null || !IsActive(user, now)) continue;
                if (cameraId is { } cam && !await CanViewCameraAsync(user.Id, cam, ct)) continue;
                if (AlertRuleEditing.IsNodeCondition(rule.ConditionType) && !await CanSeeNodeAsync(user.Id, rule.NodeId, ct)) continue;

                var reached = false;
                if (sub.Browser) reached |= (await SendToBrowsersAsync(user.Id, payload, config, ct)).Reached;
                if (sub.Email && config.Email && !string.IsNullOrWhiteSpace(user.Email)) reached |= await SendEmailAsync(user.Email, payload, ct);
                if (reached) notified++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notifying user {UserId} for rule {RuleId} failed.", sub.UserId, rule.Id);
            }
        }
        return notified;
    }

    public async Task<NotificationTestResult> SendTestAsync(string userId, CancellationToken ct = default)
    {
        var config = await NotificationSettings.LoadAsync(settings, ct);
        var payload = new NotificationPayload(Guid.NewGuid().ToString("N"), "LarisVMS test notification",
            "Notifications are working in this browser.", "/Notifications", "test-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var result = await SendToBrowsersAsync(userId, payload, config, ct);

        var emailSent = false;
        var problems = result.Problems.ToList();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (config.Email && !string.IsNullOrWhiteSpace(user?.Email) && await email.IsConfiguredAsync(ct))
        {
            emailSent = await SendEmailAsync(user.Email, payload with { Body = "Email notifications are working." }, ct);
            if (!emailSent) problems.Add("The test email couldn't be sent. Check Settings → Email.");
        }
        return new NotificationTestResult(result.OpenTabs, result.PushDevices, result.PushFailures, emailSent, problems);
    }

    public async Task<int> NotifySuperAdminsAsync(string title, string message, string? url, CancellationToken ct = default)
    {
        var superRoleIds = await db.RoleProfiles.AsNoTracking().Where(p => p.Tag == LarisVMS.Core.Auth.RoleTags.SuperAdmin)
            .Select(p => p.RoleId).ToListAsync(ct);
        var userIds = await db.UserRoles.AsNoTracking().Where(ur => superRoleIds.Contains(ur.RoleId))
            .Select(ur => ur.UserId).Distinct().ToListAsync(ct);
        if (userIds.Count == 0) return 0;

        var config = await NotificationSettings.LoadAsync(settings, ct);
        var payload = new NotificationPayload(Guid.NewGuid().ToString("N"), title, message, url, "security-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var now = DateTime.UtcNow;
        var reachedCount = 0;
        foreach (var userId in userIds)
        {
            try
            {
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
                if (user is null || !IsActive(user, now)) continue;
                var reached = (await SendToBrowsersAsync(user.Id, payload, config, ct)).Reached;
                if (config.Email && !string.IsNullOrWhiteSpace(user.Email)) reached |= await SendEmailAsync(user.Email, payload, ct);
                if (reached) reachedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Security notice to user {UserId} failed.", userId);
            }
        }
        return reachedCount;
    }

    private sealed record BrowserResult(bool Reached, int OpenTabs, int PushDevices, int PushFailures, List<string> Problems);

    private async Task<BrowserResult> SendToBrowsersAsync(string userId, NotificationPayload payload, NotificationSettings.Snapshot config, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        var problems = new List<string>();
        var tabs = config.OnPrem ? hub.Publish(userId, json) : 0;
        int pushed = 0, failed = 0;

        if (config.WebPush || config.Fcm)
        {
            var devices = await db.PushSubscriptions.Where(p => p.UserId == userId).ToListAsync(ct);
            foreach (var device in devices)
            {
                var (ok, gone, error) = device.Kind switch
                {
                    PushSubscriptionKind.WebPush when config.WebPush => await SendWebPushAsync(device, json, config, ct),
                    PushSubscriptionKind.Fcm when config.Fcm => await SendFcmAsync(device, payload, config, ct),
                    _ => (false, false, (string?)null),
                };
                if (ok) { pushed++; device.LastSuccessAt = DateTime.UtcNow; }
                else if (gone) db.PushSubscriptions.Remove(device);
                else if (error is not null) { failed++; problems.Add(error); }
            }
            await db.SaveChangesAsync(ct);
        }
        return new BrowserResult(tabs > 0 || pushed > 0, tabs, pushed, failed, problems);
    }

    private async Task<(bool Ok, bool Gone, string? Error)> SendWebPushAsync(PushSubscription device, string json,
        NotificationSettings.Snapshot config, CancellationToken ct)
    {
        try
        {
            var endpoint = new Uri(device.Endpoint);
            var body = WebPushProtocol.Encrypt(Encoding.UTF8.GetBytes(json), device.P256dh!, device.Auth!);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            request.Headers.TryAddWithoutValidation("Urgency", "high");
            request.Headers.TryAddWithoutValidation("Authorization", WebPushProtocol.VapidAuthorization(endpoint, config.Subject, config.Vapid!, DateTime.UtcNow));

            using var response = await httpClientFactory.CreateClient("push").SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (true, false, null);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return (false, true, null);
            // Status only: the response body isn't shown to the user.
            return (false, false, $"{endpoint.Host} returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or ArgumentException or UriFormatException)
        {
            return (false, false, $"Couldn't reach the browser's push service: {ex.Message}");
        }
    }

    private async Task<(bool Ok, bool Gone, string? Error)> SendFcmAsync(PushSubscription device, NotificationPayload payload,
        NotificationSettings.Snapshot config, CancellationToken ct)
    {
        try
        {
            var data = new Dictionary<string, string>
            {
                ["id"] = payload.Id, ["title"] = payload.Title, ["body"] = payload.Body,
                ["url"] = payload.Url ?? "/", ["tag"] = payload.Tag, ["atUtc"] = payload.AtUtc.ToString("o"),
            };
            var (result, error) = await fcm.SendAsync(config.FcmAccount!, device.Endpoint, data, ct);
            return (result == FcmClient.SendResult.Sent, result == FcmClient.SendResult.TokenGone, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, false, $"Couldn't reach Firebase: {ex.Message}");
        }
    }

    private async Task<bool> SendEmailAsync(string to, NotificationPayload payload, CancellationToken ct)
    {
        try
        {
            var subject = $"LarisVMS: {payload.Title}";
            var html = $"<p><strong>{WebUtility.HtmlEncode(payload.Title)}</strong></p><p>{WebUtility.HtmlEncode(payload.Body)}</p>" +
                       $"<p style=\"color:#666;font-size:12px\">{payload.AtUtc:yyyy-MM-dd HH:mm} UTC · Manage your notifications in LarisVMS under My notifications.</p>";
            await email.SendAsync(to, subject, html, $"{payload.Title}\n\n{payload.Body}\n\n{payload.AtUtc:yyyy-MM-dd HH:mm} UTC", ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notification email to {To} failed.", to);
            return false;
        }
    }

    private static bool IsActive(ApplicationUser user, DateTime now)
        => !user.DisabledByDirectorySync && (user.LockoutEnd is null || user.LockoutEnd <= now);

    /// <summary>Camera access for a user who isn't the one making this request: the same check the
    /// web pages use, run against a principal built from the user's own roles.</summary>
    private async Task<bool> CanViewCameraAsync(string userId, Guid cameraId, CancellationToken ct)
    {
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(await PrincipalForAsync(userId, ct), Core.Enums.CameraAccessActions.View, ct);
        return accessible is null || accessible.Contains(cameraId);
    }

    /// <summary>A node-health notification goes only to someone who can still see node health, and,
    /// for one node, a camera on it (the same rule as My notifications' node list) — access can be
    /// taken away after they subscribed.</summary>
    private async Task<bool> CanSeeNodeAsync(string userId, Guid? nodeId, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(userId, "Dashboard", "View", ct)
            && !await permissions.HasPermissionAsync(userId, "Nodes", "Edit", ct)) return false;
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(await PrincipalForAsync(userId, ct), Core.Enums.CameraAccessActions.View, ct);
        if (accessible is null) return true;
        if (nodeId is not { } node) return accessible.Count > 0;
        return await db.Cameras.AsNoTracking().AnyAsync(c => c.NodeId == node && accessible.Contains(c.Id), ct);
    }

    private async Task<ClaimsPrincipal> PrincipalForAsync(string userId, CancellationToken ct)
    {
        var roles = await db.UserRoles.Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .ToListAsync(ct);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Notification"));
    }
}

using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// The server's list of signed-in browsers (<see cref="UserSession"/>): started at sign-in, checked
/// on every request by the cookie's validation in Program.cs, ended at sign-out, from the Sessions
/// page, or by an admin.
/// </summary>
public sealed class UserSessionService(ApplicationDbContext db)
{
    /// <summary>The sign-in cookie's property holding the session id.</summary>
    public const string CookieItem = ".larisvms.session";

    /// <summary>This request's session id (HttpContext.Items), once the cookie has been checked.</summary>
    public const string RequestItem = "larisvms.session";

    /// <summary>How stale "last active" may get before a request updates it.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    /// <summary>This request's session row as read when the cookie was checked.</summary>
    public const string RequestRowItem = "larisvms.sessionRow";

    public static Guid? Current(HttpContext context) => context.Items[RequestItem] as Guid?;

    public static UserSession? CurrentRow(HttpContext context) => context.Items[RequestRowItem] as UserSession;

    /// <param name="confirmed">A real sign-in just checked the password: admin pages open without
    /// asking again for <see cref="StepUpPolicy.Window"/>.</param>
    public async Task<UserSession> StartAsync(string userId, HttpContext context, DateTime? createdUtc = null, bool confirmed = true)
    {
        var now = DateTime.UtcNow;
        // Sessions nobody has used for longer than the sign-in cookie lasts unused (397 days, even
        // for a role whose sessions never expire) are long dead; tidy them up here rather than in a
        // sweep.
        var oldest = now.AddDays(-397);
        await db.UserSessions.Where(s => s.UserId == userId && s.LastSeenUtc < oldest).ExecuteDeleteAsync();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedUtc = createdUtc ?? now,
            LastSeenUtc = now,
            ConfirmedUntilUtc = confirmed ? now + StepUpPolicy.Window : null,
            IpAddress = Truncate(context.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 256),
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public Task ConfirmAsync(Guid id, DateTime untilUtc) =>
        db.UserSessions.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ConfirmedUntilUtc, untilUtc));

    public Task<UserSession?> FindAsync(Guid id, string userId) =>
        db.UserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);

    public Task<List<UserSession>> ListAsync(string userId) =>
        db.UserSessions.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.LastSeenUtc).ToListAsync();

    public Task TouchAsync(Guid id, DateTime nowUtc) =>
        db.UserSessions.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastSeenUtc, nowUtc));

    /// <summary>Ends one of <paramref name="userId"/>'s sessions; false when it wasn't theirs or was
    /// already over.</summary>
    public async Task<bool> EndAsync(Guid id, string userId) =>
        await db.UserSessions.Where(s => s.Id == id && s.UserId == userId).ExecuteDeleteAsync() > 0;

    /// <summary>Ends every session of <paramref name="userId"/> except <paramref name="keep"/>.</summary>
    public Task<int> EndAllAsync(string userId, Guid? keep = null) =>
        db.UserSessions.Where(s => s.UserId == userId && (keep == null || s.Id != keep)).ExecuteDeleteAsync();

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= length ? value : value[..length];
}

/// <summary>
/// Per-role idle timeout: a session nobody has opened a page in for this long is signed out. Off (0)
/// by default — a screen left showing live video is idle by this measure. A user with several roles
/// gets the shortest timeout any of them sets.
/// </summary>
public static class SessionIdlePolicy
{
    public const string SettingKeyPrefix = "SessionIdle.Role.";
    public const int MaxMinutes = 1440;

    public static string SettingKey(string roleId) => SettingKeyPrefix + roleId;

    /// <summary>0 (off) to <see cref="MaxMinutes"/>.</summary>
    public static int Clamp(int minutes) => Math.Clamp(minutes, 0, MaxMinutes);

    /// <summary>The shortest timeout any role sets; null when none sets one.</summary>
    public static TimeSpan? EffectiveTimeout(IEnumerable<int> perRoleMinutes)
    {
        var set = perRoleMinutes.Select(Clamp).Where(m => m > 0).ToList();
        return set.Count == 0 ? null : TimeSpan.FromMinutes(set.Min());
    }

    public static bool IsIdle(DateTime lastSeenUtc, DateTime nowUtc, TimeSpan? timeout) =>
        timeout is { } t && nowUtc - lastSeenUtc > t;

    /// <summary>Whether a request is the person doing something (opening a page, sending a form),
    /// not something an open page does by itself (API calls, files, live connections).</summary>
    public static bool IsActivity(string method, PathString path) =>
        (HttpMethods.IsGet(method) || HttpMethods.IsPost(method))
        && !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase)
        && !Path.HasExtension(path.Value);

    /// <summary>"Edge on Windows" from a User-Agent header, for the Sessions list.</summary>
    public static string DescribeBrowser(string? userAgent)
    {
        var ua = userAgent ?? "";
        var browser = ua.Contains("Edg/") ? "Edge"
            : ua.Contains("OPR/") ? "Opera"
            : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") || ua.Contains("CriOS/") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari"
            : null;
        var os = ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
            : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS X") ? "macOS"
            : ua.Contains("Linux") ? "Linux"
            : null;
        return (browser, os) switch
        {
            (null, null) => "",
            (null, _) => os!,
            (_, null) => browser!,
            _ => $"{browser} · {os}",
        };
    }
}

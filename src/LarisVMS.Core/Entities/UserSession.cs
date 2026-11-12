namespace LarisVMS.Core.Entities;

/// <summary>
/// One signed-in browser. The sign-in cookie carries <see cref="Id"/>; a session whose row is gone is
/// signed out on its next request, which is how "sign out that session", "sign out everywhere", and
/// the idle timeout take effect at once rather than when the cookie next re-checks the account.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    /// <summary>The last page opened or form sent (not background requests a page makes by itself),
    /// to the minute.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Until when admin pages open without asking for the password again (signing in, or
    /// confirming it, starts this; using admin pages extends it).</summary>
    public DateTime? ConfirmedUntilUtc { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

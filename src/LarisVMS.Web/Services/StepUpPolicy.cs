namespace LarisVMS.Web.Services;

/// <summary>
/// Admin pages ask for the password again unless it was given recently: signing in, or confirming it
/// on <see cref="PagePath"/>, opens them for <see cref="Window"/>, and every admin request extends
/// that. A session someone else gets hold of (a browser left signed in, a stolen cookie) can then use
/// the system but not change how it's set up, who can sign in, or the encryption keys. Opt-in
/// (Settings → Security, <see cref="SettingKey"/>), off by default so a home install just works.
/// </summary>
public static class StepUpPolicy
{
    public const string PagePath = "/Identity/Account/Confirm";

    /// <summary>Global setting: true asks for the password again on admin pages. Off by default.</summary>
    public const string SettingKey = "Security.ConfirmPasswordForAdmin";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Extend the window only when it's this much shorter than a full one, so busy admin
    /// pages don't write to the database on every request.</summary>
    private static readonly TimeSpan ExtendAfter = TimeSpan.FromMinutes(1);

    // Changes through the APIs the admin pages use.
    private static readonly string[] ProtectedApiPrefixes = ["/api/admin", "/api/encryption"];

    /// <summary>Every admin page (/Admin, with whatever it sends), and changes through the admin and
    /// encryption APIs.</summary>
    public static bool IsProtected(string method, PathString path) =>
        path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase)
        || (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
            && ProtectedApiPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)));

    public static bool IsConfirmed(DateTime? confirmedUntilUtc, DateTime nowUtc) =>
        confirmedUntilUtc is { } until && until > nowUtc;

    public static bool ShouldExtend(DateTime confirmedUntilUtc, DateTime nowUtc) =>
        confirmedUntilUtc - nowUtc < Window - ExtendAfter;
}

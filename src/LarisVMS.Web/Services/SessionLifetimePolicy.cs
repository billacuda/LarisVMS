namespace LarisVMS.Web.Services;

/// <summary>
/// Pure decision logic behind the per-role session lifetime setting — extracted from Program.cs's
/// cookie <c>OnValidatePrincipal</c> handler so the actual math ("what's the effective window for a
/// user holding several roles with different configured limits") is unit-testable without a real
/// authentication pipeline.
///
/// A role with no configured value defaults to <see cref="DefaultHours"/> (24h, per the original
/// ask — sessions were expiring too fast at the previous fixed 60-minute cookie timeout). A limit is
/// at most <see cref="MaxHours"/> (30 days); 0 means never expire, for a home install where signing
/// in again is only a chore. A user holding several roles is bound by whichever is <b>most
/// restrictive</b>: the shortest limit among their roles applies, and "never" only when every role
/// says so.
/// </summary>
public static class SessionLifetimePolicy
{
    public const string SettingKeyPrefix = "SessionLifetime.Role.";
    public const int DefaultHours = 24;

    /// <summary>The longest any session lasts before signing in again (ASVS 7.3.2).</summary>
    public const int MaxHours = 720;

    /// <summary>What a role's setting is stored and shown as: 0 (never) or 1 to <see cref="MaxHours"/>.</summary>
    public static int Clamp(int hours) => hours <= 0 ? 0 : Math.Min(hours, MaxHours);

    /// <summary>Keyed by the role's real database Id, not its name — Settings keys are just strings
    /// this app controls the shape of, but keying by Id here deliberately mirrors the fix in
    /// SetupService.SeedViewerPermissionsAsync (0.97.0): a role's *name* is not a stable identifier
    /// to hang a lookup on, its Id is.</summary>
    public static string SettingKey(string roleId) => SettingKeyPrefix + roleId;

    /// <summary>The effective session lifetime for a user holding roles with these configured hour
    /// values: the shortest limit, each clamped to <see cref="MaxHours"/>. Null (never expires) only
    /// when every role is 0; a user with no roles gets the ceiling.</summary>
    public static TimeSpan? EffectiveWindow(IEnumerable<int> perRoleHours)
    {
        var all = perRoleHours.Select(Clamp).ToList();
        if (all.Count == 0) return TimeSpan.FromHours(MaxHours);
        var limits = all.Where(h => h > 0).ToList();
        return limits.Count == 0 ? null : TimeSpan.FromHours(limits.Min());
    }

    /// <summary>Whether a ticket issued at <paramref name="issuedUtc"/> has aged past
    /// <paramref name="window"/> as of <paramref name="nowUtc"/>. A null window (never expires)
    /// never expires by definition.</summary>
    public static bool HasExpired(DateTimeOffset issuedUtc, DateTimeOffset nowUtc, TimeSpan? window)
        => window is { } w && nowUtc - issuedUtc > w;
}

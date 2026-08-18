namespace LarisVMS.Web.Services;

/// <summary>
/// Pure decision logic behind the per-role session lifetime setting — extracted from Program.cs's
/// cookie <c>OnValidatePrincipal</c> handler so the actual math ("what's the effective window for a
/// user holding several roles with different configured limits") is unit-testable without a real
/// authentication pipeline.
///
/// A role with no configured value defaults to <see cref="DefaultHours"/> (24h, per the original
/// ask — sessions were expiring too fast at the previous fixed 60-minute cookie timeout). 0 means
/// that role never expires. A user holding several roles is bound by whichever is <b>most
/// restrictive</b>: the shortest positive window among their roles always applies, even if another
/// of their roles says "never" — a role marked unlimited should not let a user escape a stricter
/// role's own limit just by holding both. Only a user whose <i>every</i> role says 0 gets no cap at
/// all.
/// </summary>
public static class SessionLifetimePolicy
{
    public const string SettingKeyPrefix = "SessionLifetime.Role.";
    public const int DefaultHours = 24;

    /// <summary>Keyed by the role's real database Id, not its name — Settings keys are just strings
    /// this app controls the shape of, but keying by Id here deliberately mirrors the fix in
    /// SetupService.SeedViewerPermissionsAsync (0.97.0): a role's *name* is not a stable identifier
    /// to hang a lookup on, its Id is.</summary>
    public static string SettingKey(string roleId) => SettingKeyPrefix + roleId;

    /// <summary>The effective session lifetime for a user holding roles with these configured hour
    /// values, or null if the session should never expire (only when every value is 0 — including
    /// when the list itself is empty, since a roleless user can't hold anything more restrictive).</summary>
    public static TimeSpan? EffectiveWindow(IEnumerable<int> perRoleHours)
    {
        var positive = perRoleHours.Where(h => h > 0).ToList();
        return positive.Count == 0 ? null : TimeSpan.FromHours(positive.Min());
    }

    /// <summary>Whether a ticket issued at <paramref name="issuedUtc"/> has aged past
    /// <paramref name="window"/> as of <paramref name="nowUtc"/>. A null window (never expires)
    /// never expires by definition.</summary>
    public static bool HasExpired(DateTimeOffset issuedUtc, DateTimeOffset nowUtc, TimeSpan? window)
        => window is { } w && nowUtc - issuedUtc > w;
}

using System.Security.Claims;

namespace LarisVMS.Core.Auth;

/// <summary>
/// What a limited API key may do, carried as claims on its principal. A key's effective permissions
/// are its role's, narrowed to its own list: both checks must pass, so a key can never exceed its role.
/// Anything that isn't a limited key passes straight through.
/// </summary>
public static class ApiKeyScope
{
    public const string KeyIdClaim = "ApiKeyId";
    public const string LimitedClaim = "ApiKeyLimited";
    public const string PermissionClaim = "ApiKeyPermission";

    /// <summary>The permissions /api/v1 checks: the only ones worth offering when limiting a key.</summary>
    public static readonly IReadOnlyList<(string Resource, string Action)> ApiPermissions =
    [
        ("Dashboard", "View"), ("Cameras", "View"), ("Cameras", "Edit"), ("Recording", "Manual"),
        ("Playback", "View"), ("Bookmarks", "Edit"), ("Exports", "View"), ("EvidenceLocks", "Create"),
    ];

    public static string Format(string resource, string action) => resource + "." + action;

    public static bool IsLimited(ClaimsPrincipal user) => user.HasClaim(c => c.Type == LimitedClaim);

    public static bool Allows(ClaimsPrincipal user, string resource, string action) =>
        !IsLimited(user) || user.HasClaim(PermissionClaim, Format(resource, action));

    /// <summary>The claims a key adds to its principal. Null limits means an unlimited key.</summary>
    public static IEnumerable<Claim> ClaimsFor(IReadOnlySet<(string Resource, string Action)>? limits)
    {
        if (limits is null) yield break;
        yield return new Claim(LimitedClaim, "true");
        foreach (var (resource, action) in limits) yield return new Claim(PermissionClaim, Format(resource, action));
    }

    /// <summary>The ID that identifies who's acting: a user's ID, or "apikey:{id}" for a key, which has
    /// no user. Keeps two keys from looking like one anonymous actor to PTZ holds, exports, and rate
    /// limits. Null for an anonymous request.</summary>
    public static string? ActorId(this ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? (user.FindFirst(KeyIdClaim)?.Value is { } keyId ? "apikey:" + keyId : null);
}

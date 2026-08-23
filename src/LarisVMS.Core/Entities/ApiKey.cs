namespace LarisVMS.Core.Entities;

/// <summary>
/// A machine credential for the external REST API (M20), bound to exactly one Role — the key acts
/// with that role's own permissions through the same [Authorize("Resource.Action")] pipeline a
/// logged-in user goes through, via ApiKeyAuthMiddleware building a ClaimsPrincipal carrying the
/// role's name as a ClaimTypes.Role claim (no ClaimTypes.NameIdentifier — an API key is a role-bound
/// credential, not a disguised user; see PermissionAuthorizationHandler's role-claim branch and
/// CameraAccessService's authenticated-not-just-named-user guard). RoleId is a plain FK against
/// AspNetRoles.Id with no navigation property, matching Permission.RoleId's own convention.
///
/// KeyHash is a one-way SHA-256 digest (SecretHash, the same helper Node.ApiKeyHash uses) — the raw
/// key is never stored anywhere and is shown to the admin exactly once, at creation. KeyPrefix keeps
/// enough of the raw value in plaintext (not security-sensitive on its own) to tell keys apart in the
/// admin list without ever reversing the hash.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

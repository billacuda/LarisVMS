using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Issues, authenticates, and revokes ApiKey rows for the M20 REST API. See ApiKey's own doc
/// comment for why a key is bound to a Role and nothing else.</summary>
public interface IApiKeyService
{
    Task<List<ApiKey>> ListAsync(CancellationToken ct = default);

    /// <summary>Creates a new key bound to roleId and returns the raw value — the only time it's ever
    /// available in full. Only KeyHash (never reversible) and KeyPrefix (a plaintext fragment, for
    /// telling keys apart in the admin list) are persisted.</summary>
    Task<(ApiKey Key, string RawValue)> GenerateAsync(string name, string roleId, string? createdByUserId, CancellationToken ct = default);

    /// <summary>Validates a raw key presented on a request (ApiKeyAuthMiddleware): hashes it, looks up
    /// a non-revoked ApiKey by KeyHash, and — best-effort, same "write on every authenticated use"
    /// tradeoff Node.LastSeenAt already accepts — stamps LastUsedAtUtc. Returns the key's own RoleId
    /// (not the key id) plus the resolved role name the middleware needs for the ClaimTypes.Role
    /// claim, or null for a missing/revoked/unknown key.</summary>
    Task<(Guid ApiKeyId, string RoleName)?> AuthenticateAsync(string rawValue, CancellationToken ct = default);

    Task RevokeAsync(Guid id, CancellationToken ct = default);
}

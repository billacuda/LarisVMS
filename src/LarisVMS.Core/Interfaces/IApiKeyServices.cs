using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>A valid key, as the auth middleware needs it. Null Limits means the key has its role's
/// full permissions.</summary>
public readonly record struct ApiKeyAuthentication(Guid ApiKeyId, string RoleName, string KeyName,
    IReadOnlySet<(string Resource, string Action)>? Limits);

/// <summary>Issues, authenticates, edits, and revokes ApiKey rows for the REST API. See ApiKey's own
/// doc comment for how a key gets its permissions.</summary>
public interface IApiKeyService
{
    Task<List<ApiKey>> ListAsync(CancellationToken ct = default);

    /// <summary>One key and its limits (null when unlimited), or null if there's no such key.</summary>
    Task<(ApiKey Key, IReadOnlySet<(string Resource, string Action)>? Limits)?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates a new key bound to roleId and returns the raw value — the only time it's ever
    /// available in full. Only KeyHash (never reversible) and KeyPrefix (a plaintext fragment, for
    /// telling keys apart in the admin list) are persisted. Non-null limits narrows the key to those
    /// permissions; an empty set is a key that can do nothing.</summary>
    Task<(ApiKey Key, string RawValue)> GenerateAsync(string name, string roleId, string? createdByUserId, CancellationToken ct = default,
        DateTime? expiresAtUtc = null, IReadOnlyCollection<(string Resource, string Action)>? limits = null);

    /// <summary>Renames a key and replaces its limits. The key's value doesn't change, so anything
    /// using it keeps working.</summary>
    Task UpdateAsync(Guid id, string name, IReadOnlyCollection<(string Resource, string Action)>? limits, CancellationToken ct = default);

    /// <summary>Validates a raw key presented on a request (ApiKeyAuthMiddleware): hashes it, looks up
    /// a non-revoked ApiKey by KeyHash, and — best-effort, same "write on every authenticated use"
    /// tradeoff Node.LastSeenAt already accepts — stamps LastUsedAtUtc. Null for a
    /// missing/revoked/unknown key.</summary>
    Task<ApiKeyAuthentication?> AuthenticateAsync(string rawValue, CancellationToken ct = default);

    Task RevokeAsync(Guid id, CancellationToken ct = default);
}

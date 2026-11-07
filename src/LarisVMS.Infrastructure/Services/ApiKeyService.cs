using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Infrastructure.Services;

public class ApiKeyService(ApplicationDbContext db) : IApiKeyService
{
    private const string Prefix = "lvk_";

    public async Task<List<ApiKey>> ListAsync(CancellationToken ct = default)
        => await db.ApiKeys.AsNoTracking().OrderBy(k => k.Name).ToListAsync(ct);

    public async Task<(ApiKey Key, IReadOnlySet<(string Resource, string Action)>? Limits)?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return null;
        return (key, await LoadLimitsAsync(key, ct));
    }

    public async Task<(ApiKey Key, string RawValue)> GenerateAsync(string name, string roleId, string? createdByUserId, CancellationToken ct = default,
        DateTime? expiresAtUtc = null, IReadOnlyCollection<(string Resource, string Action)>? limits = null)
    {
        var raw = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyHash = SecretHash.Hash(raw),
            KeyPrefix = raw[..Math.Min(raw.Length, 12)],
            RoleId = roleId,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = createdByUserId,
            ExpiresAtUtc = expiresAtUtc,
            PermissionsLimited = limits is not null,
        };
        db.ApiKeys.Add(key);
        AddLimits(key.Id, limits);
        await db.SaveChangesAsync(ct);

        return (key, raw);
    }

    public async Task UpdateAsync(Guid id, string name, IReadOnlyCollection<(string Resource, string Action)>? limits, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new KeyNotFoundException();
        key.Name = name;
        key.PermissionsLimited = limits is not null;

        db.ApiKeyPermissions.RemoveRange(await db.ApiKeyPermissions.Where(p => p.ApiKeyId == id).ToListAsync(ct));
        AddLimits(id, limits);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ApiKeyAuthentication?> AuthenticateAsync(string rawValue, CancellationToken ct = default)
    {
        var hash = SecretHash.Hash(rawValue);
        var now = DateTime.UtcNow;
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAtUtc == null
            && (k.ExpiresAtUtc == null || k.ExpiresAtUtc > now), ct);
        if (key is null) return null;

        var roleName = await db.Roles.Where(r => r.Id == key.RoleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        if (roleName is null) return null; // the role was deleted out from under this key

        key.LastUsedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ApiKeyAuthentication(key.Id, roleName, key.Name, await LoadLimitsAsync(key, ct));
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null || key.RevokedAtUtc is not null) return;
        key.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private void AddLimits(Guid keyId, IReadOnlyCollection<(string Resource, string Action)>? limits)
    {
        foreach (var (resource, action) in (limits ?? []).Distinct())
            db.ApiKeyPermissions.Add(new ApiKeyPermission { ApiKeyId = keyId, Resource = resource, Action = action });
    }

    private async Task<IReadOnlySet<(string Resource, string Action)>?> LoadLimitsAsync(ApiKey key, CancellationToken ct)
    {
        if (!key.PermissionsLimited) return null;
        var rows = await db.ApiKeyPermissions.AsNoTracking().Where(p => p.ApiKeyId == key.Id)
            .Select(p => new { p.Resource, p.Action }).ToListAsync(ct);
        return rows.Select(p => (p.Resource, p.Action)).ToHashSet();
    }
}

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

    public async Task<(ApiKey Key, string RawValue)> GenerateAsync(string name, string roleId, string? createdByUserId, CancellationToken ct = default)
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
            CreatedByUserId = createdByUserId
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);

        return (key, raw);
    }

    public async Task<(Guid ApiKeyId, string RoleName)?> AuthenticateAsync(string rawValue, CancellationToken ct = default)
    {
        var hash = SecretHash.Hash(rawValue);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAtUtc == null, ct);
        if (key is null) return null;

        var roleName = await db.Roles.Where(r => r.Id == key.RoleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        if (roleName is null) return null; // the role was deleted out from under this key

        key.LastUsedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (key.Id, roleName);
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null || key.RevokedAtUtc is not null) return;
        key.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

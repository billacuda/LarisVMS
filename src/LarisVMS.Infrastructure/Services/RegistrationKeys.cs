using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// What a node or proxy may register with: the shared registration key (<c>Node.RegistrationKey</c>),
/// unless it's been turned off, or an unused, unexpired one-time code (<see cref="RegistrationCode"/>).
/// </summary>
public static class RegistrationKeys
{
    public const string SharedKeySettingKey = "Node.RegistrationKey";
    public const string SharedKeyEnabledKey = "Node.SharedRegistrationKeyEnabled";
    public static readonly TimeSpan[] Lifetimes = [TimeSpan.FromHours(1), TimeSpan.FromHours(24), TimeSpan.FromDays(7)];

    /// <summary>Unused codes older than this are removed when a new one is made.</summary>
    private static readonly TimeSpan KeepExpired = TimeSpan.FromDays(30);

    /// <summary>Whether <paramref name="presented"/> may register. A one-time code is marked used by
    /// <paramref name="usedBy"/> on the context, and saved with the caller's own SaveChanges, so it's
    /// spent only if the registration itself is saved. Two registrations racing for one code: the
    /// second save fails its concurrency check.</summary>
    public static async Task<bool> AcceptAsync(ApplicationDbContext db, ISettingsResolver settings, string? presented,
        string usedBy, CancellationToken ct)
    {
        presented = presented?.Trim();
        if (string.IsNullOrEmpty(presented)) return false;

        if (await settings.GetAsync(SharedKeyEnabledKey, true, ct: ct))
        {
            var shared = SecretProtection.Unprotect(await settings.GetRawAsync(SharedKeySettingKey, ct: ct));
            if (!string.IsNullOrEmpty(shared) && SecretHash.FixedTimeEquals(presented, shared)) return true;
        }

        var hash = SecretHash.Hash(presented);
        var now = DateTime.UtcNow;
        var code = await db.RegistrationCodes.FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (code is null || code.UsedUtc is not null || code.ExpiresUtc <= now) return false;

        code.UsedUtc = now;
        code.UsedBy = usedBy;
        return true;
    }

    /// <summary>A new code, valid for <paramref name="lifetime"/>. Returns the code itself, which isn't stored.</summary>
    public static async Task<(string Code, RegistrationCode Row)> CreateAsync(ApplicationDbContext db, TimeSpan lifetime,
        string? createdBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var old = await db.RegistrationCodes.Where(c => c.ExpiresUtc < now - KeepExpired).ToListAsync(ct);
        db.RegistrationCodes.RemoveRange(old);

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var row = new RegistrationCode
        {
            Id = Guid.NewGuid(),
            CodeHash = SecretHash.Hash(code),
            CreatedBy = createdBy,
            CreatedUtc = now,
            ExpiresUtc = now + lifetime,
        };
        db.RegistrationCodes.Add(row);
        await db.SaveChangesAsync(ct);
        return (code, row);
    }
}

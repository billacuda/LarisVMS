using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class UserPreferenceService(ApplicationDbContext db) : IUserPreferenceService
{
    public async Task<Dictionary<string, string>> GetAllAsync(string userId, CancellationToken ct = default)
        => await db.UserPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Key, p => p.Value, ct);

    public async Task SetAsync(string userId, string key, string value, CancellationToken ct = default)
    {
        var existing = await db.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);

        if (existing is null)
        {
            db.UserPreferences.Add(new UserPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Key = key,
                Value = value,
                LastModifiedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Value = value;
            existing.LastModifiedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}

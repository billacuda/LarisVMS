using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Rcordr.Core.Entities;
using Rcordr.Core.Enums;
using Rcordr.Core.Interfaces;
using Rcordr.Infrastructure.Data;

namespace Rcordr.Infrastructure.Services;

/// <summary>
/// Resolution order for a per-camera-overridable setting: Camera → CameraGroup ancestors (nearest
/// first) → Global (Setting) → caller-supplied default. Camera/CameraGroup don't exist until M2, so
/// today this only walks Camera-scope and Global-scope rows directly by id — the group-ancestor walk
/// is added once CameraGroup.ParentId exists, without changing this interface.
///
/// Cached in a ConcurrentDictionary keyed "{cameraId}|{key}", invalidated in bulk on any write since
/// individual invalidation needs to know which cameras/groups a key change affects (that mapping
/// isn't available until Camera/CameraGroup land) — acceptable because settings changes are rare
/// relative to reads.
/// </summary>
public class SettingsResolver(ApplicationDbContext db) : ISettingsResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    public async Task<string?> GetRawAsync(string key, Guid? cameraId = null, CancellationToken ct = default)
    {
        var cacheKey = $"{cameraId}|{key}";
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        string? value = null;

        if (cameraId is not null)
        {
            value = await db.SettingOverrides
                .Where(o => o.Scope == SettingScope.Camera && o.ScopeId == cameraId && o.Key == key)
                .Select(o => o.Value)
                .FirstOrDefaultAsync(ct);
        }

        value ??= await db.Settings
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        Cache[cacheKey] = value;
        return value;
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, CancellationToken ct = default)
    {
        var raw = await GetRawAsync(key, cameraId, ct);
        if (raw is null) return defaultValue;

        try
        {
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter.CanConvertFrom(typeof(string)))
                return (T)converter.ConvertFromString(null, CultureInfo.InvariantCulture, raw)!;
        }
        catch
        {
            // Malformed stored value — fall through to the caller's default rather than throw from
            // what call sites treat as a simple config read.
        }

        return defaultValue;
    }

    public async Task SetGlobalAsync(string key, string value, string? modifiedBy = null, CancellationToken ct = default)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting
            {
                Id = Guid.NewGuid(),
                Key = key,
                Value = value,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                LastModifiedBy = modifiedBy
            });
        }
        else
        {
            setting.Value = value;
            setting.LastModifiedAt = DateTime.UtcNow;
            setting.LastModifiedBy = modifiedBy;
        }

        await db.SaveChangesAsync(ct);
        await InvalidateAsync();
    }

    public Task InvalidateAsync()
    {
        Cache.Clear();
        return Task.CompletedTask;
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

/// <summary>
/// Resolution order for a per-camera-overridable setting: Camera → Node (the camera's current node,
/// unless the caller already knows a different one to check) → Global (Setting) → caller-supplied
/// default. CameraGroup-scoped rows are schema-ready (SettingOverride.Scope) but there's no
/// ancestor-walk yet — no feature has needed a group-level override so far, so it's deferred rather
/// than built against a guess at the real usage pattern.
///
/// Cached in a ConcurrentDictionary keyed "{cameraId}|{nodeId}|{key}", invalidated in bulk on any
/// write since individual invalidation needs to know which cameras/nodes a key change affects —
/// acceptable because settings changes are rare relative to reads.
/// </summary>
public class SettingsResolver(ApplicationDbContext db) : ISettingsResolver
{
    private static readonly ConcurrentDictionary<string, (string? Value, SettingScope? Source)> Cache = new();

    private async Task<(string? Value, SettingScope? Source)> ResolveAsync(string key, Guid? cameraId, Guid? nodeId, CancellationToken ct)
    {
        var cacheKey = $"{cameraId}|{nodeId}|{key}";
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        string? value = null;
        SettingScope? source = null;
        var resolvedNodeId = nodeId;

        if (cameraId is not null)
        {
            value = await db.SettingOverrides
                .Where(o => o.Scope == SettingScope.Camera && o.ScopeId == cameraId && o.Key == key)
                .Select(o => o.Value)
                .FirstOrDefaultAsync(ct);
            if (value is not null) source = SettingScope.Camera;

            resolvedNodeId ??= await db.Cameras
                .Where(c => c.Id == cameraId)
                .Select(c => c.NodeId)
                .FirstOrDefaultAsync(ct);
        }

        if (value is null && resolvedNodeId is not null)
        {
            value = await db.SettingOverrides
                .Where(o => o.Scope == SettingScope.Node && o.ScopeId == resolvedNodeId && o.Key == key)
                .Select(o => o.Value)
                .FirstOrDefaultAsync(ct);
            if (value is not null) source = SettingScope.Node;
        }

        if (value is null)
        {
            value = await db.Settings.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            if (value is not null) source = SettingScope.Global;
        }

        var result = (value, source);
        Cache[cacheKey] = result;
        return result;
    }

    public async Task<string?> GetRawAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
        => (await ResolveAsync(key, cameraId, nodeId, ct)).Value;

    public async Task<SettingScope?> GetSourceAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
        => (await ResolveAsync(key, cameraId, nodeId, ct)).Source;

    public async Task<string?> GetOwnOverrideAsync(SettingScope scope, Guid scopeId, string key, CancellationToken ct = default)
        => await db.SettingOverrides
            .Where(o => o.Scope == scope && o.ScopeId == scopeId && o.Key == key)
            .Select(o => o.Value)
            .FirstOrDefaultAsync(ct);

    public async Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
    {
        var raw = await GetRawAsync(key, cameraId, nodeId, ct);
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

    public async Task SetOverrideAsync(SettingScope scope, Guid scopeId, string key, string? value, string? modifiedBy = null, CancellationToken ct = default)
    {
        var existing = await db.SettingOverrides
            .FirstOrDefaultAsync(o => o.Scope == scope && o.ScopeId == scopeId && o.Key == key, ct);

        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing is not null) db.SettingOverrides.Remove(existing);
        }
        else if (existing is null)
        {
            db.SettingOverrides.Add(new SettingOverride
            {
                Id = Guid.NewGuid(),
                Scope = scope,
                ScopeId = scopeId,
                Key = key,
                Value = value,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                LastModifiedBy = modifiedBy
            });
        }
        else
        {
            existing.Value = value;
            existing.LastModifiedAt = DateTime.UtcNow;
            existing.LastModifiedBy = modifiedBy;
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

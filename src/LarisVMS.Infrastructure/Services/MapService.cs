using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class MapService(ApplicationDbContext db, ISettingsResolver settings) : IMapService
{
    public const string TileUrlKey = "Map.TileUrl";
    public const string TileAttributionKey = "Map.TileAttribution";
    public const string DefaultTileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public const string DefaultAttribution = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors";

    public async Task<List<SiteMapSummaryDto>> ListAsync(IReadOnlySet<Guid>? accessible, CancellationToken ct = default)
    {
        var maps = await db.SiteMaps.AsNoTracking().OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
            .Select(m => new
            {
                m.Id, m.Name, m.Kind, m.CenterLat, m.CenterLng, m.Zoom, m.ImageWidth, m.ImageHeight,
                Cameras = m.Placements.Select(p => p.CameraId).ToList(),
            })
            .ToListAsync(ct);
        return maps.Select(m => new SiteMapSummaryDto(m.Id, m.Name, m.Kind.ToString(), m.CenterLat, m.CenterLng, m.Zoom,
            m.ImageWidth, m.ImageHeight, m.Cameras.Count(c => accessible is null || accessible.Contains(c)))).ToList();
    }

    public async Task<SiteMapDto?> GetAsync(Guid id, IReadOnlySet<Guid>? accessible, CancellationToken ct = default)
    {
        var map = await db.SiteMaps.AsNoTracking().Where(m => m.Id == id)
            .Select(m => new { m.Id, m.Name, m.Kind, m.CenterLat, m.CenterLng, m.Zoom, m.ImageWidth, m.ImageHeight })
            .FirstOrDefaultAsync(ct);
        if (map is null) return null;

        var now = DateTime.UtcNow;
        var placements = await db.CameraPlacements.AsNoTracking().Where(p => p.MapId == id)
            .Select(p => new
            {
                p.CameraId, p.Camera.Name, p.Camera.IsEnabled, p.Camera.NodeId,
                Health = p.Camera.Streams.Where(s => s.Role == CameraStreamRole.Main).Select(s => s.HealthReportedAt).FirstOrDefault(),
                p.Y, p.X, p.BearingDeg, p.FovDeg, p.Range,
            })
            .ToListAsync(ct);

        return new SiteMapDto(map.Id, map.Name, map.Kind.ToString(), map.CenterLat, map.CenterLng, map.Zoom, map.ImageWidth, map.ImageHeight,
            placements.Where(p => accessible is null || accessible.Contains(p.CameraId))
                .OrderBy(p => p.Name)
                .Select(p => new CameraPlacementDto(p.CameraId, p.Name, Status(p.IsEnabled, p.NodeId, p.Health, now),
                    p.Y, p.X, p.BearingDeg, p.FovDeg, p.Range))
                .ToList());
    }

    /// <summary>The dashboard's rules: disabled, recording (health reported within the fresh window),
    /// otherwise offline.</summary>
    internal static string Status(bool enabled, Guid? nodeId, DateTime? healthReportedAt, DateTime nowUtc) =>
        !enabled ? "disabled"
        : nodeId is not null && healthReportedAt is { } r && nowUtc - r < DashboardService.HealthFreshWindow ? "recording"
        : "offline";

    public async Task<(byte[] Image, string ContentType)?> GetImageAsync(Guid id, CancellationToken ct = default)
    {
        var image = await db.SiteMaps.AsNoTracking().Where(m => m.Id == id && m.Image != null)
            .Select(m => new { m.Image, m.ImageContentType }).FirstOrDefaultAsync(ct);
        return image is { Image: { } bytes } ? (bytes, image.ImageContentType ?? "application/octet-stream") : null;
    }

    public async Task<SiteMap> CreateAsync(CreateSiteMapRequest request, byte[]? image, CancellationToken ct = default)
    {
        var floorplan = string.Equals(request.Kind, nameof(SiteMapKind.Floorplan), StringComparison.OrdinalIgnoreCase);
        var map = new SiteMap
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Kind = floorplan ? SiteMapKind.Floorplan : SiteMapKind.Street,
            CenterLat = floorplan ? null : request.CenterLat,
            CenterLng = floorplan ? null : request.CenterLng,
            Zoom = floorplan ? null : request.Zoom,
            Image = floorplan ? image : null,
            ImageContentType = floorplan ? request.ImageContentType : null,
            ImageWidth = floorplan ? request.ImageWidth : null,
            ImageHeight = floorplan ? request.ImageHeight : null,
            SortOrder = await db.SiteMaps.CountAsync(ct),
        };
        db.SiteMaps.Add(map);
        await db.SaveChangesAsync(ct);
        return map;
    }

    public async Task<bool> UpdateAsync(Guid id, UpdateSiteMapRequest request, CancellationToken ct = default)
    {
        var map = await db.SiteMaps.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (map is null) return false;
        if (!string.IsNullOrWhiteSpace(request.Name)) map.Name = request.Name.Trim();
        // The view the map opens at for anyone without a remembered view of their own. For a floorplan
        // the same columns hold image coordinates (Leaflet CRS.Simple: y, x, and a zoom of -5 to 4).
        if (request.CenterLat is { } lat && request.CenterLng is { } lng && IsValidView(map.Kind, lat, lng, request.Zoom))
        {
            map.CenterLat = lat;
            map.CenterLng = lng;
            map.Zoom = request.Zoom ?? map.Zoom;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool IsValidView(SiteMapKind kind, double lat, double lng, int? zoom)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lng) || zoom is < -5 or > 22) return false;
        return kind != SiteMapKind.Street || (lat is >= -90 and <= 90 && lng is >= -180 and <= 180);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var map = await db.SiteMaps.Include(m => m.Placements).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (map is null) return false;
        db.SiteMaps.Remove(map);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SavePlacementAsync(Guid mapId, Guid cameraId, SavePlacementRequest request, CancellationToken ct = default)
    {
        if (!await db.SiteMaps.AnyAsync(m => m.Id == mapId, ct) || !await db.Cameras.AnyAsync(c => c.Id == cameraId, ct)) return false;
        var placement = await db.CameraPlacements.FirstOrDefaultAsync(p => p.MapId == mapId && p.CameraId == cameraId, ct);
        if (placement is null)
        {
            placement = new CameraPlacement { Id = Guid.NewGuid(), MapId = mapId, CameraId = cameraId };
            db.CameraPlacements.Add(placement);
        }
        placement.Y = request.Y;
        placement.X = request.X;
        placement.BearingDeg = ((request.BearingDeg % 360) + 360) % 360;
        placement.FovDeg = Math.Clamp(request.FovDeg, 5, 360);
        placement.Range = Math.Max(0.1, request.Range);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemovePlacementAsync(Guid mapId, Guid cameraId, CancellationToken ct = default)
    {
        var placement = await db.CameraPlacements.FirstOrDefaultAsync(p => p.MapId == mapId && p.CameraId == cameraId, ct);
        if (placement is null) return false;
        db.CameraPlacements.Remove(placement);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<MapTileSettingsDto> GetTileSettingsAsync(CancellationToken ct = default) => new(
        await settings.GetRawAsync(TileUrlKey, ct: ct) is { Length: > 0 } url ? url : DefaultTileUrl,
        await settings.GetRawAsync(TileAttributionKey, ct: ct) is { Length: > 0 } attribution ? attribution : DefaultAttribution);

    public async Task SaveTileSettingsAsync(MapTileSettingsDto value, string? modifiedBy, CancellationToken ct = default)
    {
        await settings.SetGlobalAsync(TileUrlKey, value.TileUrl.Trim(), modifiedBy, ct);
        await settings.SetGlobalAsync(TileAttributionKey, value.Attribution.Trim(), modifiedBy, ct);
    }
}

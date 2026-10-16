using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Maps (street or floorplan) and the cameras placed on them.</summary>
public interface IMapService
{
    /// <summary>Every map; CameraCount counts only cameras in accessible (null = all).</summary>
    Task<List<SiteMapSummaryDto>> ListAsync(IReadOnlySet<Guid>? accessible, CancellationToken ct = default);

    /// <summary>The map with its placements, narrowed to accessible cameras (null = all). Null if no such map.</summary>
    Task<SiteMapDto?> GetAsync(Guid id, IReadOnlySet<Guid>? accessible, CancellationToken ct = default);

    Task<(byte[] Image, string ContentType)?> GetImageAsync(Guid id, CancellationToken ct = default);

    Task<SiteMap> CreateAsync(CreateSiteMapRequest request, byte[]? image, CancellationToken ct = default);
    Task<bool> UpdateAsync(Guid id, UpdateSiteMapRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    Task<bool> SavePlacementAsync(Guid mapId, Guid cameraId, SavePlacementRequest request, CancellationToken ct = default);
    Task<bool> RemovePlacementAsync(Guid mapId, Guid cameraId, CancellationToken ct = default);

    Task<MapTileSettingsDto> GetTileSettingsAsync(CancellationToken ct = default);
    Task SaveTileSettingsAsync(MapTileSettingsDto settings, string? modifiedBy, CancellationToken ct = default);
}

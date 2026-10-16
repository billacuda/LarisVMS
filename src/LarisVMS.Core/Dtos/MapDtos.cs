namespace LarisVMS.Core.Dtos;

/// <summary>One map in the Map page's picker.</summary>
public record SiteMapSummaryDto(Guid Id, string Name, string Kind, double? CenterLat, double? CenterLng, int? Zoom,
    int? ImageWidth, int? ImageHeight, int CameraCount);

/// <summary>A map with the cameras on it this viewer may see. Status is "recording", "offline" or
/// "disabled", the same rules as the dashboard.</summary>
public record SiteMapDto(Guid Id, string Name, string Kind, double? CenterLat, double? CenterLng, int? Zoom,
    int? ImageWidth, int? ImageHeight, List<CameraPlacementDto> Placements);

public record CameraPlacementDto(Guid CameraId, string CameraName, string Status, double Y, double X,
    double BearingDeg, double FovDeg, double Range);

/// <summary>New map. Floorplans send their image as base64 (PNG, JPEG, WebP or GIF, up to 10 MB) with
/// its pixel size; street maps send where they should open.</summary>
public record CreateSiteMapRequest(string Name, string Kind, double? CenterLat = null, double? CenterLng = null, int? Zoom = null,
    string? ImageBase64 = null, string? ImageContentType = null, int? ImageWidth = null, int? ImageHeight = null);

/// <summary>Rename a map, or (street maps) save where it opens.</summary>
public record UpdateSiteMapRequest(string? Name = null, double? CenterLat = null, double? CenterLng = null, int? Zoom = null);

public record SavePlacementRequest(double Y, double X, double BearingDeg, double FovDeg, double Range);

/// <summary>The street-map tile server: a {z}/{x}/{y} URL template and the attribution its terms require.</summary>
public record MapTileSettingsDto(string TileUrl, string Attribution);

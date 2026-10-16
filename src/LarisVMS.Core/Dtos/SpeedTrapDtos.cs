namespace LarisVMS.Core.Dtos;

/// <summary>A speed trap as the editor (speed-trap-editor.js) sees it. GatesJson is
/// <c>{"a":[[x,y],[x,y]],"b":[[x,y],[x,y]]}</c>, normalized 0-1.</summary>
public record SpeedTrapDto(Guid Id, Guid CameraId, string Name, string GatesJson, double DistanceMeters, bool IsEnabled,
    double? SpeedLimitKph = null);

/// <summary>Body of POST /api/cameras/{id}/speed-traps and PUT /api/speed-traps/{id}. Distance is
/// always metres and the limit always km/h; the editor converts before sending. A null limit means
/// none.</summary>
public record SaveSpeedTrapRequest(string Name, string GatesJson, double DistanceMeters, bool IsEnabled = true,
    double? SpeedLimitKph = null);

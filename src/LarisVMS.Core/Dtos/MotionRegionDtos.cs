namespace LarisVMS.Core.Dtos;

/// <summary>Detection/hardware-acceleration overhaul pass 3c-2: GET /api/cameras/{id}/motion-region
/// response — read by both the Zones and MotionGrid editors so each can show a banner when it isn't
/// the currently active method. Mode as a string, same SaveZoneRequest/ZoneDto convention (no
/// [JsonConverter] on the enum, no global JsonStringEnumConverter registered anywhere in this app).</summary>
public record MotionRegionDto(string Mode, int GridSize, string? GridMask, double GridSensitivity);

/// <summary>PUT /api/cameras/{id}/motion-region/mode body.</summary>
public record SetMotionRegionModeRequest(string Mode);

/// <summary>PUT /api/cameras/{id}/motion-region/grid body — always the full size/mask/sensitivity
/// triple, even when only one actually changed (a cell toggle re-sends the same size/sensitivity it
/// already had; a size change always arrives paired with an already-cleared mask). One request shape
/// covers every save the MotionGrid editor ever makes.</summary>
public record SaveMotionGridRequest(int GridSize, string? Mask, double Sensitivity);

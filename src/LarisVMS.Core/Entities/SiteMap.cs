namespace LarisVMS.Core.Entities;

public enum SiteMapKind
{
    /// <summary>A street map (Leaflet tiles, OpenStreetMap by default); positions are latitude/longitude.</summary>
    Street = 0,
    /// <summary>An uploaded floorplan or site-plan image; positions are pixels in that image.</summary>
    Floorplan = 1,
}

/// <summary>A map cameras are placed on (the Map page). Street maps remember where they open
/// (CenterLat/CenterLng/Zoom); floorplans carry their image, stored here rather than on disk so a
/// database backup includes it.</summary>
public class SiteMap
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SiteMapKind Kind { get; set; }

    public double? CenterLat { get; set; }
    public double? CenterLng { get; set; }
    public int? Zoom { get; set; }

    public byte[]? Image { get; set; }
    public string? ImageContentType { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }

    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<CameraPlacement> Placements { get; set; } = [];
}

/// <summary>Where one camera sits on one map and what it sees. Y/X are latitude/longitude on a street
/// map and image pixels (Leaflet's simple CRS: Y up from the bottom edge) on a floorplan. BearingDeg is
/// the direction the camera faces, clockwise from north (or from the top of the image); FovDeg its
/// horizontal field of view; Range how far it sees — metres on a street map, image pixels on a
/// floorplan. A camera can be on several maps, once each.</summary>
public class CameraPlacement
{
    public Guid Id { get; set; }
    public Guid MapId { get; set; }
    public Guid CameraId { get; set; }

    public double Y { get; set; }
    public double X { get; set; }
    public double BearingDeg { get; set; }
    public double FovDeg { get; set; } = 90;
    public double Range { get; set; } = 30;

    public SiteMap Map { get; set; } = null!;
    public Camera Camera { get; set; } = null!;
}

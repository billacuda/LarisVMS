namespace LarisVMS.Core.Entities;

/// <summary>
/// A speed trap on one camera: two gate lines drawn across the direction of travel and the
/// real-world distance between them. Vision times each tracked object between crossing gate A and
/// gate B (either order) and reports distance / time. Separate from <see cref="Zone"/> on purpose —
/// it's two lines and a distance, not a polygon, and nothing that reads zones should see it.
/// </summary>
public class SpeedTrap
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Both gate lines as fractions of frame width/height (0.0-1.0), the same coordinate
    /// space as Zone.PolygonJson: <c>{"a":[[x,y],[x,y]],"b":[[x,y],[x,y]]}</c>.</summary>
    public string GatesJson { get; set; } = string.Empty;

    /// <summary>Distance between the two gates along the direction of travel, in metres. The editor
    /// accepts feet and converts.</summary>
    public double DistanceMeters { get; set; }

    /// <summary>Optional speed limit for this stretch of road, in km/h (the editor accepts the
    /// display unit and converts). Drives "over the limit" counts and highlighting; null = none.</summary>
    public double? SpeedLimitKph { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Camera Camera { get; set; } = null!;
}

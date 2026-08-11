using NidusVMS.Core.Enums;

namespace NidusVMS.Core.Entities;

/// <summary>
/// A polygon region on one camera's frame. One editor handles all four <see cref="ZoneKind"/>s
/// (see plan §M8) — what each kind actually *does* varies: ServerMotion and Ignore feed the node's
/// substream frame-diff pipeline (MotionDetector), CameraMotion and Privacy are stored for a
/// follow-up pass (device push, transcode-path burn-in) that hasn't landed yet.
/// </summary>
public class Zone
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }

    public string Name { get; set; } = string.Empty;
    public ZoneKind Kind { get; set; }

    /// <summary>Polygon vertices as fractions of frame width/height (0.0-1.0), not pixels — the
    /// same shape survives a camera's stream resolution changing (re-probe, firmware update)
    /// without every stored zone needing to be redrawn. <c>{"points":[[x,y],[x,y],...]}</c>.</summary>
    public string PolygonJson { get; set; } = string.Empty;

    /// <summary>0.0-1.0. Only meaningful for ServerMotion — the fraction of unmasked pixels inside
    /// the polygon that must change beyond MotionDetector's per-pixel delta threshold for a frame to
    /// count as motion. Higher = less sensitive. Not resolved through ISettingsResolver — this is
    /// a per-row default applied once at zone creation, not a live global/node/camera fallback.
    ///
    /// 0.03, not the 0.15 this originally shipped with: verified against real captured frames from
    /// a real outdoor camera (a wide zone covering ~73% of frame) that 0.15 was unreachable in
    /// practice — the highest score observed across dozens of real frame comparisons, including
    /// visible compression-artifact spikes, was 0.0004. A zone covering most of the frame needs
    /// well over 10,000 pixels to change simultaneously in one ~200ms interval to reach 15%, which
    /// is a much higher bar than a person or vehicle crossing a wide scene actually produces.
    /// 0.03 is comfortably above the measured noise floor while still realistically reachable.
    /// Changing this default does not retroactively update already-saved zones — their Sensitivity
    /// was copied into the row at creation time and stays whatever it was set to.</summary>
    public double Sensitivity { get; set; } = 0.03;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Null until a follow-up pass implements the CameraMotion device push (ONVIF
    /// SetVideoAnalyticsConfiguration) — see ZoneKind.CameraMotion's doc comment.</summary>
    public DateTime? PushedToCameraAt { get; set; }

    public Camera Camera { get; set; } = null!;
}

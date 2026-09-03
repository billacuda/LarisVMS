namespace LarisVMS.Node;

/// <summary>
/// Corrects a camera's reported watch-stream dimensions against an operator-set orientation, before
/// they reach LarisVMS.Vision.Inference.InferenceProfile. Mirrors DetectionModelSelection's shape: a
/// small, pure, no-I/O choice resolved once per reconcile.
///
/// Why this exists: ONVIF is not always honest about a portrait/corridor-mounted camera. A device can
/// advertise a 704x480 Sub profile and then deliver 480x704 on the wire (confirmed on Amcrest). The
/// profile is built from the advertised pair, so ffmpeg is told to scale into a landscape box and
/// squashes a genuinely portrait frame to fit — the model then runs on a horizontally-stretched image
/// and every snapshot crop comes off that same distorted buffer.
///
/// This is deliberately an operator setting rather than a measurement fed back from the Vision
/// Service. 0.172.0 tried the measurement route (VisionStreamInfoReport → persist to
/// CameraStream.Width/Height → restart the watch) and it could not hold: CameraService's own
/// ReplaceStreamsAsync overwrites those columns from ONVIF on every re-probe, and a re-probe runs on
/// every web app restart, so the corrected value was reverted and the watch restarted in a loop —
/// discarding that camera's ByteTrack state each time. A camera-scoped SettingOverride row is not
/// something a re-probe touches, so the correction is stable by construction.
/// </summary>
public static class DetectionOrientation
{
    public const string Auto = "Auto";
    public const string Landscape = "Landscape";
    public const string Portrait = "Portrait";

    /// <summary>
    /// Returns the dimensions the detection profile should be built from. Only ever swaps the pair —
    /// InferenceProfile uses these purely as an aspect ratio, so orienting the reported numbers is the
    /// whole correction; there is no second number to invent.
    ///
    /// An unrecognized value (a hand-edited Setting row, a newer server talking to an older node)
    /// falls through to <see cref="Auto"/> rather than throwing — the same "unknown means today's
    /// behavior" default DetectionModelSelection and MotionRegionMode parsing already take.
    /// </summary>
    public static (int Width, int Height) Apply(string? orientation, int width, int height)
    {
        if (width <= 0 || height <= 0) return (width, height);

        return orientation switch
        {
            not null when orientation.Equals(Portrait, StringComparison.OrdinalIgnoreCase)
                => width > height ? (height, width) : (width, height),
            not null when orientation.Equals(Landscape, StringComparison.OrdinalIgnoreCase)
                => height > width ? (height, width) : (width, height),
            _ => (width, height),
        };
    }
}

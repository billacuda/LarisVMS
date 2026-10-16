namespace LarisVMS.Core.Interfaces;

/// <summary>One object inside a <see cref="DetectionEvent"/>, as last recorded. Box is the object's
/// best frame, normalized 0-1.</summary>
public record DetectionEventObject(int TrackId, string Label,
    double? X, double? Y, double? W, double? H,
    string? PrimaryColor, string? UpperColor, string? LowerColor,
    double? SpeedKph, Guid? SpeedTrapId);

/// <summary>An AI-detection span as it stands right after NodeService recorded a report for it —
/// sent on every report (open, checkpoint, close), not just the first, because a color or speed
/// can arrive on a later one. Consumers keep track of what they've already acted on.</summary>
public record DetectionEvent(long SpanId, Guid CameraId, string Category, string Label,
    DateTime StartUtc, DateTime EndUtc,
    double? BoxX, double? BoxY, double? BoxW, double? BoxH, double? Confidence,
    IReadOnlyList<DetectionEventObject> Objects);

/// <summary>Where NodeService hands each recorded AI detection for real-time processing (detection
/// alerts). Must not block: implementations queue and return.</summary>
public interface IDetectionEventSink
{
    void Publish(DetectionEvent detection);
}

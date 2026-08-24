namespace LarisVMS.Node;

/// <summary>Bookkeeping for one camera's AI detection watch, started via
/// LarisVMS.Vision.Service's own /cameras/{id}/start control-API call — mirrors
/// CameraIntegrationRecorder's shape, but this process holds no long-running session object of its
/// own for AI detection; the actual pipeline lives entirely inside the sibling Vision Service
/// process. ConfigSignature lets Reconcile tell "already watching with this exact configuration,
/// nothing to do" from "the camera's RTSP URI or detection settings changed, re-issue the start
/// call" without Vision Service itself needing to compare anything.</summary>
public sealed class CameraVisionRecorder(string configSignature)
{
    public string ConfigSignature { get; } = configSignature;

    private DateTime? _lastReportEndUtc;
    private DateTime? _lastReportReceivedUtc;

    /// <summary>Called whenever a VisionDetectionReportItem arrives for this camera (checkpoint or
    /// close alike). EndUtc only ever moves forward, the same "extends, never rewinds" reasoning
    /// NodeService.RecordMotionSpansAsync's own upsert already applies.</summary>
    public void RecordDetection(DateTime endUtc)
    {
        if (_lastReportEndUtc is null || endUtc > _lastReportEndUtc) _lastReportEndUtc = endUtc;
        _lastReportReceivedUtc = DateTime.UtcNow;
    }

    /// <summary>Object detection plan decision 9's AI-Moving OR-term (once DecideGatedSegment is
    /// updated to consult it — not wired in yet, tracked here so the data is already available when
    /// it is).</summary>
    public bool AnyDetectionSince(DateTime thresholdUtc) => _lastReportEndUtc is { } t && t >= thresholdUtc;

    /// <summary>Proxy for "AI detection is currently seeing something on this camera." There's no
    /// live session object to query the way DahuaCgiEventSession.AnyDetectionActive has — Vision
    /// Service's own MotionHysteresis instances live in a separate process — so a report received
    /// within Vision Service's own checkpoint cadence (~15s) is treated as still active, the same
    /// recency-window idea MotionHysteresis.CurrentInProgressSpan itself already relies on.</summary>
    public bool AnyDetectionActive => _lastReportReceivedUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(20);
}

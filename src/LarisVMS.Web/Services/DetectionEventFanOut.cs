using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>Hands each detection and motion span NodeService reports to every listener (detection
/// alerts, Home Assistant). Each listener only queues, so this never blocks recording.</summary>
public sealed class DetectionEventFanOut(params IDetectionEventSink[] sinks) : IDetectionEventSink
{
    public void Publish(DetectionEvent detection)
    {
        foreach (var sink in sinks) sink.Publish(detection);
    }

    public void PublishMotion(MotionEvent motion)
    {
        foreach (var sink in sinks) sink.PublishMotion(motion);
    }
}

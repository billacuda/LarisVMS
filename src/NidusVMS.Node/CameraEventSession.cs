using Microsoft.Extensions.Logging;
using NidusVMS.Core;
using NidusVMS.Media;
using NidusVMS.Onvif.Clients;
using NidusVMS.Onvif.Soap;

namespace NidusVMS.Node;

/// <summary>M8 pass 6: one raw ONVIF PullPoint notification, already classified — mirrors
/// MotionSpanReportItem/StreamInfoReportItem's shape (the event, not yet wrapped with the CameraId
/// only NodeWorker's wiring closure knows) rather than exposing OnvifNotificationMessage directly,
/// since PayloadJson is a NodeWorker/reporting concern, not something CameraEventSession's caller
/// needs to re-derive from SimpleItems itself.</summary>
public sealed record CameraEventObserved(string Topic, DateTime UtcTime, string? PayloadJson, bool IsMotion);

/// <summary>
/// Polls one camera's ONVIF PullPoint subscription and turns motion-classified notifications into
/// MotionSpans, reusing <see cref="MotionHysteresis"/> — the exact primitive MotionSession already
/// uses per zone — rather than a bespoke open/close state machine. One instance per camera, not per
/// zone: an ONVIF camera-pushed event has no concept of one of our own drawn ServerMotion zones, so
/// MotionSpans it produces carry a null ZoneId (see MotionSpanReportItem's doc comment).
///
/// No debounce on the opening edge (startAfter: TimeSpan.Zero) — the camera's own analytics/alarm
/// stack has already made the "is this really motion" call; there's nothing to confirm on this side
/// the way a raw per-frame pixel-diff tick needs. A couple of seconds on the closing edge absorbs a
/// quick True/False flicker without producing back-to-back spans for what's really one event.
///
/// Same supervision shape as RecordingSession/MotionSession — a long-lived RunAsync loop with its
/// own backoff, started/stopped entirely by NodeWorker's reconcile cycle — but far lighter weight:
/// no ffmpeg process, just an HTTP long-poll against the camera's Events service.
/// </summary>
public sealed class CameraEventSession(
    Uri eventsServiceUri, OnvifCredentials? credentials, OnvifEventsClient client, ILogger logger)
{
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(30);
    private const int PullMessageLimit = 50;

    private readonly MotionHysteresis _hysteresis = new(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(2));

    /// <summary>Fires for every notification, motion-classified or not — NodeWorker's wiring
    /// closure is what turns this into a queued CameraEventReportItem.</summary>
    public event Action<CameraEventObserved>? EventObserved;

    /// <summary>Fires only when a motion-classified run of notifications closes (the camera reports
    /// State=false, or has held quiet long enough — see the class doc comment's endAfter). Same
    /// shape/consumer as MotionSession.MotionSpanCompleted.</summary>
    public event Action<MotionSpanResult>? MotionSpanCompleted;

    /// <summary>Whether this camera has reported motion at or after <paramref name="thresholdUtc"/>
    /// — the same query MotionSession.HasMotionSince answers for server-side detection, checked
    /// alongside it by NodeWorker.DecideMotionSegment so a camera relying only on its own onboard
    /// detection (no ServerMotion zone configured at all) still gates its Motion-mode recording
    /// correctly, not just a zone-detected one.
    ///
    /// On its own this is NOT enough for a camera-pushed span, unlike a ServerMotion zone: frame-diff
    /// ticks arrive every ~200ms while motion is genuinely ongoing, so LastMotionAtUtc is always
    /// fresh for as long as a zone stays active. Many real ONVIF implementations instead send exactly
    /// one notification on each edge — a single "State=true" when motion starts, nothing again until
    /// "State=false" when it stops, no periodic "still active" heartbeat in between. For a long event
    /// like that, LastMotionAtUtc goes stale relative to a segment decided well into the gap even
    /// though motion never actually ended — see IsMotionActive, which NodeWorker checks alongside
    /// this specifically to cover that case.</summary>
    public bool HasMotionSince(DateTime thresholdUtc) =>
        _hysteresis.LastMotionAtUtc is { } t && t >= thresholdUtc;

    /// <summary>Whether a motion span is *currently open* — a rising (State=true) notification has
    /// been observed and no falling (State=false) one has arrived yet. Deliberately has no timeout or
    /// recency check of its own: this only ever becomes false when an actual falling-edge notification
    /// is observed (or Flush runs at shutdown) — never just because it's been a while since the last
    /// notification. NodeWorker.DecideMotionSegment ORs this with HasMotionSince so a segment
    /// completing in the middle of a long, sparsely-reported event is still correctly kept: recording
    /// keeps being retained for as long as the camera hasn't said motion stopped, full stop.</summary>
    public bool IsMotionActive => _hysteresis.IsActive;

    /// <summary>Snapshot of the currently open span (if any), for periodic timeline checkpointing —
    /// see NodeWorker.EnqueueMotionCheckpoints. Unlike MotionSession's per-zone equivalent
    /// (MotionHysteresis.CurrentInProgressSpan's own recency window, sized for continuous 5fps
    /// frame-diff ticks), this passes an effectively unlimited recency: a camera-pushed span can
    /// legitimately go a long stretch between notifications while still genuinely open (see
    /// IsMotionActive), so gating the checkpoint on recency would hide exactly the long-running
    /// events most worth seeing on the timeline while they're still in progress.</summary>
    public MotionSpanResult? CurrentInProgressSpan(DateTime nowUtc) =>
        _hysteresis.CurrentInProgressSpan(nowUtc, TimeSpan.MaxValue);

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            Uri? pullPoint;
            try
            {
                pullPoint = await client.CreatePullPointSubscriptionAsync(eventsServiceUri, credentials, SubscriptionLifetime, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pullPoint = null;
                logger.LogWarning(ex, "Could not open a PullPoint subscription — will retry.");
            }

            if (pullPoint is null)
            {
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                continue;
            }
            backoff = TimeSpan.FromSeconds(5);
            logger.LogInformation("Subscribed for ONVIF events.");

            // Pulls against this one subscription until it faults (device rebooted, subscription
            // expired server-side, network blip) — then loops back out to open a fresh one.
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<OnvifNotificationMessage> messages;
                try
                {
                    messages = await client.PullMessagesAsync(pullPoint, PullTimeout, PullMessageLimit, credentials, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "PullMessages failed — re-subscribing.");
                    break;
                }

                foreach (var msg in messages)
                {
                    var eventTime = msg.UtcTime ?? DateTime.UtcNow;
                    var isMotion = CameraEventClassifier.IsMotionActive(msg.Topic, msg.SimpleItems);
                    var payloadJson = msg.SimpleItems.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(msg.SimpleItems)
                        : null;
                    EventObserved?.Invoke(new CameraEventObserved(msg.Topic, eventTime, payloadJson, isMotion));

                    var completed = _hysteresis.Observe(eventTime, isMotion, score: 1.0);
                    if (completed is not null) MotionSpanCompleted?.Invoke(completed);
                }
            }
        }

        // Same shutdown shape as MotionSession.RunAsync's own FlushAll — an in-progress span (the
        // camera said motion started but hasn't yet said it stopped) is closed at whatever "now"
        // actually is rather than silently dropped when this loop ends (cancellation, or NodeWorker
        // stopping this session because the camera was reassigned/removed).
        var flushed = _hysteresis.Flush(DateTime.UtcNow);
        if (flushed is not null) MotionSpanCompleted?.Invoke(flushed);
    }
}

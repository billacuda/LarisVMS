using Microsoft.Extensions.Logging;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Media;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Node;

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
/// No debounce on either edge (startAfter/endAfter both TimeSpan.Zero) — the camera's own
/// analytics/alarm stack has already made the "is this really motion" call and already debounces its
/// own flicker; there's nothing left to confirm on this side the way a raw per-frame pixel-diff tick
/// needs. A nonzero endAfter was tried here first (2s, "absorb a quick flicker") and turned out to be
/// actively wrong for this event-driven feed, not just unnecessary: MotionHysteresis.Observe only
/// evaluates its close condition on a *later* Observe call (the falling notification itself always
/// computes zero elapsed against the quietSince it just set), which works for a continuously-polled
/// source like MotionSession's ~200ms frame ticks but starves indefinitely here — confirmed against
/// real production CameraEvents data where a camera's own true/false pairs recur every 5-120s with no
/// third, unrelated notification landing in between to supply that "later" tick. The practical result
/// was recording that never stopped: IsMotionActive stayed true for hours despite a clean falling edge
/// being present in the log every single time. Zero sidesteps the whole mechanism — the elapsed-since
/// check always evaluates true on the very first (and only) false call, closing the span in the same
/// notification that reported it, matching the closing-edge behavior a user explicitly asked for.
///
/// Same supervision shape as RecordingSession/MotionSession — a long-lived RunAsync loop with its
/// own backoff, started/stopped entirely by NodeWorker's reconcile cycle — but far lighter weight:
/// no ffmpeg process, just an HTTP long-poll against the camera's Events service.
/// </summary>
public sealed class CameraEventSession(
    Uri eventsServiceUri, OnvifCredentials? credentials, OnvifEventsClient client, ILogger logger,
    IReadOnlyList<NodeConfigEventTagRuleDto>? rules = null)
{
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(30);
    private const int PullMessageLimit = 50;

    private readonly MotionHysteresis _hysteresis = new(startAfter: TimeSpan.Zero, endAfter: TimeSpan.Zero);

    // M8 pass 8/9: one hysteresis per configured EventTagRule, independent of the built-in
    // classifier's own _hysteresis above and of each other — a rule's StartTopic/StopTopic pair has
    // nothing to do with whether the built-in motion-family topics also fired, and one rule's edges
    // must never affect another rule's state. Both two-topic and toggle rules use endAfter: Zero now
    // — a toggle rule reads its own State/IsMotion/Motion item off its one topic exactly like the
    // built-in classifier does above, so it inherited the exact same starvation bug that fix closes;
    // there's no reason for the two modes to disagree on debounce once neither one actually debounces.
    private readonly Dictionary<Guid, MotionHysteresis> _ruleHysteresis =
        (rules ?? []).ToDictionary(r => r.Id, r => new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.Zero));

    private readonly IReadOnlyList<NodeConfigEventTagRuleDto> _rules = rules ?? [];

    // Logged at most once per session — a skewed camera clock produces a notification every few
    // seconds, and this is a standing configuration problem, not a per-event incident.
    private bool _warnedClockSkew;

    /// <summary>How far a notification's own UtcTime attribute may differ from this node's clock
    /// before it stops being trusted. Generous on purpose: normal PullMessages delivery latency and
    /// ordinary NTP jitter are sub-second, so anything beyond a minute is a genuinely wrong value
    /// rather than a timing artifact, and clamping tighter would start rejecting good
    /// timestamps.</summary>
    internal static readonly TimeSpan MaxTrustedClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>Which clock a notification is stamped with: the UtcTime the camera put on the
    /// message when it's close enough to this node's clock to be believable, and the node's receive
    /// time otherwise.
    ///
    /// This exists because MotionSpans and Segments MUST share one timebase. Segments are stamped by
    /// ffmpeg on the node; MotionSpans were stamped with whatever the message claimed. Measured
    /// against this deployment, that claim is unreliable — and specifically NOT because the cameras'
    /// clocks are wrong. Their displayed time and NTP sync are both correct; the ONVIF layer is what
    /// misformats. Proven from the cameras' own data: a LastClockSynchronization notification carries
    /// a timestamp in its payload AND one in the message's UtcTime attribute, and on four of six
    /// cameras those two disagree by exactly 60.00 minutes, dead constant across 35+ samples with
    /// zero variance. Clock drift is never exactly an hour with no variance — that signature is a
    /// daylight-saving conversion bug, the camera turning its correct local time into "UTC" with the
    /// standard offset instead of the current DST one. It lands differently per unit (firmware
    /// version, most likely): on two cameras the UtcTime attribute carries the hour error and so gets
    /// ingested, on the rest it doesn't.
    ///
    /// Where it did get ingested, two consequences that were reported as separate-looking bugs:
    /// motion was drawn an hour to the right of the footage that actually contained it (green with no
    /// recording under it in one place, recording with no green on it an hour earlier), and
    /// Motion-mode gating never discarded anything, because NodeWorker.DecideMotionSegment compares
    /// LastMotionAtUtc against a window built from the segment's node-clock time — a timestamp an
    /// hour in the future satisfies any such window, permanently. Anchoring to receive time makes
    /// both correct by construction and costs only the sub-second delivery latency, far below the
    /// pre/post-roll windows this feeds. A camera whose UtcTime is actually right keeps using it.</summary>
    internal static DateTime ResolveEventTimestamp(DateTime? cameraReportedUtc, DateTime receivedAtUtc) =>
        cameraReportedUtc is { } reported && (reported - receivedAtUtc).Duration() <= MaxTrustedClockSkew
            ? reported
            : receivedAtUtc;

    /// <summary>Fires for every notification, motion-classified or not — NodeWorker's wiring
    /// closure is what turns this into a queued CameraEventReportItem.</summary>
    public event Action<CameraEventObserved>? EventObserved;

    /// <summary>Fires only when a motion-classified run of notifications closes (the camera reports
    /// State=false, or has held quiet long enough — see the class doc comment's endAfter). Same
    /// shape/consumer as MotionSession.MotionSpanCompleted.</summary>
    public event Action<MotionSpanResult>? MotionSpanCompleted;

    /// <summary>Fires when a configured EventTagRule's own span closes — same
    /// (identifier, result) shape as MotionSession.MotionSpanCompleted's (zoneId, result), just keyed
    /// by EventTagRuleId instead of ZoneId.</summary>
    public event Action<Guid, MotionSpanResult>? RuleSpanCompleted;

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

    /// <summary>Per-rule equivalent of HasMotionSince — whether the given rule has reported activity
    /// at or after thresholdUtc. Unknown ruleId (never configured, or removed since this session
    /// started) answers false rather than throwing, same "safe to over-ask" shape as the rest of this
    /// class's query surface.</summary>
    public bool RuleHasMotionSince(Guid ruleId, DateTime thresholdUtc) =>
        _ruleHysteresis.TryGetValue(ruleId, out var h) && h.LastMotionAtUtc is { } t && t >= thresholdUtc;

    /// <summary>Per-rule equivalent of IsMotionActive — see that property's doc comment for why this
    /// has no timeout of its own either.</summary>
    public bool IsRuleActive(Guid ruleId) =>
        _ruleHysteresis.TryGetValue(ruleId, out var h) && h.IsActive;

    /// <summary>Per-rule equivalent of CurrentInProgressSpan, for checkpointing every configured
    /// rule's own currently-open span (if any) — see EnqueueMotionCheckpoints.</summary>
    public IEnumerable<(Guid RuleId, MotionSpanResult Span)> CurrentInProgressRuleSpans(DateTime nowUtc)
    {
        foreach (var (ruleId, h) in _ruleHysteresis)
        {
            var span = h.CurrentInProgressSpan(nowUtc, TimeSpan.MaxValue);
            if (span is not null) yield return (ruleId, span);
        }
    }

    /// <summary>Whether any rule with DrivesRecording=true has reported activity at or after
    /// thresholdUtc — a purely-tagging rule (DrivesRecording=false) never contributes to
    /// NodeWorker.DecideMotionSegment's keep/discard decision, only to the timeline's color, exactly
    /// the way an Ignore zone never contributes to a ServerMotion keep decision either.</summary>
    public bool AnyDrivingRuleHasMotionSince(DateTime thresholdUtc) =>
        _rules.Any(r => r.DrivesRecording && RuleHasMotionSince(r.Id, thresholdUtc));

    /// <summary>Whether any rule with DrivesRecording=true currently has an open span — see
    /// IsMotionActive's doc comment for why this, not just AnyDrivingRuleHasMotionSince, is what
    /// keeps a long sparsely-reported custom-tag event's recording retained with no timeout.</summary>
    public bool AnyDrivingRuleActive =>
        _rules.Any(r => r.DrivesRecording && IsRuleActive(r.Id));

    /// <summary>Pure classification of one notification against one rule — true/false/null for
    /// rising/falling/irrelevant, pulled out of the RunAsync loop so it's directly unit-testable the
    /// same way CameraEventClassifier.IsMotionActive is, rather than only reachable through a live
    /// PullPoint loop. Two-topic mode (StopTopic set): StartTopic is always rising, StopTopic is
    /// always falling, regardless of any state item the notification happens to carry — the two
    /// topics ARE the edges, by construction. Toggle mode (StopTopic null): only StartTopic is
    /// relevant at all, and the edge is whatever CameraEventClassifier.TryGetBooleanState reads off
    /// that one notification's own payload.</summary>
    internal static bool? ClassifyRuleEdge(NodeConfigEventTagRuleDto rule, string? topic, IReadOnlyDictionary<string, string> simpleItems)
    {
        if (rule.StopTopic is null)
        {
            return string.Equals(topic, rule.StartTopic, StringComparison.Ordinal)
                ? CameraEventClassifier.TryGetBooleanState(simpleItems)
                : null;
        }

        if (string.Equals(topic, rule.StartTopic, StringComparison.Ordinal)) return true;
        if (string.Equals(topic, rule.StopTopic, StringComparison.Ordinal)) return false;
        return null;
    }

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

                // One receive stamp for the whole batch — these genuinely did all arrive together,
                // and re-reading the clock per message would invent sub-millisecond ordering that
                // carries no real information.
                var receivedAtUtc = DateTime.UtcNow;
                foreach (var msg in messages)
                {
                    var eventTime = ResolveEventTimestamp(msg.UtcTime, receivedAtUtc);
                    if (!_warnedClockSkew && msg.UtcTime is { } claimed && eventTime != claimed)
                    {
                        _warnedClockSkew = true;
                        logger.LogWarning(
                            "This camera's ONVIF event timestamps are {SkewMinutes:F1} minute(s) away from this " +
                            "node's clock (message says {CameraTime:u}, node says {NodeTime:u}) — using receive " +
                            "time instead, so motion lines up with recordings. An offset of almost exactly one " +
                            "hour is a daylight-saving conversion bug in the camera's ONVIF layer, not a wrong " +
                            "clock: the camera's own displayed time and NTP sync can both be correct while this " +
                            "field is still an hour out.",
                            (claimed - receivedAtUtc).TotalMinutes, claimed, receivedAtUtc);
                    }

                    var isMotion = CameraEventClassifier.IsMotionActive(msg.Topic, msg.SimpleItems);
                    var payloadJson = msg.SimpleItems.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(msg.SimpleItems)
                        : null;
                    EventObserved?.Invoke(new CameraEventObserved(msg.Topic, eventTime, payloadJson, isMotion));

                    var completed = _hysteresis.Observe(eventTime, isMotion, score: 1.0);
                    if (completed is not null) MotionSpanCompleted?.Invoke(completed);

                    // M8 pass 8: each configured rule only ever gets fed on a notification that's
                    // actually relevant to it — see the _ruleHysteresis field doc comment for why an
                    // unrelated topic must never count as this rule's own falling edge.
                    foreach (var rule in _rules)
                    {
                        var edge = ClassifyRuleEdge(rule, msg.Topic, msg.SimpleItems);
                        if (edge is null) continue;


                        var ruleCompleted = _ruleHysteresis[rule.Id].Observe(eventTime, edge.Value, score: 1.0);
                        if (ruleCompleted is not null) RuleSpanCompleted?.Invoke(rule.Id, ruleCompleted);
                    }
                }
            }
        }

        // Same shutdown shape as MotionSession.RunAsync's own FlushAll — an in-progress span (the
        // camera said motion started but hasn't yet said it stopped) is closed at whatever "now"
        // actually is rather than silently dropped when this loop ends (cancellation, or NodeWorker
        // stopping this session because the camera was reassigned/removed).
        var flushed = _hysteresis.Flush(DateTime.UtcNow);
        if (flushed is not null) MotionSpanCompleted?.Invoke(flushed);

        var flushNow = DateTime.UtcNow;
        foreach (var (ruleId, h) in _ruleHysteresis)
        {
            var ruleFlushed = h.Flush(flushNow);
            if (ruleFlushed is not null) RuleSpanCompleted?.Invoke(ruleId, ruleFlushed);
        }
    }
}

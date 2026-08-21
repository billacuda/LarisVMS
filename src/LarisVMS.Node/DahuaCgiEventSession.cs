using System.Net;
using Microsoft.Extensions.Logging;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>
/// Node-side half of the Dahua/Amcrest plugin (<see cref="DahuaCgiIntegrationProvider"/>): holds one
/// long-lived HTTP connection to the camera's own CGI event API and turns its person/vehicle
/// detections into the same DetectionKind spans the ONVIF path already produces.
///
/// This exists because these cameras genuinely do classify objects onboard but never publish that
/// over ONVIF — proven against this fleet, where 21,747 ONVIF events contained nothing but plain
/// motion while SMD was switched on the whole time.
///
/// Shaped deliberately like <see cref="CameraEventSession"/>: same per-kind MotionHysteresis, same
/// DetectionSpanCompleted event, same shutdown flush, same "supervise a long-running loop with
/// backoff" contract NodeWorker already knows how to start and stop. That symmetry is what lets
/// NodeWorker treat a plugin session as just another event source rather than a special case, and
/// lets both sources feed one another's spans without either knowing the other exists.
///
/// The transport is a never-ending multipart/x-mixed-replace body, so this reads lines forever
/// rather than awaiting a response that completes. All grammar/classification lives in
/// <see cref="DahuaCgiEventParser"/> (pure, unit-tested) — this file is only connection lifecycle.
/// </summary>
public sealed class DahuaCgiEventSession(
    Uri baseUri, string? username, string? password, ILogger logger, Func<HttpMessageHandler>? handlerFactory = null)
{
    /// <summary>How long a silent connection is given before it's assumed dead and reconnected.
    ///
    /// This was 3 minutes, on the stated assumption that "the camera sends a keep-alive comment line
    /// every ~30s". Confirmed live that it does not — not on this subscription — so every camera in
    /// the fleet was reconnecting every 3 minutes indefinitely, healthy or not. That matters beyond
    /// log noise: attach only delivers events from the moment it subscribes, so each reconnect is a
    /// few seconds in which a detection is lost with no trace.
    ///
    /// Two changes address it together: the subscription now includes a keep-alive code
    /// (DahuaCgiEventParser.KeepAliveCodes) so an active camera keeps the connection visibly alive,
    /// and this backstop is longer, since it now only has to catch a socket that died without
    /// signalling — which a read error or EOF usually surfaces on its own anyway. Long enough not to
    /// churn on a genuinely quiet camera, short enough that a silently-dead socket still recovers
    /// without operator intervention.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(15);

    private readonly Dictionary<DetectionKind, MotionHysteresis> _hysteresis = [];

    /// <summary>Fires when a detection span closes — identical shape to
    /// CameraEventSession.DetectionSpanCompleted, so NodeWorker wires both the same way.</summary>
    public event Action<DetectionKind, MotionSpanResult>? DetectionSpanCompleted;

    /// <summary>Same role as CameraEventSession.AnyDetectionSince/AnyDetectionActive — ORed into
    /// Motion-mode's keep decision so a person detected only by this plugin still retains footage.</summary>
    public bool AnyDetectionSince(DateTime thresholdUtc) =>
        _hysteresis.Values.Any(h => h.LastMotionAtUtc is { } t && t >= thresholdUtc);

    public bool AnyDetectionActive => _hysteresis.Values.Any(h => h.IsActive);

    public IEnumerable<(DetectionKind Kind, MotionSpanResult Span)> CurrentInProgressDetectionSpans(DateTime nowUtc)
    {
        foreach (var (kind, h) in _hysteresis)
        {
            var span = h.CurrentInProgressSpan(nowUtc, TimeSpan.MaxValue);
            if (span is not null) yield return (kind, span);
        }
    }

    /// <summary>The attach URL. Scoped to the codes this app can actually act on rather than
    /// <c>[All]</c>, which would also stream every heartbeat, storage and config-change event the
    /// camera produces.</summary>
    internal Uri BuildAttachUri() =>
        new(baseUri, $"/cgi-bin/eventManager.cgi?action=attach&codes=[{DahuaCgiEventParser.SubscribeCodes}]");

    private HttpClient CreateClient()
    {
        // Digest is what these cameras require; HttpClientHandler negotiates it from Credentials.
        // PreAuthenticate spares a 401 round trip on every reconnect. Certificate validation is
        // disabled for the same reason every other camera-facing client in this app disables it
        // (see Program.cs's "onvif" named HttpClient on both tiers) — a LAN camera reached over
        // HTTPS almost always presents a self-signed certificate with no CA behind it, so standard
        // chain validation would reject every one of them, not just misconfigured ones. This was the
        // one camera-facing client that still validated, which meant an HTTPS camera's ONVIF traffic
        // worked while its CGI event stream silently never connected.
        var handler = handlerFactory?.Invoke() ?? new HttpClientHandler
        {
            Credentials = new NetworkCredential(username ?? string.Empty, password ?? string.Empty),
            PreAuthenticate = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        // No overall client Timeout: the response body never completes by design, and HttpClient's
        // Timeout applies to the whole operation including body reads, so any finite value would
        // tear down a healthy stream on a fixed schedule. Liveness is enforced per-read below instead.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = CreateClient();
                using var response = await client.GetAsync(
                    BuildAttachUri(), HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    // 401 here is a real, actionable configuration problem (wrong credentials, or an
                    // account without the event privilege), not a transient blip — logged distinctly
                    // so it doesn't read as a network flap in the logs.
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        logger.LogWarning("Camera rejected the smart-event subscription (401). Check the camera " +
                            "credentials and that the account is allowed to read events.");
                    }
                    else
                    {
                        logger.LogWarning("Smart-event subscription failed ({Status}) — will retry.", response.StatusCode);
                    }
                }
                else
                {
                    logger.LogInformation("Subscribed to camera smart events (person/vehicle detection).");
                    backoff = TimeSpan.FromSeconds(5);
                    await ReadEventStreamAsync(response, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Smart-event connection dropped — will reconnect.");
            }

            // The stream just ended, one way or another, so any span still open is closed here at the
            // point the feed died. Reconnecting does NOT recover it: attach only delivers events from
            // the moment it subscribes, so a Stop sent during the outage is gone for good. Leaving the
            // span open would let the 15s checkpoint loop keep extending its EndUtc indefinitely — a
            // camera reboot while someone is in frame would read on the timeline as a person standing
            // there for hours. A fresh Start after reconnect opens a new span, which is the honest
            // record: this is what was actually observed, with a gap where the feed was down.
            FlushOpenSpans(DateTime.UtcNow);

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }

        // Same shutdown contract as CameraEventSession: an in-progress span is closed at "now"
        // rather than silently dropped when this loop ends.
        FlushOpenSpans(DateTime.UtcNow);
    }

    /// <summary>Closes every open detection span at <paramref name="nowUtc"/>, emitting each one.
    /// A no-op for kinds with nothing open, so it's safe to call on every reconnect.
    /// internal for tests, like HandleLine — a dropped connection can't be staged from a test run.</summary>
    internal void FlushOpenSpans(DateTime nowUtc)
    {
        foreach (var (kind, h) in _hysteresis)
        {
            var flushed = h.Flush(nowUtc);
            if (flushed is not null) DetectionSpanCompleted?.Invoke(kind, flushed);
        }
    }

    private async Task ReadEventStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            // Per-read deadline rather than a client-wide timeout — see CreateClient. A read that
            // outlives this means the socket is dead in a way TCP hasn't surfaced, so the exception
            // drops out to the reconnect loop.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeout);

            string? line;
            try { line = await reader.ReadLineAsync(readCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("No smart-event traffic for {Minutes} minutes — reconnecting.", ReadTimeout.TotalMinutes);
                return;
            }

            if (line is null) return; // camera closed the stream — reconnect
            HandleLine(line);
        }
    }

    // An event's data payload is pretty-printed JSON spanning many lines, so a line-at-a-time reader
    // can't see it whole. These accumulate from the "Code=…;data={" header until the braces balance,
    // then hand the complete text to the parser. Events without a data blob balance immediately (zero
    // braces) and take the same path with no buffering, so nothing about the simple case changes.
    private readonly System.Text.StringBuilder _pending = new();
    private int _pendingDepth;
    private bool _accumulating;

    // A malformed or truncated payload would otherwise accumulate forever, holding every subsequent
    // event hostage behind a brace that never closes. Well clear of the ~1.5 KB a real FaceDetection
    // payload occupies.
    private const int MaxPendingChars = 64 * 1024;

    private static int BraceDelta(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return depth;
    }

    /// <summary>internal for tests: drives the whole classify/hysteresis path from raw lines with no
    /// socket involved, which is what makes this session's real behavior testable. Feed the lines of
    /// an event in order; a multi-line payload is buffered until complete.</summary>
    internal void HandleLine(string line, DateTime? nowUtc = null)
    {
        if (_accumulating)
        {
            _pending.Append('\n').Append(line);
            _pendingDepth += BraceDelta(line);
            if (_pendingDepth > 0 && _pending.Length <= MaxPendingChars) return;

            // Balanced (or over the cap — parse what we have rather than discard a real event whose
            // payload was truncated; the header, which is all the classification strictly needs, is
            // intact either way).
            _accumulating = false;
            var text = _pending.ToString();
            _pending.Clear();
            HandleEvent(text, nowUtc);
            return;
        }

        // Boundary markers, Content-Type/Length headers and blank lines all land here and are
        // ignored, exactly as before — only a Code= line starts an event.
        if (!line.TrimStart().StartsWith("Code=", StringComparison.OrdinalIgnoreCase)) return;

        var depth = BraceDelta(line);
        if (depth > 0)
        {
            _accumulating = true;
            _pendingDepth = depth;
            _pending.Clear();
            _pending.Append(line);
            return;
        }

        HandleEvent(line, nowUtc);
    }

    private void HandleEvent(string fullText, DateTime? nowUtc)
    {
        var parsed = DahuaCgiEventParser.ParseEvent(fullText);
        if (parsed is not { } evt) return;

        // The payload's own object class wins over the code's mapping when present: one IVS rule
        // fires the same code (CrossRegionDetection) for every class it matches, so the code alone
        // can only say "an object" while the payload says which. Falls back to the code mapping for
        // firmware that publishes no object detail, which is exactly the previous behavior.
        var kind = DahuaCgiEventParser.ClassifyObjectType(evt.ObjectType)
            ?? DahuaCgiEventParser.Classify(evt.Code);
        if (kind is not { } detectionKind) return;

        // Stamped with the node's own clock, deliberately — the CGI payload carries no trustworthy
        // timestamp, and MotionSpans must share one timebase with Segments (which ffmpeg stamps on
        // this same machine). CameraEventSession's own ResolveEventTimestamp doc comment records why
        // trusting a camera-supplied time went wrong in practice on this exact fleet.
        var at = nowUtc ?? DateTime.UtcNow;

        if (!_hysteresis.TryGetValue(detectionKind, out var hysteresis))
        {
            hysteresis = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.Zero);
            _hysteresis[detectionKind] = hysteresis;
        }

        var completed = hysteresis.Observe(at, evt.IsStart, score: 1.0);
        if (completed is not null) DetectionSpanCompleted?.Invoke(detectionKind, completed);

        // A Pulse has no matching Stop — opened and closed in the same breath, or it would stay open
        // until shutdown. Observed immediately after the rising edge above so the span has real
        // (if minimal) duration rather than being zero-length.
        if (evt.IsPulse)
        {
            var pulseClosed = hysteresis.Observe(at, motionPresent: false, score: 1.0);
            if (pulseClosed is not null) DetectionSpanCompleted?.Invoke(detectionKind, pulseClosed);
        }
    }
}

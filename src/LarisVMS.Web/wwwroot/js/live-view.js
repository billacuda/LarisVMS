// Live view MSE player (M5). One WebSocket per watched camera, straight to /live/{cameraId} on this
// same host (proxied server-side to the recorder node — see Program.cs). The server always sends the
// fMP4 init segment as the very first binary message, then one fragment per subsequent message;
// this only has to hand each message to a SourceBuffer in order, never parse the bytes itself.
(function () {
    'use strict';

    // Failover plan phase 1: media-endpoint.js is loaded alongside this file on every page that uses
    // it. This fallback only matters if that include is ever missed — it keeps live view working
    // exactly as before (always proxy through this host).
    var mediaEndpoint = window.larisvmsMediaEndpoint || {
        resolveLive: function (id) { return Promise.resolve({ mode: 'proxy', videoUrl: '/live/' + encodeURIComponent(id), token: null }); },
        invalidate: function () { },
        reportTiming: function () { },
        reportStreamEvent: function () { }
    };

    // H.264 and H.265/HEVC video codec tokens, tried in order within whichever family actually
    // matches the stream — Safari/Chrome/Firefox differ in exactly which profile strings they
    // accept, so cast a reasonably wide net within the family rather than assuming one string works
    // everywhere. Combined with an audio codec token (below) to build the full "codecs" parameter —
    // MSE requires every track actually present in the fMP4 be declared, not just the video track.
    var AVC_VIDEO_CANDIDATES = ['avc1.640028', 'avc1.4d0028'];
    var HEVC_VIDEO_CANDIDATES = ['hvc1.1.6.L93.B0', 'hev1.1.6.L93.B0'];
    var GENERIC_FALLBACK = 'video/mp4';
    // AAC-LC — what LarisVMS's cameras report (`CameraStream.HasAudio`) actually send in practice.
    var AUDIO_CODEC = 'mp4a.40.2';

    // `codecHint` is the camera's actual stream codec (RecordingSession's ffmpeg-stderr-derived
    // CameraStream.Codec — "hevc", "h264", etc.), not a browser capability. It matters because
    // recording is `-c copy`: whatever the camera natively produces is exactly what's on the wire.
    // `MediaSource.isTypeSupported` only tells you the browser *could* decode a codec family in the
    // abstract — trying an H.264 candidate first (as this used to, unconditionally) succeeds that
    // check on essentially every browser regardless of what the stream actually is, so an HEVC camera
    // opened an H.264-typed SourceBuffer, silently failed to decode the real samples, and threw
    // "removed from parent media source" on the next append once the browser tore down the errored
    // MediaSource — with no video ever appearing and no earlier, more legible error. Filtering to the
    // matching family first (falling back to the generic type, never to the *other* family, for an
    // unrecognized/missing hint) avoids ever proposing a codec the stream isn't actually using.
    //
    // `hasAudio` matters independently of codec family: every one of these cameras also has an audio
    // track (`CameraStream.HasAudio`), and the fMP4 init segment declares it — a SourceBuffer opened
    // with a video-only "codecs" parameter doesn't match that init segment's actual track list, which
    // MSE treats as a hard failure (the same "removed from parent media source" symptom as the
    // codec-family mismatch, just from a different cause). Declaring both tracks when audio is
    // present is what the init segment actually contains, not an optional nicety.
    function pickMimeType(codecHint, hasAudio) {
        if (!window.MediaSource) return null;
        var hint = (codecHint || '').toLowerCase();
        var videoCandidates;
        if (/hevc|h\.?265/.test(hint)) {
            videoCandidates = HEVC_VIDEO_CANDIDATES;
        } else if (/avc|h\.?264/.test(hint)) {
            videoCandidates = AVC_VIDEO_CANDIDATES;
        } else {
            // Unknown/missing codec info (older node build, or the camera hasn't reported one yet) —
            // best effort across both families, same as before this hint existed.
            videoCandidates = AVC_VIDEO_CANDIDATES.concat(HEVC_VIDEO_CANDIDATES);
        }
        var audioSuffix = hasAudio ? (',' + AUDIO_CODEC) : '';
        var ordered = videoCandidates.map(function (v) {
            return 'video/mp4; codecs="' + v + audioSuffix + '"';
        });
        ordered.push(GENERIC_FALLBACK);
        for (var i = 0; i < ordered.length; i++) {
            if (MediaSource.isTypeSupported(ordered[i])) return ordered[i];
        }
        return null;
    }

    // Reconnects are routine self-healing (wifi roams, a slow-client fragment gap — see
    // startSession's own header comment below) but used to blank the tile to a dark box with
    // "Reconnecting…" text for however long the retry takes, which reads as broken even though
    // nothing actually failed. Once at least one frame has ever been decoded on this tile, a
    // reconnect instead freezes that last frame — captured onto a canvas overlay sized/letterboxed
    // to match the video's own object-fit:contain rendering, since drawImage(videoEl,...) samples
    // the video's *native* resolution, not its on-screen letterboxed box — with a small spinner on
    // top, until the new session has a real frame to show again (start()'s onFrameVisible hides it).
    // Before the very first frame there's nothing to freeze, so the existing Connecting…/codec-error
    // text is left showing through unchanged.
    function createFreezeOverlay(videoEl) {
        var parent = videoEl.parentElement;
        var canvas = document.createElement('canvas');
        canvas.className = 'live-freeze-frame';
        canvas.style.cssText = 'position:absolute; top:0; left:0; width:100%; height:100%; display:none; pointer-events:none;';
        var spinner = document.createElement('div');
        spinner.className = 'position-absolute top-50 start-50 translate-middle live-freeze-spinner';
        spinner.style.cssText = 'display:none; pointer-events:none;';
        spinner.innerHTML = '<div class="spinner-border text-light" role="status"><span class="visually-hidden">Reconnecting…</span></div>';
        if (parent) {
            parent.insertBefore(canvas, videoEl.nextSibling);
            parent.insertBefore(spinner, canvas.nextSibling);
        }

        return {
            show: function () {
                try {
                    var w = videoEl.clientWidth, h = videoEl.clientHeight;
                    var nw = videoEl.videoWidth, nh = videoEl.videoHeight;
                    if (w > 0 && h > 0 && nw > 0 && nh > 0) {
                        canvas.width = w;
                        canvas.height = h;
                        var scale = Math.min(w / nw, h / nh);
                        var dw = nw * scale, dh = nh * scale;
                        var ctx = canvas.getContext('2d');
                        ctx.clearRect(0, 0, w, h);
                        ctx.drawImage(videoEl, (w - dw) / 2, (h - dh) / 2, dw, dh);
                        canvas.style.display = 'block';
                    }
                } catch (e) { /* best-effort — worst case the spinner shows with no frozen frame behind it */ }
                spinner.style.display = '';
            },
            hide: function () {
                canvas.style.display = 'none';
                spinner.style.display = 'none';
            },
            destroy: function () {
                if (canvas.parentElement) canvas.parentElement.removeChild(canvas);
                if (spinner.parentElement) spinner.parentElement.removeChild(spinner);
            }
        };
    }

    // One watched camera's live byte stream is one continuous fMP4 fanout with no per-fragment
    // resync markers, so a gap in the middle of the byte stream corrupts everything after it for
    // this SourceBuffer — there is no getting the same session decodable again once that happens,
    // only starting a fresh one (new init segment, clean MediaSource). The node's LiveViewerHandler
    // avoids ever splicing such a gap: when a slow client can't keep up (confirmed cause: roaming
    // between mesh WiFi access points) it closes the socket cleanly (code 1008) rather than dropping
    // a fragment, so recovery is always a clean reconnect rather than a corrupt stream. `start()` is
    // the auto-retrying wrapper every caller should use; `startSession()` is one single attempt.
    // M18: `role` is 'main' (default, omitted from the URL) or 'sub' — which of the camera's two
    // live-fMP4 sources (see NodeWorker.ReconcileLiveSub) this tile's WebSocket asks the node for.
    // Purely a quality/bandwidth choice, not a security boundary (see Program.cs's own /live route
    // comment) — every session, at either role, still needs the same signed per-camera token.
    function start(cameraId, videoEl, statusEl, codecHint, hasAudio, role) {
        var stoppedByUser = false;
        var retryTimer = null;
        var retryDelayMs = 2000;
        var sessionStartedAt = 0;
        var currentStop = null;
        var hasEverShownFrame = false;
        var overlay = createFreezeOverlay(videoEl);

        // Proxies statusEl so startSession's existing `statusEl.textContent = '...'` call sites
        // don't need to change at all — writes just stop taking effect once a frame has ever been
        // shown, since the freeze+spinner overlay is doing that communicating instead from then on.
        var statusProxy = {
            set textContent(v) { if (!hasEverShownFrame) statusEl.textContent = v; },
            get textContent() { return statusEl.textContent; }
        };

        function onFrameVisible() {
            hasEverShownFrame = true;
            statusEl.textContent = '';
            overlay.hide();
        }

        function launch() {
            sessionStartedAt = Date.now();
            currentStop = startSession(cameraId, videoEl, statusProxy, codecHint, hasAudio, role, onFrameVisible, function onEnded() {
                if (stoppedByUser) return;
                if (hasEverShownFrame) overlay.show();
                // A session that ran a while before failing is a transient blip (wifi roam, brief
                // network hiccup) — reset backoff so recovery stays fast. One that fails immediately,
                // repeatedly, is more likely a real problem (node/camera actually down), where
                // retrying every 2s forever would just hammer it for no benefit.
                retryDelayMs = (Date.now() - sessionStartedAt > 15000) ? 2000 : Math.min(retryDelayMs * 2, 30000);
                statusProxy.textContent = 'Reconnecting…';
                retryTimer = setTimeout(function () { retryTimer = null; launch(); }, retryDelayMs);
            });
        }
        launch();

        return function stop() {
            stoppedByUser = true;
            if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
            if (currentStop) currentStop();
            overlay.destroy();
        };
    }

    // Live view runs currentTime a deliberate, fixed distance behind buffered.end() (the "live
    // edge") rather than chasing the edge itself. The recorder's live leg now emits an fMP4 fragment
    // roughly every 500ms (RecordingSession.LiveFragDurationMicros), but a fresh keyframe fragment
    // can still be up to a GOP of video, so buffered.end() advances in small bursts; holding ~2s of
    // slack means a burst never leaves currentTime past the end of what's buffered (which would
    // stall on `waiting` with nothing to play). This is a few seconds of added latency traded for
    // not oscillating between a speed-up and a stall — the old code chased the edge with a hard
    // playbackRate=1.5 and starved the buffer on every keyframe.
    var TARGET_LATENCY_SECONDS = 2.0;
    // AI-detection boxes arrive over their own low-latency path (measured ~200ms end to end: ~143ms
    // inter-frame at the Sub stream's 7fps, 10-40ms inference, ~75ms average on Node's 150ms poll),
    // while the video they're drawn over is held behind live by the buffering above — so without a
    // hold-back they visibly lead the object.
    //
    // That hold-back used to be a fixed constant, which could not work: the drift controller only
    // corrects outside a +/-LATENCY_DEADBAND_SECONDS band and trims the rate within it, so the
    // video's real latency *floats* by design. A constant is wrong by however far drift has wandered
    // — confirmed live in both directions (boxes 0.5-1.5s late at a 3.0s constant, then still late
    // at 2.0s once the video path got healthier). startDetectionOverlay now measures the video's
    // actual latency instead, via the timing handle startSession publishes on the shared videoEl.
    //
    // This is the only piece that measurement can't supply: the gap between a fragment arriving and
    // the wall-clock instant its NEWEST frame — the one buffered.end() actually points at — was
    // captured. Not half of RecordingSession's LiveFragDurationMicros (500ms): frag_duration/
    // min_frag_duration force a fragment boundary every 200-500ms regardless of keyframes
    // specifically so a fragment is flushed close to when it's encoded, not accumulated over that
    // whole window — so the newest frame inside it is only as stale as encode processing plus LAN
    // transit, both small. (An earlier version of this comment reasoned from the fragment's average
    // content age, which answers a different question than "how old is the newest frame" — confirmed
    // live as a real, visible source of lag once boxes were actually measured against video content
    // rather than just checked against TARGET_LATENCY_SECONDS.)
    var BOX_RESIDUAL_LATENCY_MS = 50;
    // Fallback hold-back for when no timing handle is available at all — an overlay mounted over a
    // tile that isn't running a live session (view-play.js keeps the overlay mounted across a
    // playback toggle). Same fixed-constant behaviour as before measurement existed.
    var BOX_FALLBACK_LATENCY_MS = TARGET_LATENCY_SECONDS * 1000;
    // Safety net for a paused/backgrounded tab: a tick older than this is dropped rather than ever
    // rendered, mirroring LiveViewerHandler's own bounded-queue-drop philosophy server-side.
    var BOX_QUEUE_MAX_AGE_SECONDS = 5;
    // How often the box overlay reports its own alignment numbers (measured video latency, incoming
    // ageMs, the interpolation span it's working across). rAF runs ~60x/sec; this is per-tile, so
    // keep it rare.
    var BOX_ALIGNMENT_REPORT_EVERY_FRAMES = 600;
    // Half-width of the no-correction band around the target. Inside +/- this of the target, the
    // rate stays exactly 1.0 — a tile only corrects once it has drifted meaningfully off target, so
    // it isn't perpetually nudging the rate a hair either way.
    var LATENCY_DEADBAND_SECONDS = 0.75;
    // playbackRate delta per second of error beyond the dead-band, then clamped. Gentle by design:
    // the correction is spread over many 1s ticks through already-decodable data, so there is no
    // discontinuity and no risk of landing off a keyframe (the failure mode of every seek-based
    // correction previously tried here — see the comment block above driftTimer).
    var CATCHUP_RATE_GAIN = 0.15;
    var CATCHUP_MAX_RATE = 1.25;   // behind target -> speed up, but never jarringly
    var CATCHUP_MIN_RATE = 0.90;   // too close to the edge -> ease off so the buffer rebuilds
    var DRIFT_CHECK_INTERVAL_MS = 1000;
    // Beyond this, a playbackRate catch-up would take too long to be worth it (confirmed cause:
    // browsers throttle setInterval on a backgrounded/unfocused tab, so a real gap of a minute or
    // more can go undetected until the tab regains focus) — falls back to ending the session, same
    // as the stall-recovery path below, so start()'s retry wrapper rebuilds cleanly at the edge.
    var HARD_RESYNC_THRESHOLD_SECONDS = 15;
    // Drift threshold for the pause/playing recovery handler (see onVideo('playing', ...) below) to
    // step in with an immediate seek rather than leaving it to the ordinary gentle rate-trim.
    // Comfortably above ordinary jitter, comfortably below HARD_RESYNC_THRESHOLD_SECONDS — the whole
    // point is to resync before drift ever gets anywhere near that cliff.
    var PAUSE_RESYNC_THRESHOLD_SECONDS = 5;
    // How much buffered-but-already-played data driftTimer leaves behind currentTime before trimming
    // the rest via SourceBuffer.remove() (see driftTimer below). Nothing else in this file ever
    // evicts old data, so during an extended stall (backgrounded tab, network hiccup) fragments keep
    // arriving and appending indefinitely — confirmed live as a session whose backlog grew to ~120s,
    // at which point the `waiting` handler's only keyframe-safe recovery (jumpToLiveEdge, which must
    // target buffered.start() — see the comment block above driftTimer for why seeking anywhere else
    // regressed) landed 120s behind the live edge and immediately tripped HARD_RESYNC_THRESHOLD_
    // SECONDS, tearing the session down and repeating the same failure on the very next connection.
    // Trimming bounds how far behind buffered.start() can ever be, independent of playback state
    // (fragments keep appending even while paused), so that fallout is capped well under the
    // hard-resync threshold instead of forcing a teardown. Comfortably above the largest *legitimate*
    // forward drift observed (buffered.end() - currentTime, an unrelated axis — this only ever trims
    // data *behind* currentTime, never data the catch-up controller still needs ahead of it).
    var BUFFER_TRIM_KEEP_SECONDS = 8;
    // The badge is for sustained, visible catch-up only — not the ordinary sub-1.05x trims the
    // controller makes constantly — so it needs both a minimum rate and a couple of ticks at it.
    var CATCHUP_BADGE_MIN_RATE = 1.05;
    var CATCHUP_BADGE_SUSTAIN_TICKS = 2;

    // Runs exactly one session attempt and returns its stop() function. `onEnded` fires exactly
    // once, however the session stops — explicit stop() call, decode error, or WebSocket
    // close/error — so start()'s retry wrapper above has one place to decide whether to reconnect.
    function startSession(cameraId, videoEl, statusEl, codecHint, hasAudio, role, onFrameVisible, onEnded) {
        var ended = false;
        // The <video> element outlives every session attached to it (each reconnect builds a fresh
        // MediaSource but reuses the same element), so listeners bound to it must be removed when
        // their session ends or they accumulate one full set per reconnect — every one of them still
        // firing, still holding its dead session's closure alive. Confirmed live: a single decode
        // error printed once per leaked listener, each reporting its own stale fragment count
        // ("fragments received= 489 / 483 / 2"), which reads exactly like several concurrent sessions
        // and made the console useless for diagnosing the real fault underneath.
        var elementListeners = [];
        function onVideo(type, handler) {
            elementListeners.push([type, handler]);
            videoEl.addEventListener(type, handler);
        }

        // "Catching up" badge (catchup-badge.js), shown while this tile is running above 1.0x to
        // close a drift gap; the label is the current rate. The tile's positioned container is the
        // video's parent — same element createFreezeOverlay overlays onto.
        function showCatchupBadge(rate) {
            if (!window.larisvmsCatchupBadge) return;
            var label = rate ? (Math.round(rate * 100) / 100) + '×' : 'catching up';
            window.larisvmsCatchupBadge.show(videoEl.parentElement, label);
        }
        function hideCatchupBadge() {
            if (window.larisvmsCatchupBadge) window.larisvmsCatchupBadge.hide(videoEl.parentElement);
        }

        function endSession() {
            if (ended) return;
            ended = true;
            // Cleared here rather than only in stop(): a session can end without stop() ever being
            // called (decode error, socket close), and a leaked interval would keep adjusting a video
            // element belonging to a torn-down session. playbackRate is reset too — the <video>
            // element itself persists across reconnects (a fresh MediaSource is attached to the same
            // element), so a session that ended mid-catch-up would otherwise leave the *next*
            // session's playback silently sped up from the very first frame.
            if (driftTimer) { clearInterval(driftTimer); driftTimer = null; }
            for (var i = 0; i < elementListeners.length; i++) {
                videoEl.removeEventListener(elementListeners[i][0], elementListeners[i][1]);
            }
            elementListeners = [];
            videoEl.playbackRate = 1;
            // Cleared so the box overlay falls back to its fixed constant rather than scheduling
            // against a frozen clock from a session that has stopped producing fragments — the
            // overlay outlives an individual video session (it stays mounted across reconnects and
            // across a playback toggle).
            if (videoEl._larisvmsLiveTiming) videoEl._larisvmsLiveTiming = null;
            hideCatchupBadge();
            onEnded();
        }

        if (!('MediaSource' in window)) {
            statusEl.textContent = 'This browser does not support MSE playback.';
            endSession();
            return function stop() {};
        }
        var mimeType = pickMimeType(codecHint, hasAudio);
        if (!mimeType) {
            statusEl.textContent = 'No supported video codec found for MSE in this browser.';
            endSession();
            return function stop() {};
        }

        var mediaSource = new MediaSource();
        var objectUrl = URL.createObjectURL(mediaSource);
        videoEl.src = objectUrl;

        var socket = null;
        var sourceBuffer = null;
        var pending = [];
        // Arrival time of each queued fragment (parallel to `pending`), and of the one currently
        // being appended — published to the box overlay only once its append completes, so the
        // arrival instant and the buffered end it produced always describe the same frame.
        var pendingArrivals = [];
        var appendingArrivalMs = null;
        var closed = false;
        var driftTimer = null;
        var initialSeekDone = false;
        var catchupTicks = 0;
        // Hoisted out of the resolveLive().then() callback below (where it's assigned) so the
        // stutter-beacon call sites here in startSession's top level — driftTimer, jumpToLiveEdge —
        // can report which routing mode (proxy/direct) a session was actually using.
        var streamMode = 'proxy';
        // Decode-health sampling state (driftTimer below) — previous tick's wall-clock time,
        // currentTime, and cumulative dropped/total frame counts, so each tick can compute a delta
        // rather than a cumulative-since-session-start figure. null until the first sample lands.
        var lastHealthSampleMs = 0;
        var lastHealthCurrentTime = 0;
        var lastPlaybackQuality = null;
        var decodeHealthTickCount = 0;
        // Every Nth driftTimer tick, a decode_health beacon is sent regardless of state — cheap
        // enough at 1 tick/sec that "every 10th" is still a fine-grained trend, without spamming the
        // endpoint for every one of what could be many simultaneous grid tiles.
        var DECODE_HEALTH_REPORT_EVERY_TICKS = 10;
        // Effective rate (actual currentTime advance per wall-clock second) below this fraction of
        // the *requested* playbackRate means the decoder itself can't keep up — asking it to go
        // faster can't help and only starves the buffer sooner. Below this ratio the catch-up
        // controller stops pushing the rate up and reports the shortfall immediately (not waiting
        // for the next scheduled low-duty-cycle tick), since this is exactly the condition Part 2's
        // GPU transcode work is meant to fix.
        var DECODE_BOUND_RATE_RATIO = 0.85;

        // Publishes what the box overlay needs to know about this video's real latency, so it can
        // hold detections back by the right amount instead of guessing with a constant. Hung off the
        // <video> element because startSession and startDetectionOverlay are sibling functions that
        // already share exactly one thing — this element — and threading a handle through both call
        // sites in view-play.js/view-editor.js/zones-editor.js would touch far more surface for no
        // extra capability. Ownership is the same as everywhere else in this file: whoever currently
        // owns videoEl.src owns the element, so a superseded session must not keep writing here (a
        // tile toggled into playback hands this element to playback-player.js entirely).
        //
        // Together these give the overlay:
        //   videoLatencyMs = (now - lastFragmentArrivalMs) + driftMs + BOX_RESIDUAL_LATENCY_MS
        // i.e. how long ago the newest buffered frame arrived, plus how far behind the buffer's live
        // edge playback is deliberately sitting, plus the one unmeasurable bit (camera encode +
        // fragment accumulation).
        function publishLiveTiming(arrivalMs) {
            if (closed || ended || videoEl.src !== objectUrl) return;
            var driftMs = null;
            if (sourceBuffer && !sourceBuffer.updating && sourceBuffer.buffered.length > 0) {
                var last = sourceBuffer.buffered.length - 1;
                driftMs = (sourceBuffer.buffered.end(last) - videoEl.currentTime) * 1000;
            }
            var existing = videoEl._larisvmsLiveTiming;
            // The buffered end produced by the fragment that arrived at arrivalMs. Paired with that
            // arrival (both set only after its append completes), it lets the overlay measure drift
            // against the video's *live* currentTime on every frame — see videoLatencyMs.
            var bufferedEndSec = null;
            if (arrivalMs !== null && sourceBuffer && sourceBuffer.buffered.length > 0) {
                bufferedEndSec = sourceBuffer.buffered.end(sourceBuffer.buffered.length - 1);
            }
            videoEl._larisvmsLiveTiming = {
                // Kept from the previous sample when this call couldn't measure one (mid-append), so
                // a busy SourceBuffer doesn't blank the overlay's clock for a frame.
                lastFragmentArrivalMs: arrivalMs !== null ? arrivalMs
                    : (existing ? existing.lastFragmentArrivalMs : null),
                bufferedEndSec: bufferedEndSec !== null ? bufferedEndSec
                    : (existing ? existing.bufferedEndSec : null),
                driftMs: driftMs !== null ? driftMs : (existing ? existing.driftMs : null)
            };
        }

        // An appendBuffer failure is never recoverable for this session (a torn-down/replaced
        // MediaSource throws "removed from parent media source" on the very next call, and retrying
        // the same fragment forever would just repeat the same throw for every future WebSocket
        // message) — so any exception here ends the session the same way stop() does, instead of
        // leaving the socket open and spamming the same error on every subsequent fragment.
        function appendNext() {
            if (closed || !sourceBuffer || sourceBuffer.updating || pending.length === 0) return;
            try {
                appendingArrivalMs = pendingArrivals.length > 0 ? pendingArrivals.shift() : null;
                sourceBuffer.appendBuffer(pending.shift());
            } catch (e) {
                statusEl.textContent = 'Playback error: ' + e.message;
                closed = true;
                if (socket) { try { socket.close(); } catch (e2) {} }
                endSession();
            }
        }

        // Bytes can flow all the way through (WebSocket connects, appendBuffer never throws) while
        // still never producing a visible frame — a codec/container mismatch subtle enough not to
        // fail synchronously shows up here instead, as a MediaError on the element or a stalled
        // readyState, neither of which the appendBuffer try/catch above can ever see. Logged to the
        // console (byte counts included) rather than only statusEl, since diagnosing "which fragment"
        // needs more detail than a one-line status message has room for.
        var fragmentCount = 0;
        // Fires once real playback resumes — the reliable signal that this session actually has a
        // decodable frame on screen again, not just that appendBuffer stopped throwing. Can fire more
        // than once per session (e.g. after a buffering pause); onFrameVisible is idempotent so that's
        // harmless. Registered through onVideo so it's detached with the rest when the session ends.
        onVideo('playing', onFrameVisible);

        // Recovery from an unexpected pause (confirmed live: video.paused briefly goes true —
        // whatever the outside cause, a backgrounded/occluded tab, a display sleep, or anything else
        // that stops decode for a while — with playback frozen the whole time). driftTimer's own 1Hz
        // polling can't catch this until drift has already grown past HARD_RESYNC_THRESHOLD_SECONDS,
        // at which point every tile in a grid that was paused together discovers it independently and
        // hard-resyncs within the same second or two — a synchronized restart storm. Watching the
        // element's own pause/playing transitions directly means this tile can resync to near the
        // live edge the instant playback actually resumes, before driftTimer's next tick ever sees
        // the large drift.
        var wasPaused = false;
        onVideo('pause', function () {
            // Only stop()'s own teardown calls .pause() intentionally, and it sets closed=true first
            // (see stop() below) — anything else pausing the element is the browser's doing, not ours.
            if (closed || ended) return;
            wasPaused = true;
            // Captured at the exact instant of the pause, because everything about this freeze has so
            // far had to be inferred after the fact: whether the page genuinely had focus/visibility
            // (both are reported by the browser itself here rather than recalled later), whether data
            // was still flowing, and what state the buffer was in. Cheap — an unexpected pause is
            // rare, and each one is exactly the event worth a detailed line.
            var buffered = 'none';
            if (sourceBuffer && sourceBuffer.buffered.length > 0) {
                var last = sourceBuffer.buffered.length - 1;
                buffered = sourceBuffer.buffered.start(last).toFixed(2) + '-' + sourceBuffer.buffered.end(last).toFixed(2) +
                    ' (ranges=' + sourceBuffer.buffered.length + ')';
            }
            var detail = 'visibility=' + document.visibilityState +
                ' hasFocus=' + (typeof document.hasFocus === 'function' ? document.hasFocus() : 'n/a') +
                ' readyState=' + videoEl.readyState +
                ' networkState=' + videoEl.networkState +
                ' currentTime=' + videoEl.currentTime.toFixed(2) +
                ' buffered=' + buffered +
                ' updating=' + (sourceBuffer ? sourceBuffer.updating : 'n/a') +
                ' fragments=' + fragmentCount +
                ' error=' + (videoEl.error ? videoEl.error.code : 'none');
            console.warn('[live-view] unexpected pause —', detail);
            mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'unexpected_pause', null, detail);
        });
        onVideo('playing', function () {
            if (!wasPaused) return;
            wasPaused = false;
            if (closed || ended || !sourceBuffer || sourceBuffer.buffered.length === 0) return;
            var lastRange = sourceBuffer.buffered.length - 1;
            var rangeEnd = sourceBuffer.buffered.end(lastRange);
            var drift = rangeEnd - videoEl.currentTime;
            // Small enough that the ordinary gentle rate-trim in driftTimer will close it on its own
            // — no need to intervene for every brief, harmless pause/resume blip.
            if (drift <= PAUSE_RESYNC_THRESHOLD_SECONDS) return;
            // Same target formula as doInitialSeek below — proven safe in production (rangeStart is
            // always keyframe-aligned; a point short of buffered.end() leaves headroom rather than
            // risking the "seek too close to the live edge" MediaError regression documented above
            // driftTimer), not a new risky pattern.
            var rangeStart = sourceBuffer.buffered.start(lastRange);
            var target = Math.max(rangeStart, rangeEnd - TARGET_LATENCY_SECONDS);
            console.log('[live-view] resynced after playback resumed, currentTime=', videoEl.currentTime,
                '-> ', target, 'was', drift.toFixed(1), 's behind');
            videoEl.currentTime = target;
            videoEl.playbackRate = 1;
            catchupTicks = 0;
            hideCatchupBadge();
            mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'pause_resync', drift, null);
        });

        onVideo('error', function () {
            var err = videoEl.error;
            var msg = 'Video decode error' + (err ? ' (code ' + err.code + ')' : '') + '.';
            statusEl.textContent = msg;
            console.error('[live-view]', msg, err, 'mimeType=', mimeType, 'fragments received=', fragmentCount);
            closed = true;
            if (socket) { try { socket.close(); } catch (e2) {} }
            endSession();
        });
        onVideo('stalled', function () {
            console.warn('[live-view] video element stalled; fragments received=', fragmentCount, 'readyState=', videoEl.readyState);
        });

        mediaSource.addEventListener('sourceopen', function () {
            // A session can already be over by the time this fires (early decode error, immediate
            // socket close, or the caller swapping this element into playback mode). Bailing here is
            // what keeps endSession()'s clearInterval authoritative — otherwise the driftTimer below
            // gets created *after* the cleanup that was supposed to cancel it, leaving an orphaned
            // interval nothing will ever clear for the life of the page.
            if (closed || ended) return;
            try {
                sourceBuffer = mediaSource.addSourceBuffer(mimeType);
            } catch (e) {
                statusEl.textContent = 'This browser cannot decode this stream (' + mimeType + ').';
                return;
            }
            // Registered before appendNext's own listener, so it sees the arrival of the fragment
            // that just finished appending before appendNext starts the next one.
            sourceBuffer.addEventListener('updateend', function () {
                if (appendingArrivalMs !== null) publishLiveTiming(appendingArrivalMs);
                appendingArrivalMs = null;
            });
            sourceBuffer.addEventListener('updateend', appendNext);

            function isTimeBuffered(t) {
                var b = sourceBuffer.buffered;
                for (var i = 0; i < b.length; i++) {
                    if (t >= b.start(i) && t <= b.end(i)) return true;
                }
                return false;
            }
            function jumpToLiveEdge(reason) {
                var lastRange = sourceBuffer.buffered.length - 1;
                var target = sourceBuffer.buffered.start(lastRange);
                console.log('[live-view] ' + reason + ', currentTime=', videoEl.currentTime, '-> ', target,
                    'buffered=', target, '-', sourceBuffer.buffered.end(lastRange));
                videoEl.currentTime = target;
                mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'gap_jump', null, reason);
            }

            // A fresh <video> defaults to currentTime=0, but this live leg has been running (and its
            // fMP4 timestamps incrementing) since ffmpeg started — not since this viewer connected —
            // so a late joiner's first buffered range typically starts well past 0. MSE won't advance
            // readyState past HAVE_METADATA (no error, just stuck) until currentTime falls inside a
            // buffered range, so without this a live-view session silently never plays: data arrives
            // and appendBuffer never throws, but nothing is ever positioned where it's buffered.
            //
            // Seeks once, the first time any range is buffered, to TARGET_LATENCY_SECONDS behind that
            // range's end — never to the edge itself (no buffer headroom, stalls on the first
            // keyframe burst). rangeStart is always keyframe-aligned (a browser only ever buffers a
            // range starting from a keyframe), so when less than TARGET_LATENCY_SECONDS is buffered
            // yet, max() collapses the target to rangeStart — exactly jumpToLiveEdge()'s old
            // keyframe-safe anchor — and the drift controller eases back toward the 2s target as the
            // buffer fills. This is NOT the mid-playback seek-near-the-end that regressed before (see
            // the comment block above driftTimer): it is a single seek at session start into one
            // contiguous range whose start is a keyframe, the same thing playback-player.js does on
            // every segment load. Must run before the driftTimer does anything (it early-returns
            // until initialSeekDone) — otherwise a late joiner sitting at currentTime=0 while
            // buffered.end() is minutes in reads as "hundreds of seconds behind" and the hard-resync
            // branch tears the session down on the first tick, forever.
            function doInitialSeek() {
                if (initialSeekDone || closed || ended || sourceBuffer.buffered.length === 0) return;
                var last = sourceBuffer.buffered.length - 1;
                var rangeStart = sourceBuffer.buffered.start(last);
                var rangeEnd = sourceBuffer.buffered.end(last);
                initialSeekDone = true;
                var target = Math.max(rangeStart, rangeEnd - TARGET_LATENCY_SECONDS);
                console.log('[live-view] initial seek, currentTime=', videoEl.currentTime, '-> ', target,
                    'buffered=', rangeStart, '-', rangeEnd);
                videoEl.currentTime = target;
            }
            sourceBuffer.addEventListener('updateend', doInitialSeek);
            // Ongoing counterpart to the one-time seek above: the buffered range is a sliding window
            // (the browser evicts old data as new fragments arrive), so currentTime can drift into
            // now-evicted territory later in a long-running session too, not just at startup — e.g.
            // if a rate trim below never quite keeps pace with real network lag. When that happens
            // playback stalls with no error and no visible frame (MSE has genuinely nothing buffered
            // at that position) until either the browser's own gap-jump heuristics kick in on their
            // schedule, or — on some browsers — never, leaving the tile blank indefinitely. `waiting`
            // fires whenever playback can't continue at the current position; only treat it as a gap
            // to jump across if currentTime is truly outside every buffered range — a `waiting` fired
            // just from normally catching up to the live edge (currentTime inside the last buffered
            // range, simply waiting for more to arrive) must NOT trigger a seek, or every ordinary
            // pause-for-more-data would show as a needless jump.
            onVideo('waiting', function () {
                if (closed || ended || sourceBuffer.buffered.length === 0) return;
                if (isTimeBuffered(videoEl.currentTime)) return;
                jumpToLiveEdge('resynced after falling out of the buffered range');
            });

            // The `waiting` handler above only fires when playback actually stalls, which misses the
            // case where a tile is playing perfectly well but simply *behind* — most commonly right
            // after a reconnect, since a fresh session starts at whatever its first buffered range
            // happens to be rather than at the newest frame available. Nothing ever pulled such a
            // tile forward again, so in a multi-camera grid one reconnected tile could sit seconds
            // behind its neighbors indefinitely. Each tile independently chasing its own live edge is
            // enough to keep the whole grid visually together — no cross-tile clock or driving/
            // following relationship needed (unlike Playback, which genuinely needs one because it
            // seeks to an arbitrary shared instant).
            //
            // Two prior regressions of this exact shape are already on record, both from correcting
            // drift with a *seek* (videoEl.currentTime = ...):
            //   1. Reusing jumpToLiveEdge() (targets buffered.start()) moved *further* from the live
            //      edge, not closer, when currentTime was already validly playing mid-range — a
            //      repeating rewind that only cleared once a reconnect/eviction narrowed the range.
            //   2. Seeking to a point just short of buffered.end() instead "fixed" the direction but
            //      is not guaranteed to land on a keyframe/fragment boundary — confirmed live as
            //      MediaError code 3 ("Failed to prepare video sample for decode") immediately after
            //      a large correction, which tore the session down and reconnected: the exact same
            //      "loop" symptom, from a different cause (decode failure, not a bad rewind target).
            //      Root cause of the *large* corrections that triggered it: a backgrounded/unfocused
            //      browser tab throttles setInterval, so drift can grow to tens of seconds between
            //      ticks with nothing wrong on the wire.
            // Fix: never seek to correct ordinary drift. Hold currentTime a fixed TARGET_LATENCY_
            // SECONDS behind buffered.end() and trim playbackRate gently toward that target — the
            // decoder eases in or out through real, already-decodable data, zero discontinuity, zero
            // risk of landing off a keyframe. The target being a positive offset (not ~0) is what
            // gives a keyframe burst room to land without starving the buffer, which is what caused
            // the old speed-up/stall oscillation. Only beyond HARD_RESYNC_THRESHOLD_SECONDS (drift
            // too large for a rate trim to close in reasonable time — almost always a backgrounded
            // tab whose throttled timer let a huge gap build) does this end the session and let
            // start()'s retry wrapper rebuild cleanly at the edge.
            driftTimer = setInterval(function () {
                // Ownership guard, first thing: this same <video> element gets handed to a completely
                // different player when a tile/cell is toggled into playback mode (playback-player.js
                // attaches its own MediaSource to it). A timer belonging to this now-superseded live
                // session must never touch the element again — confirmed live as a real failure, not
                // theoretical: leaked timers kept seeking the element while the playback player owned
                // it, producing exactly the interleaved multi-position "loop" this guard prevents.
                if (closed || ended || videoEl.src !== objectUrl) return;
                // Never run the controller (least of all the hard-resync branch below) before the
                // one-time initial seek has positioned currentTime — until then it's at 0 while
                // buffered.end() is minutes in, which reads as a huge false "drift".
                if (!initialSeekDone) return;

                // Runs even while paused — fragments keep arriving and appending regardless of
                // playback state, so backlog can grow whether or not the controller below is active.
                // remove() is itself an async SourceBuffer operation gated by `updating`, same as
                // appendBuffer; skipping this tick when busy is fine, the next tick tries again.
                //
                // Exonerated by an isolation test (2026-09-21): the freeze this was briefly suspected
                // of causing reproduced identically with this disabled, and was then traced to the
                // node process stalling wholesale (see NodeWorker.ProcessHealthLoopAsync). Kept on
                // because it still bounds the worst case — an extended upstream stall would otherwise
                // let backlog grow until the `waiting` handler's only keyframe-safe recovery
                // (jumpToLiveEdge → buffered.start()) lands far enough back to trip
                // HARD_RESYNC_THRESHOLD_SECONDS and tear the session down.
                if (!sourceBuffer.updating && sourceBuffer.buffered.length > 0) {
                    var trimEnd = videoEl.currentTime - BUFFER_TRIM_KEEP_SECONDS;
                    if (trimEnd > sourceBuffer.buffered.start(0)) {
                        try { sourceBuffer.remove(0, trimEnd); } catch (e) { /* best-effort, retried next tick */ }
                    }
                }

                if (videoEl.paused || sourceBuffer.buffered.length === 0) return;
                var lastRange = sourceBuffer.buffered.length - 1;
                var liveEdge = sourceBuffer.buffered.end(lastRange);
                var drift = liveEdge - videoEl.currentTime;

                // Keeps the overlay's drift figure fresh between fragment arrivals — playback keeps
                // advancing (and the rate controller keeps trimming it) in the ~500ms gaps, so a
                // figure only refreshed on arrival would lag by up to that much.
                publishLiveTiming(null);

                // Decode-health sample: how much currentTime actually advanced this tick vs. wall
                // time, against the rate we asked for. A decoder that's keeping up tracks
                // playbackRate closely; one that's saturated (too many simultaneous decode
                // instances, insufficient hardware accel) can't exceed roughly 1x of real time no
                // matter what rate is requested — this is the same signature that showed up live as
                // "requesting 1.25x never closes the gap." See DECODE_BOUND_RATE_RATIO above.
                var nowMs = Date.now();
                var effectiveRate = null, rateRatio = null, decodeBound = false;
                if (lastHealthSampleMs > 0) {
                    var wallDeltaS = (nowMs - lastHealthSampleMs) / 1000;
                    if (wallDeltaS > 0) {
                        effectiveRate = (videoEl.currentTime - lastHealthCurrentTime) / wallDeltaS;
                        if (videoEl.playbackRate > 0) {
                            rateRatio = effectiveRate / videoEl.playbackRate;
                            decodeBound = rateRatio < DECODE_BOUND_RATE_RATIO;
                        }
                    }
                }
                var droppedDelta = null, totalDelta = null;
                if (typeof videoEl.getVideoPlaybackQuality === 'function') {
                    var quality = videoEl.getVideoPlaybackQuality();
                    if (lastPlaybackQuality) {
                        droppedDelta = quality.droppedVideoFrames - lastPlaybackQuality.droppedVideoFrames;
                        totalDelta = quality.totalVideoFrames - lastPlaybackQuality.totalVideoFrames;
                    }
                    lastPlaybackQuality = quality;
                }
                lastHealthSampleMs = nowMs;
                lastHealthCurrentTime = videoEl.currentTime;

                // Reported on a low duty cycle to stay cheap across a grid of simultaneous tiles,
                // except a decode-bound tick is reported immediately — that's the condition worth
                // seeing right away, not up to 10s later.
                decodeHealthTickCount++;
                if (decodeBound || decodeHealthTickCount % DECODE_HEALTH_REPORT_EVERY_TICKS === 0) {
                    var healthDetail = 'requestedRate=' + videoEl.playbackRate.toFixed(2) +
                        (effectiveRate !== null ? ' effectiveRate=' + effectiveRate.toFixed(2) : '') +
                        (totalDelta !== null ? ' droppedFrames=' + droppedDelta + '/' + totalDelta : '');
                    mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'decode_health', rateRatio, healthDetail);
                }

                if (drift > HARD_RESYNC_THRESHOLD_SECONDS) {
                    // Deliberately NOT a seek — every seek-based correction tried here regressed (see
                    // the comment block above). Ending the session hands off to start()'s retry
                    // wrapper, which builds a fresh MediaSource that begins cleanly near the edge.
                    console.log('[live-view] ' + drift.toFixed(1) + 's behind live edge — too far to catch up, restarting session');
                    mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'hard_resync', drift, null);
                    videoEl.playbackRate = 1;
                    hideCatchupBadge();
                    closed = true;
                    if (socket) { try { socket.close(); } catch (e2) {} }
                    endSession();
                    return;
                }

                // Proportional trim around the target, inside gentle clamps. error > 0 => behind
                // target => speed up; error < 0 => drifted too close to the edge => slow below 1.0
                // so the buffer rebuilds. Inside the dead-band the rate is exactly 1.0.
                var error = drift - TARGET_LATENCY_SECONDS;
                var desiredRate = 1;
                if (error > LATENCY_DEADBAND_SECONDS) {
                    desiredRate = Math.min(CATCHUP_MAX_RATE, 1 + CATCHUP_RATE_GAIN * (error - LATENCY_DEADBAND_SECONDS));
                } else if (error < -LATENCY_DEADBAND_SECONDS) {
                    desiredRate = Math.max(CATCHUP_MIN_RATE, 1 + CATCHUP_RATE_GAIN * (error + LATENCY_DEADBAND_SECONDS));
                }
                // The decoder already can't sustain the current rate — asking for more can't close
                // the gap (confirmed live: requesting 1.25x never moved drift) and only starves the
                // buffer sooner, which is what turns an ordinary catch-up into a hard resync. Let
                // drift sit wider instead of thrashing toward a restart neither more speed nor more
                // time will fix.
                if (decodeBound && desiredRate > 1) desiredRate = 1;
                if (Math.abs(videoEl.playbackRate - desiredRate) > 0.01) videoEl.playbackRate = desiredRate;

                // Badge only on sustained real catch-up, not the constant sub-1.05x trims.
                if (desiredRate >= CATCHUP_BADGE_MIN_RATE) {
                    if (++catchupTicks === CATCHUP_BADGE_SUSTAIN_TICKS) {
                        console.log('[live-view] catching up to live edge (' + drift.toFixed(1) + 's behind)');
                        mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'catchup', drift, desiredRate.toFixed(2) + 'x');
                    }
                    if (catchupTicks >= CATCHUP_BADGE_SUSTAIN_TICKS) showCatchupBadge(desiredRate);
                } else {
                    catchupTicks = 0;
                    hideCatchupBadge();
                }
            }, DRIFT_CHECK_INTERVAL_MS);
            sourceBuffer.addEventListener('error', function (e) {
                console.error('[live-view] sourceBuffer error', e, 'mimeType=', mimeType, 'fragments received=', fragmentCount);
                statusEl.textContent = 'SourceBuffer error — see browser console.';
                closed = true;
                if (socket) { try { socket.close(); } catch (e2) {} }
                endSession();
            });

            statusEl.textContent = 'Connecting…';
            var connectStartMs = (window.performance && performance.now) ? performance.now() : Date.now();
            var timingReported = false;

            // Failover plan phase 1: resolve whether this stream goes through the server (proxy — the
            // default, and byte-for-byte the old behaviour) or straight to the recorder node (direct).
            // A missing/failed ticket falls back to the proxy URL, so this can never make live view
            // worse than before it existed.
            mediaEndpoint.resolveLive(cameraId).then(function (ticket) {
                if (closed || ended) return;
                ticket = ticket || { mode: 'proxy' };

                var url;
                streamMode = 'proxy';
                if (ticket.mode === 'direct' && ticket.videoUrl && ticket.token) {
                    var sep = ticket.videoUrl.indexOf('?') >= 0 ? '&' : '?';
                    url = ticket.videoUrl + sep + 'token=' + encodeURIComponent(ticket.token) +
                          (role === 'sub' ? '&role=sub' : '');
                    streamMode = 'direct';
                } else {
                    var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
                    url = proto + '//' + location.host + '/live/' + cameraId + (role === 'sub' ? '?role=sub' : '');
                }

                socket = new WebSocket(url);
                socket.binaryType = 'arraybuffer';

                socket.onmessage = function (evt) {
                    fragmentCount++;
                    if (fragmentCount === 1) {
                        console.log('[live-view] init segment received,', evt.data.byteLength, 'bytes, mimeType=', mimeType, 'mode=', streamMode);
                        if (!timingReported) {
                            timingReported = true;
                            var nowMs = (window.performance && performance.now) ? performance.now() : Date.now();
                            mediaEndpoint.reportTiming(cameraId, streamMode, nowMs - connectStartMs);
                        }
                    } else if (fragmentCount <= 5 || fragmentCount % 50 === 0) {
                        console.log('[live-view] fragment', fragmentCount, ',', evt.data.byteLength, 'bytes, video.readyState=', videoEl.readyState, 'video.paused=', videoEl.paused);
                    }
                    statusEl.textContent = '';
                    // Anchors the box overlay's presentation clock — see publishLiveTiming.
                    // Recorded here so it marks when the bytes actually arrived, not when the
                    // SourceBuffer got around to accepting them; published once that append
                    // completes (see the updateend listener), paired with the buffered end it made.
                    pendingArrivals.push(fragmentCount > 1 ? Date.now() : null);
                    pending.push(new Uint8Array(evt.data));
                    appendNext();
                };
                socket.onerror = function () {
                    statusEl.textContent = 'Connection error.';
                    if (streamMode === 'direct') {
                        // Very often an untrusted certificate on the recorder. Drop the cached ticket
                        // so a retry re-resolves (the operator may have flipped the toggle), and — if
                        // this was flagged insecure — offer the one-click trust step.
                        mediaEndpoint.invalidate(cameraId);
                        if (ticket.insecure && typeof ticket.trustUrl === 'string' &&
                            ticket.trustUrl.indexOf('https://') === 0 && statusEl) {
                            statusEl.textContent = 'This camera streams directly from its recorder over an untrusted certificate. ';
                            var trustLink = document.createElement('a');
                            trustLink.href = ticket.trustUrl;
                            trustLink.target = '_blank';
                            trustLink.rel = 'noopener';
                            trustLink.textContent = 'Trust the recorder';
                            statusEl.appendChild(trustLink);
                            statusEl.appendChild(document.createTextNode(', then reload this page.'));
                        }
                    }
                };
                socket.onclose = function (evt) {
                    var wasAlreadyClosed = closed;
                    closed = true;
                    // 1008 (policy violation) is the node telling us this viewer fell behind and it
                    // closed rather than splice a gap into the stream — see this function's header
                    // comment. start()'s retry wrapper reconnects with a fresh init segment.
                    if (evt.code === 1008) {
                        console.log('[live-view] server dropped us for falling behind — reconnecting');
                        mediaEndpoint.reportStreamEvent(cameraId, role || 'main', streamMode, 'server_disconnect', evt.code, evt.reason || null);
                    }
                    if (!wasAlreadyClosed && statusEl.textContent === '') {
                        statusEl.textContent = evt.reason || 'Disconnected.';
                    }
                    endSession();
                };
            });
        });

        videoEl.play().catch(function (e) {
            // Autoplay can still be blocked by browser policy even from a user-initiated click in
            // some configurations; the video stays paused with a visible play control, not an error.
            console.warn('[live-view] videoEl.play() rejected:', e);
        });

        return function stop() {
            closed = true;
            if (socket) { try { socket.close(); } catch (e) {} }
            try { if (mediaSource.readyState === 'open') mediaSource.endOfStream(); } catch (e) {}
            URL.revokeObjectURL(objectUrl);
            // Detached here, after closed=true and the socket/media-source teardown above, rather
            // than by the caller — clearing the element's src is what marks this session's
            // sourceBuffer "removed from parent media source" in the browser's own bookkeeping, so it
            // must happen last, once nothing in this closure can still call appendBuffer on it.
            videoEl.pause();
            videoEl.removeAttribute('src');
            videoEl.load();
            endSession();
        };
    }

    // Shared by the motion-badge rendering below and the AI-detection box overlay — badge/box
    // colors are both ultimately admin/auto-assigned hex values (Admin → Event Colors, or
    // DetectedObjectColorAssigner), so black text can't be assumed legible against either. Standard
    // sRGB relative luminance against the usual 0.5 midpoint; the fallback keeps prior behavior for
    // anything that isn't a plain 6-digit hex.
    function readableTextColor(hex) {
        if (typeof hex !== 'string' || !/^#[0-9a-fA-F]{6}$/.test(hex)) return '#000';
        var r = parseInt(hex.substr(1, 2), 16) / 255;
        var g = parseInt(hex.substr(3, 2), 16) / 255;
        var b = parseInt(hex.substr(5, 2), 16) / 255;
        function channel(c) { return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
        return luminance > 0.5 ? '#000' : '#fff';
    }

    // AI-category emoji, mirroring LarisVMS.Core.CocoCategoryMap.Emoji so the live badge shows the
    // same icon the Snapshots page does — duplicated here rather than fetched, since it's a fixed
    // 4-entry map and the whole point of this path is per-frame speed with no round trip.
    var AI_CATEGORY_EMOJI = { Human: '🚶', Vehicle: '🚗', Animal: '🐾' };
    function aiCategoryEmoji(category) { return AI_CATEGORY_EMOJI[category] || '📦'; }
    function capitalizeFirst(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }

    // Two independent sources feed one tile's badge row: camera-native DetectionKind badges
    // (lastDbDetectionsByCamera, refreshed on startMotionIndicatorPolling's own interval — that
    // source has no per-frame position/movement data at all, only "was this class seen recently")
    // and AI-category badges (aiMovingDetectionsByCamera, refreshed live over startDetectionOverlay's
    // own WebSocket — see that function for why this has to be the live per-frame Moving/Idle state
    // rather than a DB query: no "is this specific track moving right now" flag is ever persisted).
    // Either source updating re-renders the same combined row via renderDetectionBadges below.
    var lastDbDetectionsByCamera = {};
    var aiMovingDetectionsByCamera = {};

    function refreshBadgesForCamera(cameraId) {
        document.querySelectorAll('[data-camera-tile]').forEach(function (tile) {
            if (tile.dataset.cameraTile === cameraId) renderDetectionBadges(tile);
        });
    }

    // Rebuilt only when the combined signature actually changes, since the AI side can update every
    // frame — blindly replacing innerHTML that often would restart CSS transitions and fight any
    // text selection inside the tile.
    function renderDetectionBadges(tile) {
        var container = tile.querySelector('.live-detection-badges');
        if (!container) return;
        var cameraId = tile.dataset.cameraTile;
        var dbDetections = lastDbDetectionsByCamera[cameraId] || [];
        var aiDetections = aiMovingDetectionsByCamera[cameraId] || [];

        var signature = dbDetections.map(function (d) { return d.kind; }).join(',') +
            '|' + aiDetections.map(function (d) { return d.label; }).join(',');
        if (container.dataset.signature === signature) return;
        container.dataset.signature = signature;

        container.innerHTML = '';
        // One badge per class, never a summary — a camera seeing a person and a vehicle at the same
        // time has to show both. Spacing comes from the container's own flex gap.
        dbDetections.forEach(function (detection) {
            var badge = document.createElement('span');
            badge.className = 'badge';
            badge.style.backgroundColor = detection.colorHex;
            badge.style.color = readableTextColor(detection.colorHex);
            badge.title = detection.label + ' detected by the camera';
            badge.textContent = detection.emoji + ' ' + detection.label;
            container.appendChild(badge);
        });
        // AI badges only ever show currently-moving objects (idle/stationary ones don't badge) and
        // one per unique specific label — two moving vehicles of different kinds (a car and a truck)
        // both get their own badge rather than collapsing into one "Vehicle" badge.
        aiDetections.forEach(function (detection) {
            var color = detection.colorHex || '#22d3ee';
            var label = capitalizeFirst(detection.label);
            var badge = document.createElement('span');
            badge.className = 'badge';
            badge.style.backgroundColor = color;
            badge.style.color = readableTextColor(color);
            badge.title = label + ' is currently moving';
            badge.textContent = aiCategoryEmoji(detection.category) + ' ' + label;
            container.appendChild(badge);
        });
    }

    // Object detection plan decision 6: live-view box overlay, driven by a *separate* WebSocket
    // from the video stream itself (/live/{cameraId}/detections, not the fMP4 socket start() above
    // opens) — kept off the already-delicate binary fMP4 relay entirely. Two independent toggles
    // (Moving/Idle, decision 6's own "don't clutter the screen with static objects unless you want
    // to see them too") rather than one on/off switch; both default OFF, matching "boxes are a pure
    // client-side toggle" — nothing is drawn, and the socket isn't even opened, until at least one
    // is turned on by the caller.
    //
    // A third toggle (setShowConfidence) is a different kind of thing and deliberately does not join
    // that pair: it appends each box's score to its label and has no bearing on whether a box is
    // drawn or the socket is open. The toolbar disables it while both Moving and Idle are off, since
    // there would be nothing on screen for it to annotate.
    //
    // Coordinates arrive normalized (0-1) against the camera's own real aspect ratio (detection/
    // hardware-acceleration overhaul pass 1 — InferenceProfile.MapBoxToSource undoes whatever
    // decode-resolution/letterbox transform AI detection actually used internally, so nothing here
    // needs to know that ran on the Sub stream at some other resolution/aspect at all) — remapped
    // here using the exact same object-fit:contain letterbox math createFreezeOverlay's own show()
    // already uses, since drawing onto a canvas sized to the video's *on-screen* box (not its native
    // resolution) needs the same scale/offset either way. Before pass 1, a non-16:9 camera's boxes
    // were normalized against a fixed global decode resolution unrelated to the camera's own shape,
    // so they visibly drifted off the real video the more its aspect ratio differed from 16:9.
    //
    // `fsHandle` (optional — fullscreen-tile.js's wire() return value for this same tile) is how
    // this overlay stays in lockstep with zoom/pan: that module applies a CSS transform directly to
    // videoEl while zoomed/panned in fullscreen, which visually moves the video's content without
    // changing videoEl's own layout box — this canvas is a plain untransformed sibling with an
    // identical box, so without also applying that transform its boxes stay fixed at their
    // pre-zoom/pre-pan screen position while the video slides/scales underneath them. Applying the
    // exact same transform string (rather than re-deriving scale/panX/panY here) means the two can
    // never drift out of sync with each other.
    function startDetectionOverlay(cameraId, videoEl, fsHandle) {
        var canvas = document.createElement('canvas');
        canvas.className = 'live-detection-overlay';
        canvas.style.cssText = 'position:absolute; top:0; left:0; width:100%; height:100%; pointer-events:none; display:none;';
        var parent = videoEl.parentElement;
        if (parent) parent.insertBefore(canvas, videoEl.nextSibling);

        var socket = null;
        var latestBoxes = [];
        var showMoving = false;
        var showIdle = false;
        // Decoration only — never gates whether a box is drawn or whether the socket is open, which
        // is why updateActiveState below ignores it entirely and the toolbar disables it while both
        // of the two above are off (there would be nothing for it to decorate).
        var showConfidence = false;
        var stopped = false;
        var reconnectTimer = null;
        // Detection ticks on a client-relative *capture* timeline: each tick's captureAtMs is when
        // its frame was actually grabbed, derived on arrival as (arrival - ageMs) where ageMs is the
        // duration Vision Service measured on its own clock. Nothing here compares clocks across
        // machines — ageMs is a duration, and it's only ever subtracted from a local timestamp.
        //
        // Rendering then runs against a presentation clock (now - videoLatencyMs) and interpolates
        // between the two ticks bracketing it. Detection runs at ~7fps over ~18fps video, so drawing
        // a tick verbatim holds each box still for ~2.6 video frames, which reads as stepping;
        // interpolating removes that. Interpolation rather than prediction specifically because the
        // hold-back means the *next* tick is usually already in hand — extrapolating from one tick
        // would overshoot every time an object changed direction.
        var pendingTicks = [];
        var rafHandle = null;
        var frameCounter = 0;
        var lastBadgeTickMs = 0;
        // Last values actually rendered, kept so a frame with nothing new to say can redraw
        // identically (canvas resize, fullscreen transform change) without recomputing.
        var latestAlignmentMs = null;

        function draw() {
            canvas.style.transform = (fsHandle && fsHandle.isFullscreen()) ? fsHandle.getTransformCss() : '';

            var w = videoEl.clientWidth, h = videoEl.clientHeight;
            var nw = videoEl.videoWidth, nh = videoEl.videoHeight;
            var ctx = canvas.getContext('2d');
            if (w <= 0 || h <= 0 || nw <= 0 || nh <= 0) {
                ctx.clearRect(0, 0, canvas.width, canvas.height);
                return;
            }

            // Only on a real size change: assigning canvas.width/height resets the whole drawing
            // surface and reallocates its backing store even when the value is identical. That was
            // negligible at the old 6.7Hz tick cadence but this now runs per animation frame.
            if (canvas.width !== w || canvas.height !== h) {
                canvas.width = w;
                canvas.height = h;
            }
            var scale = Math.min(w / nw, h / nh);
            var dw = nw * scale, dh = nh * scale;
            var offsetX = (w - dw) / 2, offsetY = (h - dh) / 2;

            ctx.clearRect(0, 0, w, h);

            latestBoxes.forEach(function (box) {
                if (box.movementState === 'Moving' && !showMoving) return;
                if (box.movementState === 'Idle' && !showIdle) return;

                var x = offsetX + box.x * dw;
                var y = offsetY + box.y * dh;
                var boxW = box.w * dw;
                var boxH = box.h * dh;
                var color = box.colorHex || '#22d3ee';

                ctx.strokeStyle = color;
                ctx.lineWidth = 2;
                ctx.strokeRect(x, y, boxW, boxH);

                // Percentage rather than the raw 0-1 score: this sits on a moving video at 12px, and
                // "81%" reads at a glance where "0.81" does not. Guarded because confidence is a
                // relayed field — an older node, or a malformed tick, must degrade to the plain
                // label rather than painting "NaN%" over the video.
                // Mirror LarisVMS.Core.CocoCategoryMap.DisplayLabel: drop the specific label when it
                // adds nothing beyond the category. Today that's only Human (sole class "person") —
                // "Vehicle — truck" is kept in full.
                var label = (!box.label || box.category === 'Human' || box.label.toLowerCase() === box.category.toLowerCase())
                    ? box.category
                    : box.category + ' — ' + box.label;
                if (showConfidence && typeof box.confidence === 'number' && isFinite(box.confidence)) {
                    label += ' ' + Math.round(box.confidence * 100) + '%';
                }
                ctx.font = '12px sans-serif';
                var textWidth = ctx.measureText(label).width;
                var labelY = Math.max(0, y - 16);
                ctx.fillStyle = color;
                ctx.fillRect(x, labelY, textWidth + 8, 16);
                ctx.fillStyle = readableTextColor(color);
                ctx.fillText(label, x + 4, labelY + 12);
            });
        }

        // Derives this camera's "currently moving" AI badges straight from the same per-frame boxes
        // draw() uses, independent of the showMoving/showIdle *drawing* toggles below — those only
        // control what gets painted on the canvas, not what genuinely counts as moving right now.
        // One entry per unique specific label (a moving car and a moving truck both badge), never
        // Idle ones — see the module-scope aiMovingDetectionsByCamera doc comment for why this has
        // to be live per-frame state rather than a database query.
        function updateAiBadgeState() {
            var seen = {};
            var list = [];
            latestBoxes.forEach(function (box) {
                if (box.movementState !== 'Moving' || seen[box.label]) return;
                seen[box.label] = true;
                list.push({ label: box.label, category: box.category, colorHex: box.colorHex });
            });
            aiMovingDetectionsByCamera[cameraId] = list;
            refreshBadgesForCamera(cameraId);
        }

        function clearAiBadgeState() {
            delete aiMovingDetectionsByCamera[cameraId];
            refreshBadgesForCamera(cameraId);
        }

        // How far behind real time the video currently is, measured rather than assumed — see
        // BOX_RESIDUAL_LATENCY_MS and startSession's publishLiveTiming. Returns the fixed fallback
        // when there's no live session publishing for this element (overlay mounted over a tile in
        // playback mode, or between reconnects).
        function videoLatencyMs() {
            var t = videoEl._larisvmsLiveTiming;
            if (!t || t.lastFragmentArrivalMs === null || t.driftMs === null) return BOX_FALLBACK_LATENCY_MS;
            var sinceArrival = Date.now() - t.lastFragmentArrivalMs;
            // A stale handle means fragments stopped arriving (upstream stall). Clamping to the
            // fallback keeps boxes roughly placed instead of letting the presentation clock run away
            // backwards as sinceArrival grows without bound.
            if (sinceArrival > BOX_QUEUE_MAX_AGE_SECONDS * 1000) return BOX_FALLBACK_LATENCY_MS;
            // Drift measured against the video's live currentTime, not a sampled driftMs. A sample
            // only refreshes on fragment arrival or driftTimer's tick, so between refreshes
            // sinceArrival grew with the clock while the sampled drift stood still: the two cancel,
            // the presentation clock froze, and boxes only moved 2-3 times a second however fast
            // detections arrived. Measured live, drift shrinks exactly as fast as sinceArrival
            // grows (playback advances at ~1x), so the presentation clock advances smoothly.
            if (typeof t.bufferedEndSec === 'number') {
                var liveDriftMs = (t.bufferedEndSec - videoEl.currentTime) * 1000;
                return sinceArrival + liveDriftMs + BOX_RESIDUAL_LATENCY_MS;
            }
            return sinceArrival + t.driftMs + BOX_RESIDUAL_LATENCY_MS;
        }

        function lerp(a, b, f) { return a + (b - a) * f; }

        // Boxes for `atMs` on the capture timeline, interpolated between the two ticks bracketing it
        // and matched across them by trackId (assigned by ByteTracker, relayed end to end). Geometry
        // is interpolated; everything else (label, category, colour, movement state) comes from the
        // newer tick, since those are classifications rather than positions and shouldn't be blended.
        function boxesAt(atMs) {
            if (pendingTicks.length === 0) return null;

            var olderIndex = -1;
            for (var i = 0; i < pendingTicks.length; i++) {
                if (pendingTicks[i].captureAtMs <= atMs) olderIndex = i;
                else break;
            }
            // Presentation clock hasn't reached the oldest tick yet — nothing to show for this
            // instant, so hold whatever is already drawn rather than jumping ahead to a future
            // position. (The clock catches up within a frame or two of a session starting.)
            if (olderIndex < 0) return null;

            // Ticks before the bracket actually in use are never needed again — the presentation
            // clock only moves forward, so nothing will ever interpolate against an instant older
            // than what was just rendered. Trimming them here (rather than relying only on
            // BOX_QUEUE_MAX_AGE_SECONDS's safety net in renderFrame) keeps the queue at the ~2 ticks
            // interpolation actually needs instead of dragging several seconds of already-rendered
            // history behind it on every frame — confirmed live via box_alignment: 38-44 queued
            // ticks (~5-6s) when interpolation only ever looks at the newest two.
            if (olderIndex > 0) pendingTicks = pendingTicks.slice(olderIndex);

            var older = pendingTicks[0];
            var newer = pendingTicks.length > 1 ? pendingTicks[1] : null;
            // Past the newest tick: detection is momentarily behind the video. Hold the newest known
            // position rather than extrapolating — see the pendingTicks comment on why.
            if (!newer) return older.boxes;

            var span = newer.captureAtMs - older.captureAtMs;
            if (span <= 0) return newer.boxes;
            var f = Math.min(1, Math.max(0, (atMs - older.captureAtMs) / span));

            var newerById = {};
            for (var n = 0; n < newer.boxes.length; n++) newerById[newer.boxes[n].trackId] = newer.boxes[n];

            var out = [];
            for (var o = 0; o < older.boxes.length; o++) {
                var a = older.boxes[o];
                var b = newerById[a.trackId];
                // A track the newer tick doesn't have (object just left, or the model dropped it for
                // a frame) draws at its last known position rather than vanishing mid-interpolation.
                if (!b) { out.push(a); continue; }
                out.push({
                    trackId: b.trackId, category: b.category, label: b.label,
                    movementState: b.movementState, confidence: b.confidence, colorHex: b.colorHex,
                    x: lerp(a.x, b.x, f), y: lerp(a.y, b.y, f),
                    w: lerp(a.w, b.w, f), h: lerp(a.h, b.h, f)
                });
            }
            // Tracks only in the newer tick (just appeared) draw un-interpolated — there's no earlier
            // position to blend from, and holding them back until the next tick would make every new
            // object appear ~150ms late.
            for (var k = 0; k < newer.boxes.length; k++) {
                var nb = newer.boxes[k];
                var seen = false;
                for (var m = 0; m < older.boxes.length; m++) {
                    if (older.boxes[m].trackId === nb.trackId) { seen = true; break; }
                }
                if (!seen) out.push(nb);
            }
            return out;
        }

        // Replaces the old fixed-cadence pump: rendering at display rate is what turns 7fps
        // detections into smooth motion, and it also stops the overlay repainting on a timer when
        // nothing has moved.
        function renderFrame() {
            rafHandle = null;
            // Not just `stopped`: toggling both box types off cancels the pending frame, and without
            // this the in-flight frame would immediately reschedule and keep the loop alive forever.
            if (stopped || !(showMoving || showIdle)) return;

            var latency = videoLatencyMs();
            var presentationNowMs = Date.now() - latency;
            var boxes = boxesAt(presentationNowMs);
            if (boxes) {
                latestBoxes = boxes;
                latestAlignmentMs = latency;
                draw();
                // Deliberately NOT per frame: this writes the module-scope badge state and touches
                // the DOM through refreshBadgesForCamera. What it derives (which labels are moving)
                // can only change when a new detection tick arrives, not between interpolated
                // frames, so it runs on tick changes only — 60Hz DOM writes per tile for data that
                // changes at ~7Hz would be pure waste.
                var newestTickMs = pendingTicks.length > 0
                    ? pendingTicks[pendingTicks.length - 1].captureAtMs : 0;
                if (newestTickMs !== lastBadgeTickMs) {
                    lastBadgeTickMs = newestTickMs;
                    updateAiBadgeState();
                }
            }

            // Drop ticks well behind the presentation clock (not behind *now*) — they've already been
            // rendered through, and the clock is deliberately running BOX-latency in the past.
            var cutoffMs = presentationNowMs - BOX_QUEUE_MAX_AGE_SECONDS * 1000;
            if (pendingTicks.length > 0 && pendingTicks[0].captureAtMs < cutoffMs) {
                pendingTicks = pendingTicks.filter(function (t) { return t.captureAtMs >= cutoffMs; });
            }

            if (++frameCounter % BOX_ALIGNMENT_REPORT_EVERY_FRAMES === 0) reportAlignment();
            scheduleFrame();
        }

        function scheduleFrame() {
            if (stopped || rafHandle !== null) return;
            rafHandle = window.requestAnimationFrame(renderFrame);
        }

        // Turns "the boxes look laggy" into milliseconds, the same way decode_health did for the
        // video path — measured video latency, how old the detections themselves were, and how wide
        // a gap the interpolation is currently bridging.
        function reportAlignment() {
            var t = videoEl._larisvmsLiveTiming;
            var spanMs = pendingTicks.length >= 2
                ? pendingTicks[pendingTicks.length - 1].captureAtMs - pendingTicks[pendingTicks.length - 2].captureAtMs
                : null;
            var newestAgeMs = pendingTicks.length > 0
                ? pendingTicks[pendingTicks.length - 1].ageMs : null;
            var detail = 'videoLatencyMs=' + Math.round(latestAlignmentMs === null ? videoLatencyMs() : latestAlignmentMs) +
                ' measured=' + (t && t.driftMs !== null ? 'yes' : 'no-fallback') +
                (t && t.driftMs !== null ? ' driftMs=' + Math.round(t.driftMs) : '') +
                (newestAgeMs !== null ? ' detectionAgeMs=' + Math.round(newestAgeMs) : '') +
                (spanMs !== null ? ' tickSpanMs=' + Math.round(spanMs) : '') +
                ' queuedTicks=' + pendingTicks.length;
            mediaEndpoint.reportStreamEvent(cameraId, 'main', null, 'box_alignment',
                latestAlignmentMs === null ? null : Math.round(latestAlignmentMs), detail);
        }

        function connect() {
            if (stopped) return;
            var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
            socket = new WebSocket(proto + '//' + location.host + '/live/' + cameraId + '/detections');
            socket.onmessage = function (evt) {
                var parsed;
                try {
                    parsed = JSON.parse(evt.data);
                } catch (e) {
                    return; // one malformed tick — next one supersedes it
                }
                var boxes = (parsed && parsed.boxes) || [];
                // No usable delay budget (pipeline hasn't processed a frame yet, or this tick fell
                // back after a skipped/failed poll — see DetectionOverlayHandler.FetchSnapshotAsync)
                // — draw immediately rather than waiting on a hold-back that can never elapse.
                if (!parsed || typeof parsed.ageMs !== 'number' || !isFinite(parsed.ageMs)) {
                    latestBoxes = boxes;
                    draw();
                    updateAiBadgeState();
                    return;
                }
                // ageMs (how old this snapshot already was when Vision Service sent it — see
                // VisionLiveDetectionsResponse's doc comment) is a duration computed entirely on
                // Vision Service's own clock, so subtracting it from this machine's own arrival
                // timestamp places the tick on a local capture timeline without ever comparing the
                // two clocks against each other.
                var captureAtMs = Date.now() - parsed.ageMs;
                // Out-of-order arrival would break the bracketing scan in boxesAt, which assumes
                // ascending capture times. Ticks come off one polled socket so this is unexpected;
                // dropping the straggler is cheaper and safer than re-sorting every push.
                if (pendingTicks.length > 0 && captureAtMs <= pendingTicks[pendingTicks.length - 1].captureAtMs) return;
                pendingTicks.push({ captureAtMs: captureAtMs, ageMs: parsed.ageMs, boxes: boxes });
                scheduleFrame();
            };
            socket.onclose = function () {
                socket = null;
                pendingTicks = [];
                if (stopped || !(showMoving || showIdle)) return;
                // Routine reconnect, same fixed-delay spirit as the freeze-overlay/retry story the
                // video socket already has — this feed is lower-stakes (a missed box or two is not
                // a broken stream), so no exponential backoff needed.
                reconnectTimer = setTimeout(function () { reconnectTimer = null; connect(); }, 2000);
            };
            socket.onerror = function () { /* onclose fires next and handles reconnect */ };
        }

        function updateActiveState() {
            var anyOn = showMoving || showIdle;
            canvas.style.display = anyOn ? 'block' : 'none';
            if (anyOn && !socket && !reconnectTimer) {
                connect();
            } else if (!anyOn) {
                if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
                if (rafHandle !== null) { window.cancelAnimationFrame(rafHandle); rafHandle = null; }
                if (socket) { try { socket.close(); } catch (e) {} socket = null; }
                pendingTicks = [];
                latestBoxes = [];
                draw();
                clearAiBadgeState();
            }
        }

        return {
            setShowMoving: function (on) { showMoving = !!on; updateActiveState(); draw(); },
            setShowIdle: function (on) { showIdle = !!on; updateActiveState(); draw(); },
            // No updateActiveState: this changes what a box's label says, never whether any box is
            // drawn, so it must not open or close the detections socket.
            setShowConfidence: function (on) { showConfidence = !!on; draw(); },
            stop: function () {
                stopped = true;
                if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
                if (rafHandle !== null) { window.cancelAnimationFrame(rafHandle); rafHandle = null; }
                pendingTicks = [];
                if (socket) { try { socket.close(); } catch (e) {} }
                if (canvas.parentElement) canvas.parentElement.removeChild(canvas);
                clearAiBadgeState();
            }
        };
    }

    // M8: polls GET /api/cameras/motion-state (a list of camera IDs with a recent motion-span
    // checkpoint — see TimelineService.GetCamerasWithActiveMotionAsync) and toggles each matching
    // tile's `.live-motion-badge`. A poll, not a push over the already-open live WebSocket — motion
    // state changes on the order of seconds, not frame-by-frame, so a lightweight interval fits the
    // "no build step, framework-free" house style better than threading a second message type
    // through the live fMP4 socket for this. Every tile is looked up by data-camera-tile fresh each
    // tick rather than cached once, so tiles added/removed from the DOM after this starts (there
    // are none today, but Views/Play could reuse this later) are picked up correctly.
    function startMotionIndicatorPolling(intervalMs) {
        async function tick() {
            // Both signals on the same tick, in parallel — they're read together to decide one
            // tile's badges, and a detection is strictly more informative than the plain motion it
            // usually accompanies (see renderDetectionBadges, which suppresses the generic badge
            // when a classified one is showing).
            var activeIds = [];
            var detectionStates = [];
            try {
                var results = await Promise.all([
                    fetch('/api/cameras/motion-state'),
                    fetch('/api/cameras/detection-state')
                ]);
                if (results[0].ok) activeIds = await results[0].json();
                // A deployment whose cameras report no object classes just gets an empty list here;
                // an outright failure leaves detections alone rather than clearing existing badges.
                if (results[1].ok) detectionStates = await results[1].json();
            } catch (e) {
                return; // transient fetch failure — next tick tries again, no need to surface this
            }
            var activeSet = {};
            activeIds.forEach(function (id) { activeSet[id] = true; });
            detectionStates.forEach(function (state) { lastDbDetectionsByCamera[state.cameraId] = state.detections; });

            document.querySelectorAll('[data-camera-tile]').forEach(function (tile) {
                var cameraId = tile.dataset.cameraTile;
                var detections = lastDbDetectionsByCamera[cameraId] || [];
                renderDetectionBadges(tile);

                var badge = tile.querySelector('.live-motion-badge');
                if (!badge) return;
                // A classified detection replaces the generic badge rather than stacking with it —
                // "🚶 Human" already implies motion, and showing both just crowds a small tile.
                badge.classList.toggle('d-none', !activeSet[cameraId] || detections.length > 0);
            });
        }

        tick();
        setInterval(tick, intervalMs);
    }

    window.larisvmsLiveView = {
        start: start,
        startMotionIndicatorPolling: startMotionIndicatorPolling,
        startDetectionOverlay: startDetectionOverlay
    };
})();

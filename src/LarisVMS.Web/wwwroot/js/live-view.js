// Live view MSE player (M5). One WebSocket per watched camera, straight to /live/{cameraId} on this
// same host (proxied server-side to the recorder node — see Program.cs). The server always sends the
// fMP4 init segment as the very first binary message, then one fragment per subsequent message;
// this only has to hand each message to a SourceBuffer in order, never parse the bytes itself.
(function () {
    'use strict';

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
    // resync markers — the node's LiveViewerHandler deliberately drops the *oldest* buffered
    // fragment (not the newest) when a slow client can't keep up, so a stalled network connection
    // doesn't back-pressure the recording pipeline itself. That's the right tradeoff for recording,
    // but it means a client that falls behind (confirmed cause: roaming between mesh WiFi access
    // points) gets a gap in the middle of its byte stream, which corrupts everything after it for
    // this SourceBuffer — there is no getting the same session decodable again once that happens,
    // only starting a fresh one (new init segment, clean MediaSource). `start()` is the auto-retrying
    // wrapper every caller should use; `startSession()` is one single attempt.
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

    // How far behind its own live edge a tile is allowed to fall before being pulled forward. Well
    // clear of the normal sub-second lag between "bytes appended" and "frame decoded" (so ordinary
    // healthy playback never triggers a correction), but tight enough that a reconnected tile visibly
    // rejoins its neighbors rather than staying a beat behind.
    var DRIFT_THRESHOLD_SECONDS = 3;
    var DRIFT_CHECK_INTERVAL_MS = 3000;
    // Below this, catch-up stops and playbackRate returns to normal — deliberately smaller than
    // DRIFT_THRESHOLD_SECONDS so a tile settles just inside the "acceptable" band rather than
    // oscillating in and out of catch-up right at the threshold.
    var CATCHUP_STOP_THRESHOLD_SECONDS = 0.75;
    var CATCHUP_PLAYBACK_RATE = 1.5;
    // Beyond this, a playbackRate catch-up would take too long to be worth it (confirmed cause:
    // browsers throttle setInterval on a backgrounded/unfocused tab, so a real gap of a minute or
    // more can go undetected until the tab regains focus) — falls back to a hard seek instead, same
    // as the stall-recovery path below.
    var HARD_RESYNC_THRESHOLD_SECONDS = 15;

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

        // "Catching up" badge (catchup-badge.js), shown while this tile is running at
        // CATCHUP_PLAYBACK_RATE to close a drift gap. The tile's positioned container is the
        // video's parent — same element createFreezeOverlay overlays onto.
        function showCatchupBadge() {
            if (window.larisvmsCatchupBadge) window.larisvmsCatchupBadge.show(videoEl.parentElement, CATCHUP_PLAYBACK_RATE + '×');
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
        var closed = false;
        var seekedToLiveEdge = false;
        var driftTimer = null;

        // An appendBuffer failure is never recoverable for this session (a torn-down/replaced
        // MediaSource throws "removed from parent media source" on the very next call, and retrying
        // the same fragment forever would just repeat the same throw for every future WebSocket
        // message) — so any exception here ends the session the same way stop() does, instead of
        // leaving the socket open and spamming the same error on every subsequent fragment.
        function appendNext() {
            if (closed || !sourceBuffer || sourceBuffer.updating || pending.length === 0) return;
            try {
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
            }

            // A fresh <video> defaults to currentTime=0, but this live leg has been running (and its
            // fMP4 timestamps incrementing) since ffmpeg started — not since this viewer connected —
            // so a late joiner's first buffered range typically starts well past 0. MSE won't advance
            // readyState past HAVE_METADATA (no error, just stuck) until currentTime falls inside a
            // buffered range, so without this a live-view session silently never plays: data arrives
            // and appendBuffer never throws, but nothing is ever positioned where it's buffered. Seeks
            // once, the first time any range becomes buffered, to the start of the newest range (the
            // most recently appended data, i.e. as close to "live" as what's arrived so far allows).
            sourceBuffer.addEventListener('updateend', function () {
                if (seekedToLiveEdge || sourceBuffer.buffered.length === 0) return;
                seekedToLiveEdge = true;
                jumpToLiveEdge('seeked to live edge');
            });
            // Ongoing counterpart to the one-time seek above: the buffered range is a sliding window
            // (the browser evicts old data as new fragments arrive, and the node's own slow-client
            // handling drops the *oldest* buffered fragment when a viewer falls behind — see this
            // function's own header comment), so currentTime can drift into now-evicted territory
            // later in a long-running session too, not just at startup. When that happens playback
            // stalls with no error and no visible frame (MSE has genuinely nothing buffered at that
            // position) until either the browser's own gap-jump heuristics kick in on their own
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
            // Fix: never seek to correct ordinary drift. Speed up playback instead, so the decoder
            // catches up frame-by-frame through real, already-decodable data — zero discontinuity,
            // so zero risk of landing off a keyframe. Only beyond HARD_RESYNC_THRESHOLD_SECONDS
            // (drift too large for a catch-up to close in reasonable time) does this fall back to a
            // hard seek — reusing jumpToLiveEdge()'s buffered.start() target, the same anchor the
            // stall-recovery path below already relies on being safe.
            driftTimer = setInterval(function () {
                // Ownership guard, first thing: this same <video> element gets handed to a completely
                // different player when a tile/cell is toggled into playback mode (playback-player.js
                // attaches its own MediaSource to it). A timer belonging to this now-superseded live
                // session must never touch the element again — confirmed live as a real failure, not
                // theoretical: leaked timers kept seeking the element while the playback player owned
                // it, producing exactly the interleaved multi-position "loop" this guard prevents.
                if (closed || ended || videoEl.src !== objectUrl) return;
                if (videoEl.paused || sourceBuffer.buffered.length === 0) return;
                var lastRange = sourceBuffer.buffered.length - 1;
                var liveEdge = sourceBuffer.buffered.end(lastRange);
                var drift = liveEdge - videoEl.currentTime;
                if (drift > HARD_RESYNC_THRESHOLD_SECONDS) {
                    // Deliberately NOT a seek. Every seek-based correction tried here has been a
                    // regression: jumpToLiveEdge() targets buffered.start(), which is *behind*
                    // currentTime whenever currentTime is already inside the buffered range (the case
                    // here) — that made drift worse and re-fired forever, needing a page refresh to
                    // clear. Seeking near buffered.end() instead isn't keyframe-safe and threw real
                    // decode errors. Drift this large means this session is unrecoverably behind
                    // (typically a long-backgrounded tab whose throttled timer let a huge gap build);
                    // ending it hands off to start()'s existing retry wrapper, which builds a fresh
                    // MediaSource that begins cleanly at the live edge — the one path already proven
                    // to recover correctly.
                    console.log('[live-view] ' + drift.toFixed(1) + 's behind live edge — too far to catch up, restarting session');
                    videoEl.playbackRate = 1;
                    hideCatchupBadge();
                    closed = true;
                    if (socket) { try { socket.close(); } catch (e2) {} }
                    endSession();
                } else if (drift > DRIFT_THRESHOLD_SECONDS) {
                    if (videoEl.playbackRate !== CATCHUP_PLAYBACK_RATE) {
                        console.log('[live-view] catching up to live edge (' + drift.toFixed(1) + 's behind)');
                        videoEl.playbackRate = CATCHUP_PLAYBACK_RATE;
                        showCatchupBadge();
                    }
                } else if (drift < CATCHUP_STOP_THRESHOLD_SECONDS && videoEl.playbackRate !== 1) {
                    videoEl.playbackRate = 1;
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

            var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
            var roleQuery = role === 'sub' ? '?role=sub' : '';
            socket = new WebSocket(proto + '//' + location.host + '/live/' + cameraId + roleQuery);
            socket.binaryType = 'arraybuffer';

            statusEl.textContent = 'Connecting…';

            socket.onmessage = function (evt) {
                fragmentCount++;
                if (fragmentCount === 1) {
                    console.log('[live-view] init segment received,', evt.data.byteLength, 'bytes, mimeType=', mimeType);
                } else if (fragmentCount <= 5 || fragmentCount % 50 === 0) {
                    console.log('[live-view] fragment', fragmentCount, ',', evt.data.byteLength, 'bytes, video.readyState=', videoEl.readyState, 'video.paused=', videoEl.paused);
                }
                statusEl.textContent = '';
                pending.push(new Uint8Array(evt.data));
                appendNext();
            };
            socket.onerror = function () {
                statusEl.textContent = 'Connection error.';
            };
            socket.onclose = function (evt) {
                var wasAlreadyClosed = closed;
                closed = true;
                if (!wasAlreadyClosed && statusEl.textContent === '') {
                    statusEl.textContent = evt.reason || 'Disconnected.';
                }
                endSession();
            };
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
        var stopped = false;
        var reconnectTimer = null;

        function draw() {
            canvas.style.transform = (fsHandle && fsHandle.isFullscreen()) ? fsHandle.getTransformCss() : '';

            var w = videoEl.clientWidth, h = videoEl.clientHeight;
            var nw = videoEl.videoWidth, nh = videoEl.videoHeight;
            var ctx = canvas.getContext('2d');
            if (w <= 0 || h <= 0 || nw <= 0 || nh <= 0) {
                ctx.clearRect(0, 0, canvas.width, canvas.height);
                return;
            }

            canvas.width = w;
            canvas.height = h;
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

                var label = box.category + ' — ' + box.label;
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

        function connect() {
            if (stopped) return;
            var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
            socket = new WebSocket(proto + '//' + location.host + '/live/' + cameraId + '/detections');
            socket.onmessage = function (evt) {
                try {
                    latestBoxes = JSON.parse(evt.data) || [];
                } catch (e) {
                    return; // one malformed tick — next one supersedes it
                }
                draw();
                updateAiBadgeState();
            };
            socket.onclose = function () {
                socket = null;
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
                if (socket) { try { socket.close(); } catch (e) {} socket = null; }
                latestBoxes = [];
                draw();
                clearAiBadgeState();
            }
        }

        return {
            setShowMoving: function (on) { showMoving = !!on; updateActiveState(); draw(); },
            setShowIdle: function (on) { showIdle = !!on; updateActiveState(); draw(); },
            stop: function () {
                stopped = true;
                if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
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

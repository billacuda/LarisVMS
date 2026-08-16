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
    function start(cameraId, videoEl, statusEl, codecHint, hasAudio) {
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
            currentStop = startSession(cameraId, videoEl, statusProxy, codecHint, hasAudio, onFrameVisible, function onEnded() {
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
    function startSession(cameraId, videoEl, statusEl, codecHint, hasAudio, onFrameVisible, onEnded) {
        var ended = false;
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
            videoEl.playbackRate = 1;
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
        // harmless. Same "never removed" lifetime as the error/stalled listeners just below — an
        // existing pattern in this function, not something newly introduced here.
        videoEl.addEventListener('playing', onFrameVisible);
        videoEl.addEventListener('error', function () {
            var err = videoEl.error;
            var msg = 'Video decode error' + (err ? ' (code ' + err.code + ')' : '') + '.';
            statusEl.textContent = msg;
            console.error('[live-view]', msg, err, 'mimeType=', mimeType, 'fragments received=', fragmentCount);
            closed = true;
            if (socket) { try { socket.close(); } catch (e2) {} }
            endSession();
        });
        videoEl.addEventListener('stalled', function () {
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
            videoEl.addEventListener('waiting', function () {
                if (closed || sourceBuffer.buffered.length === 0) return;
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
                    closed = true;
                    if (socket) { try { socket.close(); } catch (e2) {} }
                    endSession();
                } else if (drift > DRIFT_THRESHOLD_SECONDS) {
                    if (videoEl.playbackRate !== CATCHUP_PLAYBACK_RATE) {
                        console.log('[live-view] catching up to live edge (' + drift.toFixed(1) + 's behind)');
                        videoEl.playbackRate = CATCHUP_PLAYBACK_RATE;
                    }
                } else if (drift < CATCHUP_STOP_THRESHOLD_SECONDS && videoEl.playbackRate !== 1) {
                    videoEl.playbackRate = 1;
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
            socket = new WebSocket(proto + '//' + location.host + '/live/' + cameraId);
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
            var activeIds;
            try {
                var resp = await fetch('/api/cameras/motion-state');
                if (!resp.ok) return;
                activeIds = await resp.json();
            } catch (e) {
                return; // transient fetch failure — next tick tries again, no need to surface this
            }
            var activeSet = {};
            activeIds.forEach(function (id) { activeSet[id] = true; });

            document.querySelectorAll('[data-camera-tile]').forEach(function (tile) {
                var badge = tile.querySelector('.live-motion-badge');
                if (!badge) return;
                badge.classList.toggle('d-none', !activeSet[tile.dataset.cameraTile]);
            });
        }

        tick();
        setInterval(tick, intervalMs);
    }

    window.larisvmsLiveView = { start: start, startMotionIndicatorPolling: startMotionIndicatorPolling };
})();

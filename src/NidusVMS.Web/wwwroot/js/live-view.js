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
    // AAC-LC — what NidusVMS's cameras report (`CameraStream.HasAudio`) actually send in practice.
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

        function launch() {
            sessionStartedAt = Date.now();
            currentStop = startSession(cameraId, videoEl, statusEl, codecHint, hasAudio, function onEnded() {
                if (stoppedByUser) return;
                // A session that ran a while before failing is a transient blip (wifi roam, brief
                // network hiccup) — reset backoff so recovery stays fast. One that fails immediately,
                // repeatedly, is more likely a real problem (node/camera actually down), where
                // retrying every 2s forever would just hammer it for no benefit.
                retryDelayMs = (Date.now() - sessionStartedAt > 15000) ? 2000 : Math.min(retryDelayMs * 2, 30000);
                statusEl.textContent = 'Reconnecting…';
                retryTimer = setTimeout(function () { retryTimer = null; launch(); }, retryDelayMs);
            });
        }
        launch();

        return function stop() {
            stoppedByUser = true;
            if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
            if (currentStop) currentStop();
        };
    }

    // Runs exactly one session attempt and returns its stop() function. `onEnded` fires exactly
    // once, however the session stops — explicit stop() call, decode error, or WebSocket
    // close/error — so start()'s retry wrapper above has one place to decide whether to reconnect.
    function startSession(cameraId, videoEl, statusEl, codecHint, hasAudio, onEnded) {
        var ended = false;
        function endSession() {
            if (ended) return;
            ended = true;
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
            try {
                sourceBuffer = mediaSource.addSourceBuffer(mimeType);
            } catch (e) {
                statusEl.textContent = 'This browser cannot decode this stream (' + mimeType + ').';
                return;
            }
            sourceBuffer.addEventListener('updateend', appendNext);
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
                var lastRange = sourceBuffer.buffered.length - 1;
                videoEl.currentTime = sourceBuffer.buffered.start(lastRange);
                console.log('[live-view] seeked to live edge, currentTime=', videoEl.currentTime, 'buffered=', sourceBuffer.buffered.start(lastRange), '-', sourceBuffer.buffered.end(lastRange));
            });
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

    window.nidusvmsLiveView = { start: start };
})();

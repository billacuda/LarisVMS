// M7 playback: per-camera MSE player (larisvmsPlaybackPlayer) plus the Pages/Playback page glue
// (larisvmsPlaybackPage) that wires the view picker, both timelines (timeline.js), and one player
// per camera in the selected view together. Each segment file is already a self-contained fMP4
// (its own ftyp+moov+moof+mdat, same shape live-view.js's init segment is) — MSE natively accepts
// a fresh initialization segment mid-stream, so a tile just fetches one segment's full bytes per
// seek and appends them, rather than needing any server-side timestamp-rebasing/concatenation.
// Simpler and lower-risk than synthesizing one continuous stream server-side, at the cost of a
// decode restart at every segment boundary — see the CHANGELOG's Known limitations for what that
// trades away.
(function () {
    'use strict';

    var AVC_VIDEO_CANDIDATES = ['avc1.640028', 'avc1.4d0028'];
    var HEVC_VIDEO_CANDIDATES = ['hvc1.1.6.L93.B0', 'hev1.1.6.L93.B0'];
    var AUDIO_CODEC = 'mp4a.40.2';
    // M18 follow-up: matches Program.cs's own MinSeekSecondsForPartialFetch on the node — see
    // loadSegment's own comment for why this exists on both ends.
    var MIN_SEEK_SECONDS_FOR_PARTIAL_FETCH = 2.0;
    // How long to wait for the node to even start responding to /playback-segment before giving up
    // on this attempt — confirmed live as a real gap: a merely *slow* (not failed, not aborted)
    // response, e.g. a cold read of an older segment off SMB-backed storage, left `await fetch`
    // pending forever with nothing to time it out. Unlike a network error or an HTTP error status,
    // that never reaches any of this file's existing recovery paths (those all require a response,
    // or a decode failure, to have happened at all) — the tile just sat on "Loading…" until the user
    // forced a real reload by hand. Generous on purpose: a cold SMB read can legitimately take
    // several seconds, and this only needs to catch "stuck", not shave latency off a normal slow
    // read.
    var PLAYBACK_SEGMENT_FETCH_TIMEOUT_MS = 20000;

    function pickMimeType(codecHint, hasAudio) {
        if (!window.MediaSource) return null;
        var hint = (codecHint || '').toLowerCase();
        var videoCandidates = /hevc|h\.?265/.test(hint) ? HEVC_VIDEO_CANDIDATES
            : /avc|h\.?264/.test(hint) ? AVC_VIDEO_CANDIDATES
            : AVC_VIDEO_CANDIDATES.concat(HEVC_VIDEO_CANDIDATES);
        var audioSuffix = hasAudio ? (',' + AUDIO_CODEC) : '';
        var ordered = videoCandidates.map(function (v) { return 'video/mp4; codecs="' + v + audioSuffix + '"'; });
        ordered.push('video/mp4');
        for (var i = 0; i < ordered.length; i++) {
            if (MediaSource.isTypeSupported(ordered[i])) return ordered[i];
        }
        return null;
    }

    function createTile(cameraId, videoEl, statusEl, codecHint, hasAudio) {
        var segments = []; // [{id, startUtc, endUtc}] as epoch ms, sorted by startUtc
        var currentSegmentId = null;
        // Media-timeline value corresponding to the loaded segment's wall-clock start. Usually 0,
        // but not guaranteed — see the buffered-range handling in loadSegment.
        var currentSegmentTimeOrigin = 0;
        var loadToken = 0; // bumped on every seek so a superseded in-flight fetch's *result* is a no-op on arrival
        var abortController = null; // actually cancels the superseded fetch itself, not just its result
        // M16: confirmed live as the cause of "8x speed quietly resets to 1x after a few seconds" —
        // teardown() calls videoEl.load() on every segment transition (including the natural
        // end-of-segment auto-advance below, not just an explicit seek), and browsers reset
        // playbackRate back to 1 when load() runs. Every segment load re-applies this value (see
        // tryStartPlaybackOnce/loadWholeBufferFallback) instead of trusting the property to persist
        // across it, so a fast-forward rate now survives however many segment boundaries playback
        // crosses. setPlaybackRate (below) is the only writer.
        var desiredPlaybackRate = 1;
        // Segment ids whose fetch failed (404/500/etc.) this page load — confirmed live as a real
        // amplifier of whatever caused the original failure: the tape-scrubber drag fires throttled
        // scrub ticks roughly every 120ms, and since a failed fetch never sets currentSegmentId, a
        // slow drag through a single bad segment's time range re-triggered the exact same doomed
        // fetch on every tick — one missing file turning into a dozen-plus repeated 404s in a
        // couple of seconds. Once a segment id fails, later seeks into its range go straight to the
        // error status without hitting the network again. Cleared implicitly on page reload, not
        // persisted — if the file really does reappear (a StorageManager/retention timing issue
        // resolving itself, say), a fresh page load will try it again rather than remembering a
        // failure forever.
        var knownBadSegmentIds = {};

        // Look-ahead buffering (M18): the immediately-next segment's full bytes, fetched in the
        // background while the current one is still playing, so the 'ended' handler's transition
        // can skip the network round trip entirely instead of starting the fetch cold at that exact
        // moment. Confirmed live as the dominant cause of a visible "Loading…" blank at every ~60s
        // segment boundary during faster-than-1x playback (the fetch+decode-setup latency is the
        // same in wall-clock terms regardless of speed, so it eats a proportionally bigger bite out
        // of a segment that's playing out in less real time, and recurs more often per minute of
        // wall time too). Only ever holds one entry — the single segment expected to play next in
        // sequence — not a general cache; an arbitrary scrub/seek always misses it and falls through
        // to the normal fetch path unchanged. { id, promise<Uint8Array|null> }.
        var prefetch = null;

        function schedulePrefetch(afterSegment) {
            var next = nextSegmentAfter(afterSegment.startUtc);
            if (!next || knownBadSegmentIds[next.id] || (prefetch && prefetch.id === next.id)) return;
            var id = next.id;
            var p = fetch('/playback-segment/' + cameraId + '/' + id)
                .then(function (r) {
                    if (!r.ok) {
                        if (r.status === 404) knownBadSegmentIds[id] = true;
                        throw new Error('prefetch failed: ' + r.status);
                    }
                    return r.arrayBuffer();
                })
                .then(function (buf) { return new Uint8Array(buf); })
                .catch(function () {
                    // Swallowed deliberately — a failed prefetch just means the next real transition
                    // falls back to fetching normally (or, for a 404, hits the existing
                    // knownBadSegmentIds short-circuit in seekTo before ever reaching loadSegment).
                    if (prefetch && prefetch.id === id) prefetch = null;
                    return null;
                });
            prefetch = { id: id, promise: p };
        }

        // Segment-transition freeze frame: teardown() below blanks the <video> element immediately
        // (removeAttribute('src') + load()), and loading the next segment's first frame takes real
        // time even on a prefetch hit (a MediaSource/SourceBuffer append is still async) — confirmed
        // live as a black flash at every segment boundary despite prefetching already eliminating the
        // network wait. A canvas snapshot of the last painted frame, laid over the video and matched
        // to its own object-fit:contain box, covers that gap; it's removed once the new segment has
        // actually produced a frame to show ('seeked' or 'playing', whichever fires first) or once a
        // load attempt ends in an error with nothing to reveal. Lazily created and left in the DOM for
        // this tile's lifetime — cheap, and the returned teardown() hides it on tile disposal.
        var freezeCanvas = null;

        function ensureFreezeCanvas() {
            if (freezeCanvas) return freezeCanvas;
            var parent = videoEl.parentNode;
            if (!parent) return null;
            // Reuse rather than re-create: Views/Play (view-play.js) calls createTile afresh on every
            // live→playback toggle against the same cell's <video>, so a per-call canvas would pile
            // up one dead overlay per toggle for the life of the page.
            freezeCanvas = parent.querySelector(':scope > .pb-freeze-frame');
            if (freezeCanvas) return freezeCanvas;
            freezeCanvas = document.createElement('canvas');
            freezeCanvas.className = 'pb-freeze-frame';
            freezeCanvas.style.cssText = 'position:absolute; inset:0; width:100%; height:100%; ' +
                'object-fit:contain; pointer-events:none; display:none;';
            // Inserted immediately after the video (not appended last) so later overlays — status
            // text, badges, zoom/fullscreen controls — keep painting on top of it; position:absolute
            // siblings with no z-index stack in DOM order.
            parent.insertBefore(freezeCanvas, videoEl.nextSibling);
            return freezeCanvas;
        }

        function showFreezeFrame() {
            if (!videoEl.videoWidth || !videoEl.videoHeight) return; // nothing painted yet to freeze
            var canvas = ensureFreezeCanvas();
            if (!canvas) return;
            try {
                canvas.width = videoEl.videoWidth;
                canvas.height = videoEl.videoHeight;
                canvas.getContext('2d').drawImage(videoEl, 0, 0, canvas.width, canvas.height);
                // Matches the video's own digital-zoom transform (wireZoom, in the page glue below)
                // so a frozen frame while zoomed in doesn't visually snap back to 1x for the gap.
                canvas.style.transform = videoEl.style.transform || '';
                canvas.style.transformOrigin = videoEl.style.transformOrigin || '';
                canvas.style.display = '';
            } catch (e) { /* worst case this one transition flashes black, same as before this existed */ }
        }

        function hideFreezeFrame() {
            if (freezeCanvas) freezeCanvas.style.display = 'none';
        }

        // Deferred hide: waits for the new segment to actually have a frame ready ('seeked' fires
        // once a programmatic currentTime assignment's target frame is decoded; 'playing' covers the
        // same moment for a still-playing element) rather than hiding the instant currentTime/play()
        // are called, which can be a tick ahead of anything actually being painted.
        function hideFreezeFrameOnReady() {
            if (!freezeCanvas || freezeCanvas.style.display === 'none') return;
            var done = false;
            function onReady() {
                if (done) return;
                done = true;
                videoEl.removeEventListener('seeked', onReady);
                videoEl.removeEventListener('playing', onReady);
                hideFreezeFrame();
            }
            videoEl.addEventListener('seeked', onReady);
            videoEl.addEventListener('playing', onReady);
        }

        function findSegment(targetMs) {
            for (var i = 0; i < segments.length; i++) {
                if (targetMs >= segments[i].startUtc && targetMs < segments[i].endUtc) return segments[i];
            }
            return null;
        }

        function nextSegmentAfter(targetMs) {
            var best = null;
            segments.forEach(function (s) {
                if (s.startUtc > targetMs && (!best || s.startUtc < best.startUtc)) best = s;
            });
            return best;
        }

        async function ensureSegmentsLoaded(fromMs, toMs, signal) {
            try {
                var url = '/api/cameras/' + cameraId + '/segments?from=' + encodeURIComponent(new Date(fromMs).toISOString()) +
                    '&to=' + encodeURIComponent(new Date(toMs).toISOString());
                var resp = await fetch(url, { signal: signal });
                if (!resp.ok) {
                    // Previously silent — a tile that failed here just sat blank forever with
                    // nothing in the console to explain why, indistinguishable from "still loading".
                    console.error('[playback] segment list fetch failed for camera', cameraId, 'status', resp.status);
                    if (statusEl) statusEl.textContent = 'Could not load recordings (' + resp.status + ').';
                    return;
                }
                var list = await resp.json();
                segments = list.map(function (s) {
                    return { id: s.id, startUtc: new Date(s.startUtc).getTime(), endUtc: new Date(s.endUtc).getTime() };
                });
            } catch (e) {
                if (signal.aborted) return; // superseded by a newer seek — expected, not an error
                console.error('[playback] segment list fetch threw for camera', cameraId, e);
                if (statusEl) statusEl.textContent = 'Could not load recordings.';
            }
        }

        function teardown() {
            loadToken++;
            currentSegmentId = null;
            currentSegmentTimeOrigin = 0;
            try { videoEl.pause(); } catch (e) { /* ignore */ }
            try { videoEl.removeAttribute('src'); videoEl.load(); } catch (e) { /* ignore */ }
        }

        async function loadSegment(segment, seekSeconds, autoplay, myToken, signal, isRetryAfterTimeout) {
            // A prefetch started while the *previous* segment was playing lets this transition skip
            // the network round trip entirely — see prefetch's own comment. Only ever matches the
            // sequential 'ended' advance; an arbitrary seek's target segment was never the one being
            // prefetched, so this is always a no-op miss for that path (falls through unchanged).
            // Before teardown() blanks the element — see showFreezeFrame's own comment. A no-op when
            // there's nothing painted yet (first load on this tile), and harmlessly re-entrant on a
            // rapid scrub: the video is already blank by then, so the previously captured frame
            // stays up rather than being overwritten with nothing.
            showFreezeFrame();

            var prefetchedBytes = null;
            if (prefetch && prefetch.id === segment.id) {
                try { prefetchedBytes = await prefetch.promise; } catch (e) { prefetchedBytes = null; }
                prefetch = null; // consumed either way — a hit or a settled miss, never re-tried as-is
                if (myToken !== loadToken) return; // superseded while the (already in-flight) prefetch was awaited
            }

            var mimeType = pickMimeType(codecHint, hasAudio);

            if (prefetchedBytes) {
                teardown();
                loadToken = myToken; // teardown() bumped it again — restore the token this call owns
                // No "Loading…" here, deliberately — the bytes are already in hand, so the only work
                // left is a MediaSource/SourceBuffer append that resolves in native code with no
                // network wait. The previous segment's own playback already cleared statusEl to ''
                // once it started, so leaving it untouched here means nothing visible flashes at all
                // for a prefetch-hit transition. Confirmed live that setting it here regardless (as
                // this used to) still read as a "Loading" flash on every boundary even once the
                // network fetch itself was eliminated — the text was the visible symptom, not the
                // fetch latency it was written to describe.
                if (!mimeType) {
                    if (statusEl) statusEl.textContent = 'No supported codec for this browser.';
                    hideFreezeFrame();
                    return;
                }

                var prefetchedMediaSource = new MediaSource();
                videoEl.src = URL.createObjectURL(prefetchedMediaSource);
                currentSegmentId = segment.id;

                prefetchedMediaSource.addEventListener('sourceopen', function onOpen() {
                    prefetchedMediaSource.removeEventListener('sourceopen', onOpen);
                    if (myToken !== loadToken) return;
                    var sourceBuffer;
                    try {
                        sourceBuffer = prefetchedMediaSource.addSourceBuffer(mimeType);
                    } catch (e) {
                        if (statusEl) statusEl.textContent = 'This browser cannot decode this stream.';
                        hideFreezeFrame();
                        return;
                    }
                    // 0: a prefetch hit always fetched its segment whole, from true start (see
                    // schedulePrefetch), so there's no fragment-skip offset to account for.
                    appendWholeSegment(prefetchedBytes, sourceBuffer, prefetchedMediaSource, myToken, seekSeconds, autoplay, segment, 0);
                });
                return;
            }

            teardown();
            loadToken = myToken; // teardown() bumped it again — restore the token this call owns
            if (statusEl) statusEl.textContent = 'Loading…';

            if (!mimeType) {
                if (statusEl) statusEl.textContent = 'No supported codec for this browser.';
                hideFreezeFrame();
                return;
            }

            // M18 follow-up: below MIN_SEEK_SECONDS_FOR_PARTIAL_FETCH, skip the query param entirely
            // — same threshold the node applies on its own (Program.cs), kept here too purely so a
            // trivial seek doesn't pay for building/escaping a URL param that would be ignored anyway.
            // The server decides whether it can actually honor this (a usable fragment index must
            // exist); when it can't, seekSeconds is silently ignored server-side and this falls back
            // to the exact whole-file behavior that already existed, with fragmentStartSeconds staying
            // 0 (see below) since no X-Fragment-Start-Seconds header comes back either.
            var seekQuery = seekSeconds > MIN_SEEK_SECONDS_FOR_PARTIAL_FETCH
                ? '?seekSeconds=' + encodeURIComponent(seekSeconds) : '';

            // Combines the caller's own supersede signal with a local timeout — fetch() only accepts
            // one signal, and AbortSignal.any() isn't reliably available across this fleet's browser
            // range, so the timeout's own controller is aborted from both sources instead.
            var fetchController = new AbortController();
            var onOuterAbort = function () { fetchController.abort(); };
            signal.addEventListener('abort', onOuterAbort);
            var timedOut = false;
            var timeoutHandle = setTimeout(function () {
                timedOut = true;
                fetchController.abort();
            }, PLAYBACK_SEGMENT_FETCH_TIMEOUT_MS);

            var resp;
            try {
                resp = await fetch('/playback-segment/' + cameraId + '/' + segment.id + seekQuery, { signal: fetchController.signal });
            } catch (e) {
                signal.removeEventListener('abort', onOuterAbort);
                clearTimeout(timeoutHandle);

                // A superseded seek's fetch lands here once aborted — not a real failure, the next
                // seek already owns the tile, so no status text should overwrite whatever it sets.
                // Same reasoning for the freeze frame: the seek that superseded this one owns it now
                // and will hide it when its own segment is ready.
                if (signal.aborted) return;

                if (timedOut) {
                    // One automatic retry before giving up — a cold SMB read finishing just after
                    // this timeout fires is the common case, not a genuinely dead node, so a single
                    // extra attempt clears most of these without the user ever noticing. myToken and
                    // signal are still valid (nothing superseded this seek — only the fetch itself
                    // stalled), so retrying loadSegment directly reuses them rather than re-running
                    // seekTo's own segment lookup for a segment that's already known.
                    if (!isRetryAfterTimeout) {
                        if (myToken !== loadToken) return;
                        await loadSegment(segment, seekSeconds, autoplay, myToken, signal, true);
                        return;
                    }
                    if (statusEl) statusEl.textContent = 'Recorder is not responding — try again shortly.';
                    hideFreezeFrame();
                    return;
                }

                if (statusEl) statusEl.textContent = 'Could not reach server.';
                hideFreezeFrame();
                return;
            }
            signal.removeEventListener('abort', onOuterAbort);
            clearTimeout(timeoutHandle);
            if (myToken !== loadToken || resp === undefined) return;
            if (!resp.ok) {
                // 404 specifically means "this file doesn't exist on the recorder" — a state that
                // isn't going to resolve itself moments later, unlike a 5xx (a transient node/proxy
                // hiccup, worth retrying on the next seek rather than blacklisting for the rest of
                // this page load).
                if (resp.status === 404) knownBadSegmentIds[segment.id] = true;
                if (statusEl) statusEl.textContent = 'Playback error (' + resp.status + ').';
                hideFreezeFrame();
                return;
            }

            // Absent (null) for the ordinary whole-file response — treated as 0 below, which is
            // exactly the media time a whole-file fetch's first appended sample actually carries.
            // Present only when the node actually served the fragment-skipping partial response (see
            // Program.cs's own /playback-segment route) — the media time of whichever fragment it
            // started from, which is at-or-before seekSeconds but not necessarily exactly it.
            var fragmentStartHeader = resp.headers.get('X-Fragment-Start-Seconds');
            var fragmentStartSeconds = fragmentStartHeader !== null ? parseFloat(fragmentStartHeader) : 0;
            if (!isFinite(fragmentStartSeconds)) fragmentStartSeconds = 0;

            var mediaSource = new MediaSource();
            videoEl.src = URL.createObjectURL(mediaSource);
            currentSegmentId = segment.id;

            mediaSource.addEventListener('sourceopen', function onOpen() {
                mediaSource.removeEventListener('sourceopen', onOpen);
                if (myToken !== loadToken) return;

                var sourceBuffer;
                try {
                    sourceBuffer = mediaSource.addSourceBuffer(mimeType);
                } catch (e) {
                    if (statusEl) statusEl.textContent = 'This browser cannot decode this stream.';
                    hideFreezeFrame();
                    return;
                }

                if (resp.body) {
                    streamIntoSourceBuffer(resp.body.getReader(), sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay, segment, fragmentStartSeconds);
                } else {
                    // No streaming response body support (older browser, or an intermediary that
                    // buffers the whole thing) — fall back to the previous whole-file behavior
                    // rather than failing outright. resp is still the same Response from the
                    // enclosing loadSegment closure; arrayBuffer() works on it regardless of
                    // whether .body (the streaming reader) is exposed.
                    loadWholeBufferFallback(resp, sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay, segment, fragmentStartSeconds);
                }
            });
        }

        // Reads the segment's bytes off the network incrementally and appends each chunk to the
        // SourceBuffer as it arrives, rather than waiting for the entire (potentially several-MB)
        // file to download before any of it is even handed to the decoder — confirmed live as a
        // real, meaningful delay before the first frame appeared, and the same wait was compounding
        // across every tile in a multi-camera view with no shared start, which is most of why tiles
        // could end up 10-30s apart in wall-clock content position: whichever camera's segment
        // happened to be smallest/fastest to fetch would already be playing while a slower one was
        // still downloading its entire file. Seeking as soon as the *first* chunk is buffered (not
        // waiting for the whole segment) means every tile starts close to the same real time
        // regardless of its total segment size.
        //
        // SourceBuffer only accepts one pending appendBuffer() at a time, so chunks are pumped one
        // at a time, each read gated on the previous append's updateend.
        function streamIntoSourceBuffer(reader, sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay, segment, fragmentStartSeconds) {
            var startedPlayback = false;
            // Set by finish() before its own tryStartPlaybackOnce call — "no more bytes are coming,
            // so stop waiting for the target to appear and use whatever actually arrived".
            var streamEnded = false;

            function tryStartPlaybackOnce() {
                if (startedPlayback || myToken !== loadToken) return;
                var buffered = sourceBuffer.buffered;
                if (!buffered.length) return; // first chunk hasn't been processed into a range yet

                // seekSeconds is an offset from the segment's *wall-clock* start, but a recorded
                // fMP4's internal timeline doesn't have to begin at zero — these carry the
                // baseMediaDecodeTime they were written with, so buffered can start at an arbitrary
                // large value (confirmed live: seeking to the raw offset landed outside the
                // buffered range entirely and rendered nothing, with no error). fragmentStartSeconds
                // (M18 follow-up) generalizes this the same way for a partial fetch that skipped
                // straight to a fragment mid-segment (see loadSegment's own X-Fragment-Start-Seconds
                // handling): bufStart there reports that fragment's own media time, not the segment's
                // true start, so the residual offset still to seek across is (seekSeconds -
                // fragmentStartSeconds), not seekSeconds itself. Zero for every ordinary whole-file
                // fetch (ended-handler auto-advance and any seek the node couldn't/didn't partially
                // serve), which reduces this to exactly the original formula.
                var bufStart = buffered.start(0);
                var bufEnd = buffered.end(buffered.length - 1);
                var residualSeekSeconds = seekSeconds - fragmentStartSeconds;
                var target = bufStart + residualSeekSeconds;

                // Wait for the target instant to actually be buffered before seeking to it. An
                // earlier version set currentTime here as soon as the *first* chunk landed, on the
                // theory that HTMLMediaElement would wait for the rest by itself the way progressive
                // download does. It doesn't: under MSE the browser clamps a seek to the `seekable`
                // range, which is derived from what's currently buffered — so a deep link into the
                // middle of a 60s segment got clamped back to the segment's start and simply played
                // from there. Confirmed live as "clicking Play on a bookmark starts at the beginning
                // of the minute, and you have to wait for it to buffer and then click the timeline".
                //
                // Only ever delays when the target genuinely isn't buffered yet. The common
                // end-of-segment auto-advance passes a zero residual, where target == bufStart and
                // this is satisfied by the very first chunk, exactly as before.
                if (residualSeekSeconds > 0 && target > bufEnd && !streamEnded) {
                    // Nothing to show at the requested instant yet, but the tile shouldn't look dead
                    // while the rest of the segment streams in — a freeze frame from the previous
                    // segment (if any) is still up, so this only speaks when there's nothing to see.
                    if (statusEl && !statusEl.textContent) statusEl.textContent = 'Loading…';
                    return;
                }

                startedPlayback = true;
                // Minus fragmentStartSeconds, not bare bufStart: this variable means "the media time
                // that corresponds to the segment's wall-clock *start*" (see its declaration), and on
                // a partial fetch bufStart is the media time of the fragment the node skipped ahead
                // to — which corresponds to segStart + fragmentStartSeconds, not segStart. Storing
                // bare bufStart made computeCurrentWallClockMs read exactly fragmentStartSeconds too
                // early, so the timeline strip and the time readout sat behind the picture by however
                // far into the segment the seek had landed, while the video itself played the right
                // frames (the seek math below was already correct). At a deep link into the middle of
                // a segment that reads as the timeline starting at the segment's own start instead of
                // the event, then snapping forward once the next segment loads whole (origin 0 again).
                // Zero for every ordinary whole-file fetch, which leaves this exactly as it was.
                currentSegmentTimeOrigin = bufStart - fragmentStartSeconds;
                // Clamped to what exists: if the stream ended before reaching the target (a segment
                // whose real content is shorter than its recorded duration — the same wall-clock vs.
                // encoded-length drift /playback-thumbnail already compensates for), land on the last
                // real frame rather than seeking past the end and stalling forever.
                videoEl.currentTime = Math.min(Math.max(bufStart, target), bufEnd);
                videoEl.playbackRate = desiredPlaybackRate; // load() (in teardown, just before this segment) reset it — see desiredPlaybackRate's own comment
                hideFreezeFrameOnReady(); // held over from the previous segment until this one actually paints
                if (statusEl) statusEl.textContent = '';
                if (autoplay) videoEl.play().catch(function () { /* blocked by autoplay policy — stays paused with a visible control */ });
                // Scoped to actual continuous playback (autoplay=true) — a paused scrub or a
                // stepped-mode seek (both pass autoplay=false) has no real "next" to look ahead to,
                // so prefetching there would just be a wasted background fetch for bytes the user is
                // very unlikely to play through to.
                if (autoplay) schedulePrefetch(segment);
            }

            function finish() {
                if (myToken !== loadToken) return;
                // Before the tryStartPlaybackOnce call below: releases the "wait for the target to
                // buffer" guard, since no further bytes are coming and whatever arrived is all there
                // will ever be.
                streamEnded = true;
                // Without endOfStream the MediaSource stays 'open', meaning duration stays
                // unbounded and the element never fires 'ended' — which the auto-advance to the
                // next segment depends on, so it silently never advanced.
                if (mediaSource.readyState === 'open') {
                    try { mediaSource.endOfStream(); } catch (e) { /* already ended/detached */ }
                }
                // Covers the case where the entire (small) segment arrived in a single read, so
                // updateend never got a chance to fire tryStartPlaybackOnce before this ran —
                // idempotent either way via the startedPlayback guard.
                tryStartPlaybackOnce();
                if (!startedPlayback) {
                    console.error('[playback] segment fully streamed but nothing buffered for camera', cameraId);
                    if (statusEl) statusEl.textContent = 'Segment could not be decoded.';
                    hideFreezeFrame();
                }
            }

            function pumpNext() {
                if (signal.aborted || myToken !== loadToken) return;
                reader.read().then(function (result) {
                    if (signal.aborted || myToken !== loadToken) return;
                    if (result.done) {
                        finish();
                        return;
                    }
                    try {
                        sourceBuffer.appendBuffer(result.value);
                    } catch (e) {
                        console.error('[playback] appendBuffer failed for camera', cameraId, e);
                        if (statusEl) statusEl.textContent = 'Playback error: ' + e.message;
                        recoverUnrecoverableMediaSource('an appendBuffer failure (' + e.name + ')',
                            segment.startUtc + seekSeconds * 1000, autoplay);
                    }
                }).catch(function (e) {
                    if (signal.aborted) return; // superseded — the reader was cancelled below, expected
                    console.error('[playback] segment stream read failed for camera', cameraId, e);
                    if (statusEl) statusEl.textContent = 'Playback error reading segment.';
                });
            }

            sourceBuffer.addEventListener('updateend', function () {
                if (myToken !== loadToken) return;
                tryStartPlaybackOnce();
                pumpNext();
            });

            // If a newer seek supersedes this one mid-stream, stop pulling bytes off the network
            // for a tile that's already been abandoned rather than reading it to completion.
            signal.addEventListener('abort', function () { try { reader.cancel(); } catch (e) { /* ignore */ } });

            pumpNext();
        }

        // Same behavior the whole file used to have unconditionally — kept only as a fallback for
        // a fetch() Response with no streaming body support.
        async function loadWholeBufferFallback(resp, sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay, segment, fragmentStartSeconds) {
            var bytes;
            try {
                bytes = new Uint8Array(await resp.arrayBuffer());
            } catch (e) {
                if (!signal.aborted && statusEl) statusEl.textContent = 'Playback error reading segment.';
                return;
            }
            if (myToken !== loadToken) return;
            appendWholeSegment(bytes, sourceBuffer, mediaSource, myToken, seekSeconds, autoplay, segment, fragmentStartSeconds);
        }

        // Shared by loadWholeBufferFallback (a Response with no streaming body support) and a
        // prefetch hit in loadSegment (bytes already fully downloaded ahead of time, nothing left to
        // read) — both cases already have the segment's complete bytes in hand with no further
        // network work, so appending is identical either way. fragmentStartSeconds is always 0 for a
        // prefetch hit (prefetch always fetches its segment whole, from true start — see
        // schedulePrefetch) and only ever nonzero when loadWholeBufferFallback's own caller read a
        // real X-Fragment-Start-Seconds header off a partial response.
        function appendWholeSegment(bytes, sourceBuffer, mediaSource, myToken, seekSeconds, autoplay, segment, fragmentStartSeconds) {
            sourceBuffer.addEventListener('updateend', function () {
                if (myToken !== loadToken) return;
                if (mediaSource.readyState === 'open') {
                    try { mediaSource.endOfStream(); } catch (e) { /* already ended/detached */ }
                }
                var buffered = sourceBuffer.buffered;
                if (buffered.length) {
                    var bufStart = buffered.start(0);
                    currentSegmentTimeOrigin = bufStart - fragmentStartSeconds; // see tryStartPlaybackOnce for why this isn't bare bufStart
                    // The whole segment is already appended on this path, so there's nothing to wait
                    // for the way the streaming path has to — but the same upper clamp applies: a
                    // segment whose real encoded length falls short of its recorded duration would
                    // otherwise be seeked past its end and stall. See tryStartPlaybackOnce for why
                    // the residual is (seekSeconds - fragmentStartSeconds), not seekSeconds itself.
                    videoEl.currentTime = Math.min(
                        Math.max(bufStart, bufStart + (seekSeconds - fragmentStartSeconds)), buffered.end(buffered.length - 1));
                    videoEl.playbackRate = desiredPlaybackRate; // see desiredPlaybackRate's own comment
                    hideFreezeFrameOnReady(); // see the matching call in tryStartPlaybackOnce
                } else {
                    if (statusEl) statusEl.textContent = 'Segment could not be decoded.';
                    hideFreezeFrame();
                    return;
                }
                if (statusEl) statusEl.textContent = '';
                if (autoplay) videoEl.play().catch(function () { /* blocked by autoplay policy */ });
                // Scoped to actual continuous playback (autoplay=true) — a paused scrub or a
                // stepped-mode seek (both pass autoplay=false) has no real "next" to look ahead to,
                // so prefetching there would just be a wasted background fetch for bytes the user is
                // very unlikely to play through to.
                if (autoplay) schedulePrefetch(segment);
            });
            try {
                sourceBuffer.appendBuffer(bytes);
            } catch (e) {
                console.error('[playback] appendBuffer failed for camera', cameraId, e);
                if (statusEl) statusEl.textContent = 'Playback error: ' + e.message;
                recoverUnrecoverableMediaSource('an appendBuffer failure (' + e.name + ')',
                    segment.startUtc + seekSeconds * 1000, autoplay);
            }
        }

        async function seekTo(targetMs, autoplay) {
            var myToken = ++loadToken;
            // Actually cancel whatever this tile had in flight (not just mark its result stale) —
            // confirmed live as necessary, not just defensive: rapid scrubbing left dozens of
            // already-abandoned fetches running to completion, exhausting the browser's connection
            // pool (net::ERR_INSUFFICIENT_RESOURCES) even after the drag handler itself was
            // throttled, since a fast scrub still supersedes a seek roughly every throttle tick.
            if (abortController) abortController.abort();
            abortController = new AbortController();
            var signal = abortController.signal;

            var segment = findSegment(targetMs);

            if (!segment) {
                // Status was previously left untouched for this whole lookup — a tile just looked
                // blank the entire time it was in flight, identical whether that took 50ms or never
                // resolved at all. "Loading…" itself is set separately once a segment is actually
                // found and its bytes are being fetched (loadSegment); this is the step before that.
                if (statusEl) statusEl.textContent = 'Looking for a recording…';
                await ensureSegmentsLoaded(targetMs - 2 * 3600 * 1000, targetMs + 2 * 3600 * 1000, signal);
                if (myToken !== loadToken) return;
                segment = findSegment(targetMs);
            }

            if (!segment) {
                var next = nextSegmentAfter(targetMs);
                if (statusEl) {
                    statusEl.textContent = next
                        ? 'No recording here — next at ' + new Date(next.startUtc).toLocaleString()
                        : 'No recording available.';
                }
                teardown();
                hideFreezeFrame(); // nothing is going to load here — a held frame would sit stale forever
                return;
            }

            if (knownBadSegmentIds[segment.id]) {
                // Already confirmed unreachable this page load — see knownBadSegmentIds' own
                // comment for why re-fetching it here matters, not just cosmetically.
                teardown();
                hideFreezeFrame();
                if (statusEl) statusEl.textContent = 'This recording is unavailable (missing on the recorder).';
                return;
            }

            if (segment.id === currentSegmentId) {
                videoEl.currentTime = currentSegmentTimeOrigin + (targetMs - segment.startUtc) / 1000;
                if (autoplay) videoEl.play().catch(function () { /* see above */ });
                return;
            }

            await loadSegment(segment, (targetMs - segment.startUtc) / 1000, autoplay, myToken, signal);
        }

        function computeCurrentWallClockMs() {
            var seg = segments.find(function (s) { return s.id === currentSegmentId; });
            return seg ? seg.startUtc + (videoEl.currentTime - currentSegmentTimeOrigin) * 1000 : null;
        }

        // Once videoEl.error is set (a decode hiccup, not necessarily anything wrong with the file
        // itself — confirmed live from rapid scrubbing), the *only* way to clear it is videoEl.load(),
        // which is also what detaches the current MediaSource for good. Until that happens, every
        // future sourceBuffer.appendBuffer() call on this element — even one for a brand new segment's
        // brand new SourceBuffer — throws "the HTMLMediaElement.error attribute is not null" the
        // instant it's called, and streamIntoSourceBuffer's pumpNext() only ever re-invokes itself
        // from a successful append's 'updateend', so that throw silently stalls the whole reader with
        // nothing left running to recover it. This was the whole tile going dead until the user forced
        // a real reload by hand (a page refresh, or scrubbing far enough to cross two segment
        // boundaries) — confirmed live as exactly that report.
        //
        // Recovery has to go through teardown() first, not straight back into seekTo(): seekTo has a
        // same-segment fast path that only moves currentTime on the *existing* (still broken)
        // MediaSource when the target resolves to the segment already loaded, which is exactly the
        // common case here and would silently skip the one thing that actually fixes anything.
        // teardown() clears currentSegmentId (defeating that fast path) and calls videoEl.load()
        // (which is what actually resets .error), so the seekTo() right after is guaranteed to run a
        // genuine loadSegment() against a fresh element.
        //
        // Debounced rather than reacting unconditionally — a codec/segment the browser genuinely
        // can't decode must not retry in a tight loop forever. Shared by two distinct failure modes
        // that both mean "this MediaSource/SourceBuffer is unrecoverable, start over": the videoEl
        // 'error' event below (a real decode error), and a synchronous sourceBuffer.appendBuffer()
        // throw (streamIntoSourceBuffer's pumpNext, appendWholeSegment) — confirmed live as a second,
        // independent way into the identical stuck-forever symptom: QuotaExceededError is a real risk
        // at this fleet's segment sizes (24–44MB/segment at 4K/HEVC), and unlike the 'error' event, a
        // throw from appendBuffer() never fires 'updateend', so pumpNext's own only re-invocation path
        // never runs again — nothing was left to notice or recover from it before this existed.
        var lastErrorRecoveryAt = 0;
        var ERROR_RECOVERY_COOLDOWN_MS = 3000;
        function recoverUnrecoverableMediaSource(reason, targetMs, resumeAutoplay) {
            var now = Date.now();
            if (now - lastErrorRecoveryAt < ERROR_RECOVERY_COOLDOWN_MS) return;
            lastErrorRecoveryAt = now;
            console.error('[playback] recovering camera', cameraId, 'tile after', reason, '— reloading at', targetMs);
            teardown();
            if (targetMs !== null && targetMs !== undefined) {
                seekTo(targetMs, resumeAutoplay).catch(function () { /* logged inside seekTo's own wrapper */ });
            }
        }

        videoEl.addEventListener('error', function () {
            if (!currentSegmentId) return; // nothing loaded yet, or already superseded — not our error to fix
            recoverUnrecoverableMediaSource('a video decode error', computeCurrentWallClockMs(), !videoEl.paused);
        });

        // Each tile auto-advances to whatever's next *for this camera*, independent of any sibling
        // tile — one camera's gap or segment boundary never waits on another's.
        videoEl.addEventListener('ended', function () {
            var seg = segments.find(function (s) { return s.id === currentSegmentId; });
            if (seg) seekTo(seg.endUtc + 1, true);
        });

        return {
            // Wrapped so a rejection can never escape as an unhandled promise: every caller
            // (seekAll, the 'ended' handler) fires this and moves on without awaiting, so an
            // internal throw would otherwise surface only as console noise with no context.
            seekTo: function (targetMs, autoplay) {
                return seekTo(targetMs, autoplay).catch(function (e) {
                    if (e && e.name === 'AbortError') return; // superseded by a newer seek — expected
                    console.error('[playback] seek failed', e);
                });
            },
            currentWallClockMs: computeCurrentWallClockMs,
            // A same-segment nudge, not a full seek: no fetch, no segment lookup, just moving
            // currentTime within whatever's already loaded — for correcting small drift between
            // tiles during ongoing playback (see the page glue's resyncDriftingTiles), not for
            // jumping to a different point in time. A no-op if targetMs has moved outside the
            // currently loaded segment entirely; a real segment change belongs to seekTo/the
            // 'ended' handler, not a drift correction.
            resyncTo: function (targetMs) {
                var seg = segments.find(function (s) { return s.id === currentSegmentId; });
                if (!seg || targetMs < seg.startUtc || targetMs >= seg.endUtc) return;
                videoEl.currentTime = currentSegmentTimeOrigin + (targetMs - seg.startUtc) / 1000;
            },
            // M16: sets the rate immediately (for the currently loaded segment) and remembers it for
            // every segment loaded from here on — see desiredPlaybackRate's own comment for why a
            // one-time videoEl.playbackRate assignment doesn't survive this tile's lifetime.
            setPlaybackRate: function (rate) {
                desiredPlaybackRate = rate;
                videoEl.playbackRate = rate;
            },
            teardown: function () {
                teardown();
                hideFreezeFrame(); // tile is being disposed/rebuilt — never leave a frozen frame over a dead tile
            }
        };
    }

    window.larisvmsPlaybackPlayer = { createTile: createTile };
})();

// ── Pages/Playback page glue ─────────────────────────────────────────────────
// Driven by saved Views (M6), not an ad-hoc camera picker — a view's cell layout (positions,
// aspect ratios, cameras) is what's rendered, with playback video in place of live video, so the
// same arrangement you'd watch live is what you scrub through here.
(function () {
    'use strict';

    var opts = null;
    var cameraById = {};
    var viewsById = {};
    var tiles = {}; // cameraId -> { player, videoEl, statusEl }
    var timeline = null;       // selected camera's own coverage
    var globalTimeline = null; // merged coverage across every camera
    var primaryCameraId = null;
    var playheadMs = Date.now();
    var playing = false;
    var speedRate = 1;
    var stepLoopToken = null; // non-null only while speedRate is outside native playbackRate's reliable range — see applySpeed

    // ── Remembered timeline position / display preference ──────────────────
    // Server-backed (see user-preferences.js) — follows the user across devices, not just across
    // reloads on the same browser the way localStorage did. init() (below) already waits for the
    // preferences fetch to resolve before either load* function here is ever called.
    var POSITION_KEY = 'playback.position';
    var HOUR24_KEY = 'playback.hour24';
    var EVENT_TAGS_KEY = 'playback.eventTags';
    var VIEW_KEY = 'playback.viewId';
    var savePositionTimer = null;

    // Which View was last open — a refresh used to always land back on the bare "(choose a view)"
    // picker with no tiles/timeline at all until re-picked by hand, since only the scrub
    // position/zoom were ever remembered, not the view selection itself. Same server-backed
    // preferences store as POSITION_KEY, saved on every real selection (picker change or a deep
    // link's own view resolution) and read once on init, skipped only when a deep link is present
    // (a deliberate specific navigation outranks "whatever was open last time").
    function loadPersistedViewId() {
        return window.larisvmsPreferences.get(VIEW_KEY, null);
    }

    function saveViewId(viewId) {
        window.larisvmsPreferences.set(VIEW_KEY, viewId);
    }

    function loadPersistedPosition() {
        var raw = window.larisvmsPreferences.get(POSITION_KEY, null);
        if (!raw) return null;
        try {
            var parsed = JSON.parse(raw);
            if (typeof parsed.centerMs === 'number' && typeof parsed.rangeMs === 'number') return parsed;
        } catch (e) { /* corrupted value — fall back to the usual default */ }
        return null;
    }

    // Debounced — this fires on every playback tick (500ms) and every throttled drag-scrub tick,
    // and a write on each one would be needless request churn for a value that only needs to be
    // current by the time the tab actually closes or reloads.
    function schedulePositionSave() {
        clearTimeout(savePositionTimer);
        savePositionTimer = setTimeout(function () {
            if (!timeline) return;
            window.larisvmsPreferences.set(POSITION_KEY, JSON.stringify({ centerMs: playheadMs, rangeMs: timeline.getRange() }));
        }, 500);
    }

    function loadHour24Preference() {
        return window.larisvmsPreferences.get(HOUR24_KEY, 'false') === 'true';
    }

    function saveHour24Preference(on) {
        window.larisvmsPreferences.set(HOUR24_KEY, on);
    }

    // Off by default (M16) — a real-phone walkthrough found the tag-colored timeline busy/confusing
    // for everyday review; turning it on is an opt-in "show me event detail" mode now, not the
    // always-on default it used to be.
    function loadEventTagsPreference() {
        return window.larisvmsPreferences.get(EVENT_TAGS_KEY, 'false') === 'true';
    }

    function saveEventTagsPreference(on) {
        window.larisvmsPreferences.set(EVENT_TAGS_KEY, on);
    }

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // datetime-local inputs read/write local wall-clock time with no timezone suffix — `new
    // Date(value)` already parses that string as local time on the way back in, so this only needs
    // to handle the ms-to-string direction.
    function msToLocalDatetimeInputValue(ms) {
        var d = new Date(ms);
        function pad(n) { return String(n).padStart(2, '0'); }
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' +
            pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
    }

    // The tape-scrubber timeline is already ms-precise once zoomed in (timeline.js's MIN_RANGE_MS
    // is 5 seconds), but that's not discoverable — at the default 24h zoom a single pixel of drag
    // covers 100+ seconds, which reads as "can only seek to the minute" even though nothing is
    // actually snapping. Click-to-edit on the readout itself gives an exact-seek path that doesn't
    // depend on zoom level at all.
    function wireCurrentTimeEdit(elId) {
        var displayEl = document.getElementById(elId);
        if (!displayEl) return;
        var editing = false;

        function render() { displayEl.textContent = new Date(playheadMs).toLocaleString(); }

        function commit(inputEl) {
            if (!editing) return;
            editing = false;
            var ms = inputEl.value ? new Date(inputEl.value).getTime() : NaN;
            if (!isNaN(ms)) seekAll(ms, playing); // also repopulates displayEl's text, replacing the input
            else render();
        }

        function cancel() {
            editing = false;
            render();
        }

        displayEl.style.cursor = 'pointer';
        displayEl.title = 'Click to jump to an exact time';
        displayEl.addEventListener('click', function () {
            if (editing) return;
            editing = true;
            var inputEl = document.createElement('input');
            inputEl.type = 'datetime-local';
            inputEl.step = '1';
            inputEl.className = 'form-control form-control-sm d-inline-block w-auto';
            inputEl.value = msToLocalDatetimeInputValue(playheadMs);
            displayEl.textContent = '';
            displayEl.appendChild(inputEl);
            inputEl.focus();
            inputEl.select();
            // Stopped from bubbling so the paused-playback arrow-key nudge (see init()) never fires
            // off the keystrokes used to type into this field.
            inputEl.addEventListener('keydown', function (e) {
                e.stopPropagation();
                if (e.key === 'Enter') { e.preventDefault(); commit(inputEl); }
                else if (e.key === 'Escape') { e.preventDefault(); cancel(); }
            });
            inputEl.addEventListener('blur', function () { commit(inputEl); });
        });
    }

    // Arrow-key nudge is only active while paused — while playing, playheadMs is already advancing
    // every tick (see updatePlayhead), and a ±1s jump on top of that would just be confusing.
    function wireArrowKeyNudge() {
        window.addEventListener('keydown', function (e) {
            if (playing) return;
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
            var tag = document.activeElement && document.activeElement.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
            e.preventDefault();
            var deltaMs = (e.shiftKey ? 10000 : 1000) * (e.key === 'ArrowLeft' ? -1 : 1);
            seekAll(playheadMs + deltaMs, false);
        });
    }

    // No .ratio-16x9 wrapper here on purpose (unlike Live's tiles) — the grid cell itself is
    // whatever shape rebuildTilesFromView's auto NxM grid gives it (fills the whole window, one
    // camera to a whole page down to many cameras in a dense grid), not locked to 16:9, so forcing
    // a 16:9 box inside a differently-shaped cell would letterbox twice (once for the forced ratio,
    // again for the video's own object-fit) and waste space. The video's own object-fit:contain is
    // what preserves its real aspect ratio without distortion; the cell just gets filled 100%.
    // Camera name moves to a corner overlay badge instead of a below-video bar, so it doesn't eat
    // into the vertical space this whole redesign exists to maximize.
    function buildTileCardHtml(cam, isPrimary) {
        return (
            '<div class="h-100 w-100 position-relative bg-black overflow-hidden pb-tile-frame" ' +
                'style="outline:' + (isPrimary ? '3px solid var(--bs-primary)' : 'none') + '; outline-offset:-2px;">' +
                '<video class="pb-video" muted playsinline ' +
                    'style="width:100%; height:100%; object-fit:contain; transform-origin:center center; cursor:default; touch-action:none;"></video>' +
                '<div class="position-absolute top-50 start-50 translate-middle text-white small text-center px-2 pb-status"></div>' +
                '<div class="position-absolute top-0 start-0 m-1 px-2 py-1 small text-white text-truncate pb-select-primary" ' +
                    'style="background:rgba(0,0,0,.55); border-radius:.25rem; max-width:calc(100% - 96px); pointer-events:none;" ' +
                    'title="Click this tile to make it drive the per-camera timeline">' +
                    (isPrimary ? '★ ' : '') + escHtml(cam.name) +
                '</div>' +
                '<div class="position-absolute top-0 end-0 m-1 btn-group btn-group-sm">' +
                    '<button type="button" class="btn btn-outline-light pb-zoom-out" title="Zoom out" style="padding:.1rem .35rem;">−</button>' +
                    '<button type="button" class="btn btn-outline-light pb-zoom-reset" title="Reset zoom" style="padding:.1rem .35rem;">⤢</button>' +
                    '<button type="button" class="btn btn-outline-light pb-zoom-in" title="Zoom in" style="padding:.1rem .35rem;">+</button>' +
                '</div>' +
                // Hidden until hover (matches Live's own .live-controls) or forced visible while this
                // tile is the fullscreen element (see .tile-fullscreen in site.css) — the digital zoom
                // buttons above are the normal-grid-view zoom; fullscreen has its own wheel-zoom/drag-pan
                // (fullscreen-tile.js) and just needs audio + a way back out while active.
                //
                // A flex row rather than one btn-group: the audio group leads with a volume slider
                // (audio-controls.js), which isn't a button and shouldn't pick up btn-group's own
                // joined-corners styling. Same structure as a Views/Play cell's controls.
                '<div class="position-absolute bottom-0 end-0 m-1 d-flex align-items-center gap-1 pb-fullscreen-controls d-none">' +
                    window.larisvmsAudioControls.html(cam && cam.hasAudio) +
                    '<div class="btn-group btn-group-sm">' +
                        // The page toolbar's own Play/Pause is a page-level element, so it isn't
                        // rendered at all while a tile holds the fullscreen layer — the timeline gets
                        // moved in (see wireFullscreen) but the transport control had no counterpart,
                        // leaving no way to start or stop playback without exiting fullscreen first.
                        // Drives the same togglePlay as the toolbar button and stays label-synced with it.
                        '<button type="button" class="btn btn-outline-light pb-fs-playpause" title="Play/Pause" style="padding:.1rem .35rem;">▶</button>' +
                        '<button type="button" class="btn btn-outline-light pb-fullscreen-toggle" title="Fullscreen" style="padding:.1rem .35rem;">⛶</button>' +
                    '</div>' +
                '</div>' +
            '</div>'
        );
    }

    // Digital zoom: CSS transform scale/translate on the <video> element itself, no server
    // involvement. Drag-to-pan engages once zoomed in past 1x; at 1x the same drag instead draws a
    // selection rectangle and zooms to fill it on release (M16's "drag-select-to-zoom on a video
    // cell") — one gesture, two meanings depending on current zoom, rather than a second control.
    function wireZoom(tileEl, videoEl) {
        var MAX_ZOOM = 4;
        var zoom = 1, panX = 0, panY = 0;
        function apply() { videoEl.style.transform = 'scale(' + zoom + ') translate(' + panX + 'px, ' + panY + 'px)'; }

        var inBtn = tileEl.querySelector('.pb-zoom-in');
        var outBtn = tileEl.querySelector('.pb-zoom-out');
        var resetBtn = tileEl.querySelector('.pb-zoom-reset');
        if (inBtn) inBtn.addEventListener('click', function () { zoom = Math.min(MAX_ZOOM, zoom + 0.5); apply(); });
        if (outBtn) outBtn.addEventListener('click', function () {
            zoom = Math.max(1, zoom - 0.5);
            if (zoom === 1) { panX = 0; panY = 0; }
            apply();
        });
        if (resetBtn) resetBtn.addEventListener('click', function () { zoom = 1; panX = 0; panY = 0; endGesture(false); apply(); });

        // <video> is a native drag source in Chrome/Edge (you can drag a frame out like an image) —
        // with no preventDefault on pointerdown, a mousedown-then-move over the video could kick off
        // *that* native drag concurrently with this handler. Once it does, the browser owns the mouse
        // gesture for the rest of that drag (showing the no-drop/circle-slash cursor over anything
        // that isn't a valid drop target, which is everywhere on this page, including this same video
        // and the timeline canvases below it) and our own pointermove/pointerup below stop getting
        // sane deltas — confirmed live as exactly this: dragging felt "random", working on one
        // timeline/tile but not another, because it depended on whether that particular pointerdown
        // happened to also trigger a native dragstart. preventDefault on pointerdown is the documented
        // way to suppress it, and now fires unconditionally (a version of this before drag-select-to-
        // zoom existed left it out at zoom<=1, since there was nothing to drag yet there either) —
        // needed now since a drag at zoom<=1 is a deliberate selection gesture too, not "nothing to
        // do here."
        // Pointer events + setPointerCapture, not mouse events on window — see fullscreen-tile.js's
        // matching handler (and timeline.js's original write-up) for why: a lost mouseup left
        // `dragging` stuck true forever, and from then on every mousemove anywhere on the page kept
        // panning this video, intermittently stealing gestures aimed at other controls.
        var dragging = false, startX = 0, startY = 0, startPanX = 0, startPanY = 0, dragPointerId = null;
        var selecting = false, selStartX = 0, selStartY = 0, selRectEl = null;
        // Below this, a drag reads as a plain click (selectPrimary on the tile still fires from the
        // ordinary 'click' event afterward — preventDefault on pointerdown doesn't suppress it) rather
        // than a deliberate "zoom to this region" gesture.
        var MIN_SELECT_PX = 24;

        function ensureSelRectEl() {
            if (selRectEl) return selRectEl;
            selRectEl = document.createElement('div');
            selRectEl.style.cssText = 'position:absolute; border:1px dashed #fff; ' +
                'background:rgba(255,255,255,.15); pointer-events:none; display:none; z-index:5;';
            videoEl.parentElement.appendChild(selRectEl);
            return selRectEl;
        }

        function releaseCapture() {
            if (dragPointerId !== null && videoEl.hasPointerCapture(dragPointerId)) {
                videoEl.releasePointerCapture(dragPointerId);
            }
            dragPointerId = null;
        }

        function endDrag() {
            if (!dragging) return;
            dragging = false;
            releaseCapture();
            videoEl.style.cursor = zoom > 1 ? 'grab' : 'default';
        }

        // commit=false on pointercancel (Esc, losing capture mid-gesture, etc.) — the selection is
        // simply discarded rather than zooming to wherever it happened to be when interrupted.
        function endSelect(commit) {
            if (!selecting) return;
            selecting = false;
            releaseCapture();
            var el = ensureSelRectEl();
            var w = parseFloat(el.style.width) || 0, h = parseFloat(el.style.height) || 0;
            var left = parseFloat(el.style.left) || 0, top = parseFloat(el.style.top) || 0;
            el.style.display = 'none';
            if (commit && w >= MIN_SELECT_PX && h >= MIN_SELECT_PX) {
                var vRect = videoEl.getBoundingClientRect();
                // The pan math that centers the selection: transform is `scale(zoom)
                // translate(panX,panY)` around the element's own center, and CSS applies the
                // rightmost function (translate) to the point first, then scale — so a point at
                // offset d from center ends up at zoom*(d+pan) from center post-transform. Setting
                // that to 0 (selection center lands exactly on the frame's center) gives
                // pan = -d = (frameCenter - selectionCenter), independent of zoom itself.
                var cx = left + w / 2, cy = top + h / 2;
                zoom = Math.min(MAX_ZOOM, Math.max(1, Math.min(vRect.width / w, vRect.height / h)));
                panX = vRect.width / 2 - cx;
                panY = vRect.height / 2 - cy;
                apply();
            }
            videoEl.style.cursor = zoom > 1 ? 'grab' : 'default';
        }

        function endGesture(commit) { endDrag(); endSelect(commit); }

        videoEl.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) return;
            e.preventDefault();
            dragPointerId = e.pointerId;
            videoEl.setPointerCapture(e.pointerId);
            if (zoom > 1) {
                dragging = true;
                startX = e.clientX; startY = e.clientY;
                startPanX = panX; startPanY = panY;
                videoEl.style.cursor = 'grabbing';
            } else {
                selecting = true;
                var vRect = videoEl.getBoundingClientRect();
                selStartX = e.clientX - vRect.left;
                selStartY = e.clientY - vRect.top;
                var el = ensureSelRectEl();
                el.style.left = selStartX + 'px'; el.style.top = selStartY + 'px';
                el.style.width = '0px'; el.style.height = '0px';
                el.style.display = '';
                videoEl.style.cursor = 'crosshair';
            }
        });
        videoEl.addEventListener('pointermove', function (e) {
            if (dragging) {
                panX = startPanX + (e.clientX - startX) / zoom;
                panY = startPanY + (e.clientY - startY) / zoom;
                apply();
            } else if (selecting) {
                var vRect = videoEl.getBoundingClientRect();
                var x = e.clientX - vRect.left, y = e.clientY - vRect.top;
                var el = ensureSelRectEl();
                el.style.left = Math.min(selStartX, x) + 'px';
                el.style.top = Math.min(selStartY, y) + 'px';
                el.style.width = Math.abs(x - selStartX) + 'px';
                el.style.height = Math.abs(y - selStartY) + 'px';
            }
        });
        videoEl.addEventListener('pointerup', function () { endGesture(true); });
        videoEl.addEventListener('pointercancel', function () { endGesture(false); });
    }

    // Double-click-to-fullscreen + wheel-zoom/drag-pan (fullscreen-tile.js), plus the audio/exit
    // controls that are the only ones left visible once this tile is the fullscreen element —
    // see .tile-fullscreen in site.css, which hides the normal-grid-view zoom buttons and the
    // primary-select badge while it's active. frameEl is .pb-tile-frame, not the outer grid-cell
    // wrapper, so fullscreening it doesn't also fullscreen this tile's grid-sizing wrapper element.
    function wireFullscreen(frameEl, videoEl) {
        var controls = frameEl.querySelector('.pb-fullscreen-controls');
        var fsBtn = frameEl.querySelector('.pb-fullscreen-toggle');
        var playBtn = frameEl.querySelector('.pb-fs-playpause');
        if (playBtn) {
            // stopPropagation so this doesn't also register as a tile click (selectPrimary).
            playBtn.addEventListener('click', function (e) { e.stopPropagation(); togglePlay(); });
            playBtn.textContent = playing ? '⏸' : '▶';
        }

        frameEl.addEventListener('mouseenter', function () { if (controls) controls.classList.remove('d-none'); });
        frameEl.addEventListener('mouseleave', function () { if (controls) controls.classList.add('d-none'); });

        // Mute toggle + volume slider. Every segment boundary re-runs the tile's own
        // teardown()/videoEl.load() (see createTile/loadSegment above), which resets the element back
        // to its `muted` HTML-attribute default (true) — audio-controls.js reapplies both mute state
        // and volume on each new resource load, so a tile's audio survives a seek across segments
        // instead of silently reverting mid-scrub.
        window.larisvmsAudioControls.wire(controls, videoEl);

        // Fullscreening a tile promotes only that element's own subtree into the browser's
        // fullscreen layer, so the timelines — page-level elements below the grid — simply stop
        // being rendered, leaving no way to scrub the very footage being watched full-screen. Rather
        // than duplicating them inside the tile (two canvases to keep in sync, two sets of buckets to
        // fetch), the existing element is *moved* into the fullscreened tile and moved back on exit:
        // same canvas, same timeline instance, no state to reconcile. Its canvases re-measure
        // themselves on arrival via timeline.js's ResizeObserver, which is what makes a plain
        // appendChild sufficient here.
        var timelineArea = opts.timelineAreaId ? document.getElementById(opts.timelineAreaId) : null;
        var timelineHome = null;

        function moveTimelineIntoFullscreen() {
            if (!timelineArea || timelineArea.parentElement === frameEl) return;
            // Remembered as (parent, nextSibling) rather than an index so the element goes back
            // exactly where it was even if siblings changed while it was away.
            timelineHome = { parent: timelineArea.parentElement, before: timelineArea.nextSibling };
            timelineArea.classList.add('pb-timeline-fullscreen');
            frameEl.appendChild(timelineArea);
            // Reserve room on the right for pb-fullscreen-controls (mute/volume/exit) — both anchor
            // to this same bottom-right corner, and without this the timeline's own canvases (still
            // draggable for scrubbing) sat directly underneath the control buttons on mobile, where
            // there's no hover to reveal one and hide the other: confirmed live as controls that were
            // both visually buried under the timeline's backdrop and unclickable, since the timeline
            // canvas — a later sibling — was capturing the tap as a scrub-drag first. Measured from
            // controls' own actual rendered width (already display:flex per .tile-fullscreen by the
            // time this runs, same fullscreenchange handler that toggled that class runs before this
            // callback) rather than a guessed constant, so this stays correct whether or not this
            // camera has audio (a volume slider roughly doubles the control area's width).
            if (controls) timelineArea.style.paddingRight = (controls.getBoundingClientRect().width + 12) + 'px';
        }

        function restoreTimeline() {
            if (!timelineArea || !timelineHome) return;
            // fullscreen-tile.js's fullscreenchange handler fires for *every* wired tile, not just
            // the one whose state changed, so a tile that doesn't currently hold the timeline must
            // not yank it out of whichever tile does.
            if (timelineArea.parentElement !== frameEl) { timelineHome = null; return; }
            timelineArea.classList.remove('pb-timeline-fullscreen');
            timelineArea.style.paddingRight = '';
            timelineHome.parent.insertBefore(timelineArea, timelineHome.before);
            timelineHome = null;
        }

        var fsHandle = window.larisvmsFullscreenTile.wire(frameEl, videoEl, {
            onFullscreenChange: function (active) {
                if (active) moveTimelineIntoFullscreen(); else restoreTimeline();
                if (!fsBtn) return;
                fsBtn.textContent = active ? '⤢' : '⛶';
                fsBtn.title = active ? 'Exit fullscreen' : 'Fullscreen';
            }
        });
        if (fsBtn) {
            fsBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                if (fsHandle.isFullscreen()) fsHandle.exitFullscreen();
                else frameEl.requestFullscreen().catch(function () { /* ignore */ });
            });
        }
    }

    async function getBucketsForPrimary(fromIso, toIso, bucketCount) {
        if (!primaryCameraId) return [];
        var url = '/api/cameras/' + primaryCameraId + '/timeline?from=' + encodeURIComponent(fromIso) +
            '&to=' + encodeURIComponent(toIso) + '&buckets=' + bucketCount;
        var resp = await fetch(url);
        return resp.ok ? await resp.json() : [];
    }

    async function getGlobalBuckets(fromIso, toIso, bucketCount) {
        // Scoped to the cameras actually in the current view (tiles' own keys) rather than every
        // camera in the system — an "overview" timeline for a 2-camera view showing activity from
        // four cameras nobody's looking at here was confusing, not useful.
        var url = '/api/timeline?from=' + encodeURIComponent(fromIso) + '&to=' + encodeURIComponent(toIso) + '&buckets=' + bucketCount;
        Object.keys(tiles).forEach(function (id) { url += '&cameraIds=' + encodeURIComponent(id); });
        var resp = await fetch(url);
        return resp.ok ? await resp.json() : [];
    }

    // Returns a Promise resolving once every tile's own seekTo has settled — existing callers (arrow-
    // key nudge, timeline scrub/click) fire this without awaiting, which still works unchanged, since
    // a Promise nobody awaits just runs to completion on its own. The stepped high-speed loop below
    // (applySpeed) is the one caller that actually needs to wait for it — see its own comment for why.
    function seekAll(targetMs, autoplay) {
        playheadMs = targetMs;
        var pending = Object.keys(tiles).map(function (id) { return tiles[id].player.seekTo(targetMs, autoplay); });
        syncTimelineCenters();
        schedulePositionSave();
        var el = opts.currentTimeId && document.getElementById(opts.currentTimeId);
        if (el) el.textContent = new Date(targetMs).toLocaleString();
        return Promise.all(pending);
    }

    // Both timelines always track the one shared playhead — under the tape-scrubber model
    // (timeline.js) the marker itself never moves, so keeping the strip in sync means recentering
    // it on the current playhead any time that changes, whether from a drag on *either* timeline,
    // a click, or normal playback advancing (see updatePlayhead).
    function syncTimelineCenters() {
        if (timeline) timeline.setCenter(playheadMs);
        if (globalTimeline) globalTimeline.setCenter(playheadMs);
    }

    // Makes cameraId drive the per-camera timeline instead of whichever camera was first in the
    // view's reading order. Re-highlights every tile's border/star rather than a full rebuild —
    // cheap and doesn't interrupt anything already playing.
    function selectPrimary(cameraId) {
        if (cameraId === primaryCameraId || !tiles[cameraId]) return;
        primaryCameraId = cameraId;
        Object.keys(tiles).forEach(function (id) {
            var tileEl = document.querySelector('[data-playback-tile="' + id + '"]');
            if (!tileEl) return;
            var frameEl = tileEl.querySelector('.pb-tile-frame');
            var nameEl = tileEl.querySelector('.pb-select-primary');
            var isPrimary = id === cameraId;
            if (frameEl) frameEl.style.outline = isPrimary ? '3px solid var(--bs-primary)' : 'none';
            if (nameEl) nameEl.textContent = (isPrimary ? '★ ' : '') + cameraById[id].name;
        });
        if (timeline) timeline.reload();
    }

    // View cells are ordered top-to-bottom/left-to-right (same convention Pages/Views/Play uses
    // for its derived mobile layout) so "the view's top-left-most camera" is well-defined and
    // matches what a viewer would naturally read as "the first one".
    function orderedCells(view) {
        var layout = { cells: [] };
        try {
            var p = JSON.parse(view.layoutJson);
            if (p && Array.isArray(p.cells)) layout = p;
        } catch (e) { /* corrupted layout — treat as empty rather than fail the page */ }

        return layout.cells
            .filter(function (c) { return cameraById[c.cameraId]; }) // camera since removed/disabled
            .slice()
            .sort(function (a, b) { return (a.y - b.y) || (a.x - b.x); });
    }

    // The playhead starts at Date.now() (see init()) — almost never covered by an actual
    // recording, since footage is always somewhat behind "right now" and there may be nothing
    // recording live at all in a test/dev setup. Left uncorrected, every tile's first seek finds
    // no segment, nothing ever loads into a <video>, and pressing Play has no source to play —
    // confirmed as exactly this after the CSS zoom-button fix shipped. Resolving to the most
    // recent actual recording (looked up fresh per view, since different views can have different
    // primary cameras) gives Play something to play immediately, same as any DVR defaulting to
    // "most recent footage" rather than a bare clock reading.
    async function resolveInitialPlayheadMs(cameraId) {
        if (!cameraId) return Date.now();
        try {
            var toMs = Date.now();
            var fromMs = toMs - 7 * 24 * 3600 * 1000; // look back a week for "most recent recording"
            var url = '/api/cameras/' + cameraId + '/segments?from=' + encodeURIComponent(new Date(fromMs).toISOString()) +
                '&to=' + encodeURIComponent(new Date(toMs).toISOString());
            var resp = await fetch(url);
            if (!resp.ok) return Date.now();
            var list = await resp.json();
            if (!list.length) return Date.now(); // genuinely nothing in the last week — "now" is as good a default as any
            var mostRecent = list[list.length - 1]; // GetSegmentsAsync orders by StartUtc ascending
            return new Date(mostRecent.startUtc).getTime();
        } catch (e) {
            return Date.now();
        }
    }
    // Exported so Live's per-tile playback toggle (Pages/Live/Index.cshtml) can default a
    // newly-toggled tile to its most recent recording too, instead of duplicating this lookup.
    window.larisvmsPlaybackPlayer.resolveInitialPlayheadMs = resolveInitialPlayheadMs;

    // Audit-only ping: which cameras a user actually reviewed is resolved entirely client-side on
    // this page (the view picker never round-trips to the server), so without this there is no
    // server-side record of it at all. Fire-and-forget by design — the response is ignored and any
    // failure is swallowed, since an audit record must never be able to stop playback from starting.
    // Server-side, the camera list comes from the view's own stored layout, not from anything sent
    // here; this only names which view was opened.
    function reportViewOpened(viewId) {
        if (!viewId) return;
        try {
            fetch('/api/playback/view-opened', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ viewId: viewId })
            }).catch(function () { /* audit-only — never surface this to the user */ });
        } catch (e) { /* ditto */ }
    }

    // skipInitialSeek (M18): resolveDeepLink's own case — a Bookmark/Snapshot "▶ Play" link already
    // knows exactly which instant it wants and seeks there itself right after this returns. Without
    // this flag, this function's own persisted-position-or-most-recent seek below fired first
    // (unawaited, so this function returned before it finished) and the deep link's own seek raced
    // it — confirmed live as "bookmark playback starts at the beginning of the segment, not the
    // bookmark": whichever seek's segment lookup happened to resolve last won, not necessarily the
    // deep link's. Skipping it here removes the race entirely rather than trying to out-race it.
    async function rebuildTilesFromView(viewId, skipInitialSeek) {
        var view = viewsById[viewId];
        return rebuildTilesFromCells(view ? orderedCells(view) : [], viewId, skipInitialSeek);
    }

    // Split out from rebuildTilesFromView so a Bookmark/Snapshot deep link can render one camera
    // directly — no saved View needed at all, not even a synthetic one — rather than first having to
    // find a View that happens to contain the camera. That lookup used to be the deep link's only
    // path in, and a camera not currently placed in any View the viewer can see (a perfectly normal
    // state — not every camera has to be curated into a layout) surfaced as a dead end: "This
    // bookmark's camera isn't in any view you can see," even though the camera itself, and its
    // footage, were both right there. Explicit user ask, paired with the same single-camera capability
    // added to Pages/Live's own camera picker.
    //
    // viewId is null for a single-camera call — that's what suppresses the view-picker sync and the
    // audit ping below, both of which only make sense for a real, savable View.
    async function rebuildTilesFromCells(cells, viewId, skipInitialSeek) {
        Object.keys(tiles).forEach(function (id) { tiles[id].player.teardown(); });
        tiles = {};

        var tilesEl = document.getElementById(opts.tilesId);
        if (!tilesEl) return;
        tilesEl.innerHTML = '';

        if (viewId) reportViewOpened(viewId);

        // Deliberate divergence from Pages/Live and Views/Play, which faithfully reproduce a saved
        // view's own x/y/w/h arrangement and aspect ratios: Playback exists for review, where
        // maximizing each tile's video area matters more than preserving a curated layout, so this
        // ignores the view's stored geometry entirely and auto-fills an N-camera grid sized purely
        // by count — 1 fills the whole area, 2 sit side by side, 6 form a 3x2 grid, and so on.
        // Standard "smallest square-ish grid that fits N items" — ceil(sqrt(N)) columns, enough rows
        // to hold the rest; CSS Grid's own 1fr tracks handle resizing on window resize for free, no
        // resize listener needed for the grid itself (only for the flex area's own height — see
        // layoutForViewport in Pages/Playback/Index.cshtml).
        var n = cells.length;
        var cols = Math.max(1, Math.ceil(Math.sqrt(n)));
        var rows = Math.max(1, Math.ceil(n / cols));
        tilesEl.style.display = 'grid';
        tilesEl.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
        tilesEl.style.gridTemplateRows = 'repeat(' + rows + ', 1fr)';
        tilesEl.style.gap = '4px';
        // No explicit height/width:100% here — tilesEl is already a flex item (flex:1 1 auto;
        // min-height:0, set in Pages/Playback/Index.cshtml) inside the pbLayout column, and that's
        // what makes it fill exactly the space left after the toolbar and timeline claim theirs.
        // An explicit height:100% here used to fight that: a tall 9:16 camera's own intrinsic
        // aspect ratio (see the min-width/min-height:0 on each cell below — CSS Grid items default
        // to min-height:auto, i.e. "at least as tall as my content wants") could force this grid's
        // rows taller than the space actually available, growing tilesEl itself right over the
        // timeline below it. Confirmed live as a real bug: a vertical camera in the grid pushed
        // other tiles off the bottom of the page, and separately, video tiles covered the timeline.

        // Set before the loop below (not after, as this used to be) so the first render already
        // highlights the right tile instead of only picking it up on the next click.
        primaryCameraId = cells.length ? cells[0].cameraId : null;

        cells.forEach(function (cell) {
            var cam = cameraById[cell.cameraId];
            var el = document.createElement('div');
            el.setAttribute('data-playback-tile', cell.cameraId);
            // No explicit gridColumn/gridRow: grid auto-placement fills left-to-right, top-to-bottom
            // in DOM order, which orderedCells() has already sorted the same way (top-to-bottom,
            // left-to-right by the view's original y/x) — same reading order as before, just laid
            // into the new NxM grid instead of the view's own cell positions.
            // minWidth/minHeight:0 override CSS Grid's default min-size:auto on grid items (which
            // otherwise means "never shrink below my content's own intrinsic size") — without this,
            // a portrait (9:16) camera's video wants real height to preserve its aspect ratio even
            // when object-fit:contain is asked to shrink it, and that demand could grow this cell's
            // whole grid row (and the grid container itself) past the space actually available.
            // overflow:hidden is the backstop in case anything still tries to exceed the cell anyway.
            el.style.minWidth = '0';
            el.style.minHeight = '0';
            el.style.overflow = 'hidden';
            el.innerHTML = buildTileCardHtml(cam, cell.cameraId === primaryCameraId);

            var videoEl = el.querySelector('.pb-video');
            var statusEl = el.querySelector('.pb-status');
            var frameEl = el.querySelector('.pb-tile-frame');
            wireZoom(el, videoEl);
            wireFullscreen(frameEl, videoEl);
            // Whole cell is clickable to select it as primary, not just the name label — the name
            // element (which bubbles up to this same listener) keeps its pointer cursor as a hint,
            // but clicking anywhere else on the tile (the video, its background) works too. Zoom
            // buttons bubble here as well, which is fine: interacting with a tile's zoom controls
            // is itself a reasonable signal that camera should become primary.
            el.style.cursor = 'pointer';
            el.addEventListener('click', function () { selectPrimary(cell.cameraId); });
            tilesEl.appendChild(el);

            var player = window.larisvmsPlaybackPlayer.createTile(cell.cameraId, videoEl, statusEl, cam.codec, cam.hasAudio);
            tiles[cell.cameraId] = { player: player, videoEl: videoEl, statusEl: statusEl };
        });

        var playBtn = document.getElementById(opts.playPauseBtnId);
        if (playBtn) playBtn.disabled = cells.length === 0;

        if (skipInitialSeek) {
            applySpeed(); // freshly created <video> elements default to playbackRate 1 regardless of the selected speed
            return;
        }

        // A remembered position (from a previous visit) wins over "jump to most recent recording"
        // — once the user has scrubbed anywhere, that's a stronger signal of where they want to be
        // than a fresh guess. The most-recent-recording default only applies until the first
        // position is ever saved.
        var persisted = loadPersistedPosition();
        var initialMs = persisted ? persisted.centerMs : await resolveInitialPlayheadMs(primaryCameraId);
        if (persisted && timeline) timeline.setRange(persisted.rangeMs);
        if (persisted && globalTimeline) globalTimeline.setRange(persisted.rangeMs);
        if (timeline) timeline.setCenter(initialMs);
        if (globalTimeline) globalTimeline.setCenter(initialMs);
        // setCenter only redraws with whatever buckets are already loaded — reload() re-fetches
        // for the now-recentered range, so the visible timeline actually reflects it.
        if (timeline) timeline.reload();
        if (globalTimeline) globalTimeline.reload();
        seekAll(initialMs, false);
        applySpeed(); // freshly created <video> elements default to playbackRate 1 regardless of the selected speed
    }

    // Shared by togglePlay (toggling from whatever it currently is) and resolveDeepLink (forcing
    // straight to playing, regardless of the default paused-on-arrival state) — both need the same
    // button-text sync across the toolbar and every tile's fullscreen transport control.
    function setPlaying(value) {
        playing = value;
        var btn = document.getElementById(opts.playPauseBtnId);
        if (btn) btn.textContent = playing ? '⏸ Pause' : '▶ Play';
        // Every tile's fullscreen transport button mirrors the same state, so whichever one the user
        // is looking at reads correctly regardless of which control actually toggled it.
        document.querySelectorAll('.pb-fs-playpause').forEach(function (b) {
            b.textContent = playing ? '⏸' : '▶';
        });
        applySpeed();
    }

    function togglePlay() {
        setPlaying(!playing);
    }

    // ── Playback speed (M16), 1/32x-32x ──────────────────────────────────────
    // Native HTML5 <video>.playbackRate handles slow motion down to 1/32x with no special handling
    // needed — decoding *slower* than realtime has no extra cost. Fast-forward is the one with a
    // ceiling: continuous decode at, say, 32x would mean decoding-and-discarding 31 out of every 32
    // frames, real CPU cost for frames never shown, and most browsers clamp or visibly choke well
    // below that anyway. Above NATIVE_RATE_MAX this switches to a "stepped" mode instead: every tile
    // is paused and a timer periodically re-seeks the shared playhead forward by (tick * rate),
    // reusing the same per-segment seek path createTile.seekTo already uses for scrubbing. Each seek
    // decodes fresh from that segment's own keyframe rather than continuously decoding the frames in
    // between — effectively keyframe-driven fast-forward rather than true continuous playback, which
    // is what "I-frame-only decode" means in practice for this segment-fetch player architecture
    // (there is no in-app demuxer parsing GOP structure to selectively decode only I-frames from a
    // continuous stream).
    var NATIVE_RATE_MAX = 8;
    var STEP_TICK_MS = 200;

    function applySpeed() {
        stopStepLoop();
        var stepping = speedRate > NATIVE_RATE_MAX;

        Object.keys(tiles).forEach(function (id) {
            var tile = tiles[id];
            if (stepping) {
                tile.videoEl.pause();
            } else {
                // Routed through the player, not a direct videoEl.playbackRate assignment — a plain
                // assignment doesn't survive the next segment transition (every one calls
                // videoEl.load(), which resets it), so a fast-forward rate used to quietly fall back
                // to 1x the moment playback crossed into the next segment. setPlaybackRate remembers
                // the rate and re-applies it on every future segment load too. Confirmed live: 8x
                // "reset to normal speed after a few seconds" was exactly this, ~60s/8x apart.
                tile.player.setPlaybackRate(speedRate);
                if (playing) tile.videoEl.play().catch(function () { /* autoplay policy — user can press play again */ });
                else tile.videoEl.pause();
            }
        });

        if (stepping && playing) startStepLoop();
    }

    // The stepped high-speed loop waits for each step's own seek to fully settle (every tile) before
    // scheduling the next one, rather than firing on a fixed interval regardless of completion.
    // Confirmed live as necessary: a real segment fetch can easily take longer than one 200ms tick,
    // and firing a new seek before the previous one lands supersedes it (see seekTo's own
    // AbortController) before it ever gets to show a frame — with ticks close enough together, every
    // seek gets pre-empted by the next before finishing, and the tile is stuck on "Loading…"
    // permanently instead of stepping through frames. This is what "16x and 32x drop the video and
    // say Loading after a few keyframes" was. Self-scheduling via setTimeout after each step's own
    // await, rather than setInterval, is what lets the loop naturally slow to whatever the real
    // fetch/seek latency allows instead of piling up overlapping seeks.
    function stopStepLoop() {
        if (stepLoopToken) stepLoopToken.cancelled = true;
        stepLoopToken = null;
    }

    function startStepLoop() {
        var token = { cancelled: false };
        stepLoopToken = token;

        function scheduleNext() {
            if (!token.cancelled) setTimeout(tick, STEP_TICK_MS);
        }

        async function tick() {
            if (token.cancelled) return;
            // seekAll's own per-tile seekTo already catches and logs whatever it can, so nothing
            // here should actually reject — this is a last-resort guard against the loop dying
            // silently on an unexpected throw rather than a code path expected to run often.
            try { await seekAll(playheadMs + STEP_TICK_MS * speedRate, false); }
            catch (e) { console.error('[playback] stepped fast-forward tick failed', e); }
            scheduleNext();
        }

        scheduleNext();
    }

    function wireSpeed(selectId) {
        var select = selectId && document.getElementById(selectId);
        if (!select) return;
        select.addEventListener('change', function () {
            speedRate = parseFloat(select.value) || 1;
            applySpeed();
        });
    }

    // Prefers the primary (starred) tile, but falls back to any other tile that's actually
    // playing — confirmed live as a real gap: the primary specifically stalled (no segment found
    // for that camera at that moment) while every other tile in the view kept playing normally,
    // and the timeline sat frozen the whole time even though most of the grid was moving. One
    // camera's own playback trouble shouldn't be able to freeze the shared clock for every other
    // camera in the view. Returns the id alongside the tile so the caller can exclude it from
    // drift correction below — the driving tile is definitionally never "drifting" relative to
    // itself.
    function pickDrivingTile() {
        if (primaryCameraId && tiles[primaryCameraId] && !tiles[primaryCameraId].videoEl.paused) {
            return { id: primaryCameraId, tile: tiles[primaryCameraId] };
        }
        var ids = Object.keys(tiles);
        for (var i = 0; i < ids.length; i++) {
            if (!tiles[ids[i]].videoEl.paused) return { id: ids[i], tile: tiles[ids[i]] };
        }
        return null;
    }

    // How far a tile's own content-time is allowed to drift from the shared playheadMs before
    // being nudged back in line — confirmed live as a real problem (tiles ending up 10-30s apart),
    // traced mainly to each tile's segment-fetch time differing (now much smaller after the
    // streaming-load change above, but decode-rate variance between separate <video> elements can
    // still accumulate real drift over a long playback session even with an instant start). Loose
    // enough that ordinary per-tile timing jitter (a frame or two) never triggers a visible jump,
    // tight enough that "which camera did what, relative to another" stays trustworthy for review.
    var DRIFT_THRESHOLD_MS = 2000;

    // Nudges any other playing tile whose own content-time has drifted past DRIFT_THRESHOLD_MS
    // from the driving tile's — a small in-place currentTime correction (player.resyncTo), not a
    // re-seek/reload, so it doesn't interrupt playback or re-fetch anything. Only ever pulls a
    // drifted tile toward the driving tile, never the other way around — the driving tile's own
    // clock is playheadMs by definition.
    function resyncDriftingTiles(drivingId) {
        Object.keys(tiles).forEach(function (id) {
            if (id === drivingId) return;
            var tile = tiles[id];
            if (tile.videoEl.paused) return;
            var current = tile.player.currentWallClockMs();
            if (current === null) return;
            if (Math.abs(current - playheadMs) > DRIFT_THRESHOLD_MS) {
                tile.player.resyncTo(playheadMs);
            }
        });
    }

    // Drives the timeline strip and the "current time" readout from whichever tile is actually
    // playing (see pickDrivingTile) — under the tape-scrubber model (timeline.js) the marker is
    // fixed at center, so "following playback" means recentering the strip on the advancing time,
    // not moving a marker across it. Also the point where every other playing tile gets checked
    // for drift against that shared clock (see resyncDriftingTiles) — same 500ms tick, so drift
    // correction runs on the same cadence as the clock it corrects against, not a separate timer.
    function updatePlayhead() {
        var driving = pickDrivingTile();
        if (!driving) return;
        var current = driving.tile.player.currentWallClockMs();
        if (current === null) return;
        playheadMs = current;
        syncTimelineCenters();
        resyncDriftingTiles(driving.id);
        schedulePositionSave();
        var el = opts.currentTimeId && document.getElementById(opts.currentTimeId);
        if (el) el.textContent = new Date(playheadMs).toLocaleString();
    }

    // Waits for the preferences fetch (see user-preferences.js) before doing anything, so
    // loadHour24Preference()'s first read already reflects real data instead of the fallback — the
    // caller (Pages/Playback/Index.cshtml) never uses init()'s return value, so deferring the whole
    // body costs nothing beyond the wait itself, and user-preferences.js's own GET request already
    // started well before this script even ran.
    function init(o) {
        window.larisvmsPreferences.whenReady().then(function () { initImpl(o); });
    }

    function initImpl(o) {
        opts = o;
        cameraById = {};
        (o.cameras || []).forEach(function (c) { cameraById[c.id] = c; });
        viewsById = {};
        (o.views || []).forEach(function (v) { viewsById[v.id] = v; });

        var picker = document.getElementById(o.pickerId);
        if (picker) {
            picker.addEventListener('change', function () {
                saveViewId(picker.value);
                rebuildTilesFromView(picker.value);
            });
        }

        var hour24 = loadHour24Preference();
        var showEventTags = loadEventTagsPreference();

        // Zoom is applied at construction, not left to rebuildTilesFromView's own restore below.
        // That restore is reached only when a view is actually built, which two real paths skip: a
        // Bookmark/Snapshot deep link returns early from rebuildTilesFromView (skipInitialSeek) before
        // ever calling setRange, and a visit with no remembered view never calls it at all. In both
        // cases the timeline stayed at the 24h default — and worse, the seekAll that follows saves the
        // current range, so the default overwrote the user's real saved zoom. Confirmed as "the
        // timeline zoom resets between page loads", which clicking through Snapshots reproduces every
        // time. Setting it here makes the saved zoom independent of which path built the page.
        var persistedPosition = loadPersistedPosition();
        var initialRangeMs = persistedPosition ? persistedPosition.rangeMs : undefined;

        // Both timelines always show the same time window and zoom level — zooming or scrubbing
        // either one mirrors onto the other via setRange/setCenter (no-callback setters, so this
        // can't bounce a change back and forth between them).
        var canvas = document.getElementById(o.timelineCanvasId);
        if (canvas) {
            timeline = window.larisvmsTimeline.create(canvas, {
                hour24: hour24,
                showEventTags: showEventTags,
                initialRangeMs: initialRangeMs,
                getBuckets: getBucketsForPrimary,
                // Per-camera timeline only — globalTimeline below omits this entirely, since the
                // merged "all cameras" view has no single camera to preview. Reads primaryCameraId
                // from this module's own scope at hover time (same pattern getBucketsForPrimary
                // already uses), so it stays correct across selectPrimary() with no extra wiring.
                getThumbnailUrl: function (atMs) {
                    return primaryCameraId
                        ? '/playback-thumbnail/' + primaryCameraId + '?atUtc=' + encodeURIComponent(new Date(atMs).toISOString())
                        : null;
                },
                // M18: bookmark markers on the per-camera timeline — same primaryCameraId-at-call-time
                // pattern as getThumbnailUrl above, so this stays correct across selectPrimary() too.
                getBookmarks: function (fromIso, toIso) {
                    if (!primaryCameraId) return Promise.resolve([]);
                    var url = '/api/cameras/' + primaryCameraId + '/bookmarks?from=' + encodeURIComponent(fromIso) + '&to=' + encodeURIComponent(toIso);
                    return fetch(url).then(function (r) { return r.ok ? r.json() : []; }).catch(function () { return []; });
                },
                onScrub: function (ms) { seekAll(ms, playing); },
                onRangeChange: function (ms) { if (globalTimeline) globalTimeline.setRange(ms); schedulePositionSave(); }
            });
        }

        var globalCanvas = o.globalTimelineCanvasId && document.getElementById(o.globalTimelineCanvasId);
        if (globalCanvas) {
            globalTimeline = window.larisvmsTimeline.create(globalCanvas, {
                hour24: hour24,
                showEventTags: showEventTags,
                initialRangeMs: initialRangeMs, // kept in lockstep with the per-camera timeline above
                getBuckets: getGlobalBuckets,
                onScrub: function (ms) { seekAll(ms, playing); },
                onRangeChange: function (ms) { if (timeline) timeline.setRange(ms); schedulePositionSave(); }
            });
        }

        var hour24Toggle = o.hour24ToggleId && document.getElementById(o.hour24ToggleId);
        if (hour24Toggle) {
            hour24Toggle.checked = hour24;
            hour24Toggle.addEventListener('change', function () {
                var on = hour24Toggle.checked;
                saveHour24Preference(on);
                if (timeline) timeline.setHour24(on);
                if (globalTimeline) globalTimeline.setHour24(on);
            });
        }

        var eventTagsToggle = o.eventTagsToggleId && document.getElementById(o.eventTagsToggleId);
        if (eventTagsToggle) {
            eventTagsToggle.checked = showEventTags;
            eventTagsToggle.addEventListener('change', function () {
                var on = eventTagsToggle.checked;
                saveEventTagsPreference(on);
                if (timeline) timeline.setShowEventTags(on);
                if (globalTimeline) globalTimeline.setShowEventTags(on);
            });
        }

        wireSpeed(o.speedSelectId);

        var playBtn = document.getElementById(o.playPauseBtnId);
        if (playBtn) playBtn.addEventListener('click', togglePlay);

        if (o.currentTimeId) wireCurrentTimeEdit(o.currentTimeId);
        wireArrowKeyNudge();
        wireExportPanel();
        wireBookmark();

        if (o.deepLinkCameraId) {
            // A deliberate specific navigation (Bookmarks/Snapshots "▶ Play") outranks "whichever
            // view was open last time" — resolveDeepLink saves its own resolved view afterward, so
            // the *next* plain reload still remembers it.
            resolveDeepLink(o.deepLinkCameraId, o.deepLinkAtUtc);
        } else {
            var persistedViewId = loadPersistedViewId();
            if (persistedViewId && viewsById[persistedViewId] && picker) {
                picker.value = persistedViewId;
                rebuildTilesFromView(persistedViewId);
            }
        }

        setInterval(updatePlayhead, 500);
    }

    // M18: a Bookmark's "▶ Play" link (Pages/Bookmarks/Index) arrives as ?cameraId=&atUtc=
    // (Pages/Playback/Index.cshtml.cs) — this page has no camera picker of its own for a plain visit,
    // so resolving that into an actual tile happens entirely here.
    //
    // Renders the one camera directly (rebuildTilesFromCells with a single synthetic cell) rather
    // than searching for a View that contains it — no saved View is needed at all. Previously this
    // hunted for the first View (in picker order) placing the camera somewhere, which meant a
    // perfectly normal camera that simply isn't curated into any layout dead-ended with "isn't in any
    // view you can see" despite its footage being right there. Explicit user ask.
    async function resolveDeepLink(cameraId, atUtcIso) {
        var statusEl = opts.deepLinkStatusId && document.getElementById(opts.deepLinkStatusId);
        var cam = cameraById[cameraId];
        if (!cam) {
            // Deleted, disabled/unassigned since the bookmark was made, or CameraAccess no longer
            // grants this viewer Playback on it — cameraById is already narrowed the same way the
            // view picker's own camera list is, so this is the one check that covers all three.
            if (statusEl) {
                statusEl.textContent = "This camera is no longer available to you.";
                statusEl.className = 'small alert alert-warning py-1 px-2 mb-0';
            }
            return;
        }

        // Not a real View, so the picker is cleared rather than pointed at something misleading, and
        // nothing here is persisted as "the view to restore on the next plain visit" — a deep link is
        // a one-off destination, not a standing choice the way picking a View from the dropdown is.
        var picker = opts.pickerId && document.getElementById(opts.pickerId);
        if (picker) picker.value = '';
        // true: skip rebuildTilesFromCells' own persisted-position-or-most-recent seek — this
        // function's own seek just below is the only one that should ever run for a deep link.
        await rebuildTilesFromCells([{ cameraId: cameraId }], null, true);
        if (atUtcIso) {
            // Landing on a Bookmark/Snapshot deep link and still having to hit Play is one extra,
            // easy-to-miss step for what's supposed to be a one-click jump straight to the moment in
            // question — explicit user ask. Every other arrival at Playback (picking a view fresh,
            // reloading) still lands paused; this path alone starts moving immediately.
            setPlaying(true);
            await seekAll(new Date(atUtcIso).getTime(), true);
            // seekAll recenters both timelines' markers (syncTimelineCenters) but doesn't refetch
            // their bucket data for the new range on its own — without this they'd keep showing
            // whatever range was visible at tile-construction time (effectively "now").
            if (timeline) timeline.reload();
            if (globalTimeline) globalTimeline.reload();
        }
    }

    // ── Export (multi-camera video export trigger) ──────────────────────────
    // Small inline form, not a modal — checkboxes for every camera on this account (not just
    // whatever the currently selected view happens to render — with no view picked, or a
    // one-camera view, scoping to `tiles` used to leave 0 or 1 checkboxes, reading as "can't select
    // multiple cameras") plus a start/end range defaulted around the current playhead. Submits to
    // POST /api/exports and hands off to ExportJobDispatcher server-side; the actual per-camera work
    // and its results live on the Exports page, not here.
    function wireExportPanel() {
        var btn = opts.exportBtnId && document.getElementById(opts.exportBtnId);
        var panel = opts.exportPanelId && document.getElementById(opts.exportPanelId);
        if (!btn || !panel) return;

        btn.addEventListener('click', function () {
            var wasHidden = panel.classList.contains('d-none');
            if (wasHidden) populateExportPanel();
            panel.classList.toggle('d-none');
        });

        var submitBtn = opts.exportSubmitId && document.getElementById(opts.exportSubmitId);
        if (submitBtn) submitBtn.addEventListener('click', submitExport);
    }

    function populateExportPanel() {
        var camerasEl = opts.exportCamerasId && document.getElementById(opts.exportCamerasId);
        if (camerasEl) {
            camerasEl.innerHTML = '';
            // Checked by default only for cameras in the currently selected view (the "export what
            // I'm looking at" common case) — every other camera is still listed, just unchecked,
            // rather than omitted, so picking additional/different cameras is one click away.
            Object.keys(cameraById).forEach(function (id) {
                var cam = cameraById[id];
                var label = document.createElement('label');
                label.className = 'form-check form-check-inline mb-0';
                var input = document.createElement('input');
                input.type = 'checkbox';
                input.className = 'form-check-input pbExportCameraCheck';
                input.value = id;
                input.checked = !!tiles[id];
                var span = document.createElement('span');
                span.className = 'form-check-label small';
                span.textContent = cam ? cam.name : id;
                label.appendChild(input);
                label.appendChild(span);
                camerasEl.appendChild(label);
            });
        }

        // A 5-minute window centered on the current playhead — enough to be immediately useful for
        // the common "export what I'm looking at right now" case, adjustable before submitting for
        // anything longer.
        var fromEl = opts.exportFromId && document.getElementById(opts.exportFromId);
        var toEl = opts.exportToId && document.getElementById(opts.exportToId);
        if (fromEl) fromEl.value = msToLocalDatetimeInputValue(playheadMs - 5 * 60 * 1000);
        if (toEl) toEl.value = msToLocalDatetimeInputValue(playheadMs + 5 * 60 * 1000);

        var statusEl = opts.exportStatusId && document.getElementById(opts.exportStatusId);
        if (statusEl) { statusEl.textContent = ''; statusEl.className = 'small'; }
    }

    function submitExport() {
        var statusEl = opts.exportStatusId && document.getElementById(opts.exportStatusId);
        var camerasEl = opts.exportCamerasId && document.getElementById(opts.exportCamerasId);
        var fromEl = opts.exportFromId && document.getElementById(opts.exportFromId);
        var toEl = opts.exportToId && document.getElementById(opts.exportToId);
        var submitBtn = opts.exportSubmitId && document.getElementById(opts.exportSubmitId);
        if (!camerasEl || !fromEl || !toEl) return;

        // alert-* (not just a text color) so a validation/fetch failure reads as an unmissable
        // message next to the button rather than easy-to-miss small print — "the button doesn't seem
        // to do anything" was the actual symptom reported for what was really a silent validation
        // failure here.
        function setStatus(text, kind) {
            if (!statusEl) return;
            statusEl.textContent = text;
            statusEl.className = 'small' + (kind ? ' alert alert-' + kind + ' py-1 px-2 mb-0' : '');
        }

        var cameraIds = Array.prototype.slice.call(camerasEl.querySelectorAll('.pbExportCameraCheck:checked'))
            .map(function (cb) { return cb.value; });
        if (cameraIds.length === 0) {
            setStatus('Select at least one camera.', 'danger');
            return;
        }

        // datetime-local values parse as local time via `new Date(value)`, same as
        // msToLocalDatetimeInputValue's own round trip assumes.
        var fromMs = new Date(fromEl.value).getTime();
        var toMs = new Date(toEl.value).getTime();
        if (!isFinite(fromMs) || !isFinite(toMs) || toMs <= fromMs) {
            setStatus('End time must be after start time.', 'danger');
            return;
        }

        setStatus('Starting…', null);
        if (submitBtn) submitBtn.disabled = true;

        fetch('/api/exports', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                cameraIds: cameraIds,
                fromUtc: new Date(fromMs).toISOString(),
                toUtc: new Date(toMs).toISOString()
            })
        }).then(function (resp) {
            if (!resp.ok) return resp.text().then(function (t) { throw new Error(t || ('HTTP ' + resp.status)); });
            return resp.json();
        }).then(function () {
            setStatus('Export started — see the Exports page.', 'success');
        }).catch(function (err) {
            setStatus('Failed to start export: ' + (err && err.message ? err.message : err), 'danger');
        }).finally(function () {
            if (submitBtn) submitBtn.disabled = false;
        });
    }

    // ── Bookmark (M18) ────────────────────────────────────────────────────
    // Marks the *primary* tile's camera at the current playhead — same small-inline-form-not-a-modal
    // shape as the export panel above. There's deliberately no camera/time picker here: a bookmark is
    // one instant on one camera's own timeline, and that's exactly what primaryCameraId/playheadMs
    // already track.
    function wireBookmark() {
        var btn = opts.bookmarkBtnId && document.getElementById(opts.bookmarkBtnId);
        var panel = opts.bookmarkPanelId && document.getElementById(opts.bookmarkPanelId);
        if (!btn || !panel) return;

        btn.addEventListener('click', function () {
            var wasHidden = panel.classList.contains('d-none');
            if (wasHidden) populateBookmarkPanel();
            panel.classList.toggle('d-none');
        });

        var submitBtn = opts.bookmarkSubmitId && document.getElementById(opts.bookmarkSubmitId);
        if (submitBtn) submitBtn.addEventListener('click', submitBookmark);
    }

    function populateBookmarkPanel() {
        var targetEl = opts.bookmarkTargetId && document.getElementById(opts.bookmarkTargetId);
        if (targetEl) {
            var cam = primaryCameraId && cameraById[primaryCameraId];
            targetEl.textContent = cam
                ? cam.name + ' @ ' + new Date(playheadMs).toLocaleString()
                : 'No camera selected.';
        }
        var noteEl = opts.bookmarkNoteId && document.getElementById(opts.bookmarkNoteId);
        if (noteEl) noteEl.value = '';
        var statusEl = opts.bookmarkStatusId && document.getElementById(opts.bookmarkStatusId);
        if (statusEl) { statusEl.textContent = ''; statusEl.className = 'small'; }
    }

    function submitBookmark() {
        var statusEl = opts.bookmarkStatusId && document.getElementById(opts.bookmarkStatusId);
        var noteEl = opts.bookmarkNoteId && document.getElementById(opts.bookmarkNoteId);
        var submitBtn = opts.bookmarkSubmitId && document.getElementById(opts.bookmarkSubmitId);
        if (!noteEl) return;

        function setStatus(text, kind) {
            if (!statusEl) return;
            statusEl.textContent = text;
            statusEl.className = 'small' + (kind ? ' alert alert-' + kind + ' py-1 px-2 mb-0' : '');
        }

        if (!primaryCameraId) {
            setStatus('No camera selected.', 'danger');
            return;
        }
        var note = noteEl.value.trim();
        if (!note) {
            setStatus('Enter a note.', 'danger');
            return;
        }

        setStatus('Saving…', null);
        if (submitBtn) submitBtn.disabled = true;

        fetch('/api/bookmarks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                cameraId: primaryCameraId,
                timestampUtc: new Date(playheadMs).toISOString(),
                note: note
            })
        }).then(function (resp) {
            if (!resp.ok) return resp.text().then(function (t) { throw new Error(t || ('HTTP ' + resp.status)); });
            return resp.json();
        }).then(function () {
            setStatus('Bookmark saved.', 'success');
            noteEl.value = '';
            // Without this the new marker never appears until something unrelated (a drag, a camera
            // switch) happens to trigger the per-camera timeline's own reload — confirmed live as
            // "bookmarks not showing on the timeline" right after saving one.
            if (timeline) timeline.reload();
        }).catch(function (err) {
            setStatus('Failed to save bookmark: ' + (err && err.message ? err.message : err), 'danger');
        }).finally(function () {
            if (submitBtn) submitBtn.disabled = false;
        });
    }

    window.larisvmsPlaybackPage = { init: init };
})();

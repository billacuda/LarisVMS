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

        async function loadSegment(segment, seekSeconds, autoplay, myToken, signal) {
            teardown();
            loadToken = myToken; // teardown() bumped it again — restore the token this call owns
            if (statusEl) statusEl.textContent = 'Loading…';

            var mimeType = pickMimeType(codecHint, hasAudio);
            if (!mimeType) {
                if (statusEl) statusEl.textContent = 'No supported codec for this browser.';
                return;
            }

            var resp;
            try {
                resp = await fetch('/playback-segment/' + cameraId + '/' + segment.id, { signal: signal });
            } catch (e) {
                // A superseded seek's fetch lands here once aborted — not a real failure, the next
                // seek already owns the tile, so no status text should overwrite whatever it sets.
                if (!signal.aborted && statusEl) statusEl.textContent = 'Could not reach server.';
                return;
            }
            if (myToken !== loadToken || resp === undefined) return;
            if (!resp.ok) {
                // 404 specifically means "this file doesn't exist on the recorder" — a state that
                // isn't going to resolve itself moments later, unlike a 5xx (a transient node/proxy
                // hiccup, worth retrying on the next seek rather than blacklisting for the rest of
                // this page load).
                if (resp.status === 404) knownBadSegmentIds[segment.id] = true;
                if (statusEl) statusEl.textContent = 'Playback error (' + resp.status + ').';
                return;
            }

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
                    return;
                }

                if (resp.body) {
                    streamIntoSourceBuffer(resp.body.getReader(), sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay);
                } else {
                    // No streaming response body support (older browser, or an intermediary that
                    // buffers the whole thing) — fall back to the previous whole-file behavior
                    // rather than failing outright. resp is still the same Response from the
                    // enclosing loadSegment closure; arrayBuffer() works on it regardless of
                    // whether .body (the streaming reader) is exposed.
                    loadWholeBufferFallback(resp, sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay);
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
        function streamIntoSourceBuffer(reader, sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay) {
            var startedPlayback = false;

            function tryStartPlaybackOnce() {
                if (startedPlayback || myToken !== loadToken) return;
                var buffered = sourceBuffer.buffered;
                if (!buffered.length) return; // first chunk hasn't been processed into a range yet
                startedPlayback = true;

                // seekSeconds is an offset from the segment's *wall-clock* start, but a recorded
                // fMP4's internal timeline doesn't have to begin at zero — these carry the
                // baseMediaDecodeTime they were written with, so buffered can start at an arbitrary
                // large value (confirmed live: seeking to the raw offset landed outside the
                // buffered range entirely and rendered nothing, with no error). No upper-bound
                // clamp is needed here the way the old whole-file version needed one: if the target
                // is further into the segment than has streamed in yet, setting currentTime there
                // anyway is exactly correct — HTMLMediaElement natively waits for the data to
                // arrive and resumes on its own once it does, the same way any progressively
                // downloaded video works.
                var bufStart = buffered.start(0);
                currentSegmentTimeOrigin = bufStart;
                videoEl.currentTime = Math.max(bufStart, bufStart + seekSeconds);
                if (statusEl) statusEl.textContent = '';
                if (autoplay) videoEl.play().catch(function () { /* blocked by autoplay policy — stays paused with a visible control */ });
            }

            function finish() {
                if (myToken !== loadToken) return;
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
                        if (statusEl) statusEl.textContent = 'Playback error: ' + e.message;
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
        async function loadWholeBufferFallback(resp, sourceBuffer, mediaSource, myToken, signal, seekSeconds, autoplay) {
            var bytes;
            try {
                bytes = new Uint8Array(await resp.arrayBuffer());
            } catch (e) {
                if (!signal.aborted && statusEl) statusEl.textContent = 'Playback error reading segment.';
                return;
            }
            if (myToken !== loadToken) return;

            sourceBuffer.addEventListener('updateend', function () {
                if (myToken !== loadToken) return;
                if (mediaSource.readyState === 'open') {
                    try { mediaSource.endOfStream(); } catch (e) { /* already ended/detached */ }
                }
                var buffered = sourceBuffer.buffered;
                if (buffered.length) {
                    var bufStart = buffered.start(0);
                    currentSegmentTimeOrigin = bufStart;
                    videoEl.currentTime = Math.max(bufStart, bufStart + seekSeconds);
                } else {
                    if (statusEl) statusEl.textContent = 'Segment could not be decoded.';
                    return;
                }
                if (statusEl) statusEl.textContent = '';
                if (autoplay) videoEl.play().catch(function () { /* blocked by autoplay policy */ });
            });
            try {
                sourceBuffer.appendBuffer(bytes);
            } catch (e) {
                if (statusEl) statusEl.textContent = 'Playback error: ' + e.message;
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
                return;
            }

            if (knownBadSegmentIds[segment.id]) {
                // Already confirmed unreachable this page load — see knownBadSegmentIds' own
                // comment for why re-fetching it here matters, not just cosmetically.
                teardown();
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
            currentWallClockMs: function () {
                var seg = segments.find(function (s) { return s.id === currentSegmentId; });
                return seg ? seg.startUtc + (videoEl.currentTime - currentSegmentTimeOrigin) * 1000 : null;
            },
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
            teardown: teardown
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

    // ── Remembered timeline position / display preference ──────────────────
    // localStorage, not sessionStorage: "remember between refreshes" means a normal page reload,
    // which sessionStorage would also survive, but the user's actual ask was persistence across
    // browser restarts too, and there's nothing sensitive in a timestamp+zoom-level pair.
    var POSITION_KEY = 'larisvms.playback.position';
    var HOUR24_KEY = 'larisvms.playback.hour24';
    var savePositionTimer = null;

    function loadPersistedPosition() {
        try {
            var raw = localStorage.getItem(POSITION_KEY);
            if (!raw) return null;
            var parsed = JSON.parse(raw);
            if (typeof parsed.centerMs === 'number' && typeof parsed.rangeMs === 'number') return parsed;
        } catch (e) { /* corrupted/blocked storage — fall back to the usual default */ }
        return null;
    }

    // Debounced — this fires on every playback tick (500ms) and every throttled drag-scrub tick,
    // and a write on each one would be needless localStorage churn for a value that only needs to
    // be current by the time the tab actually closes or reloads.
    function schedulePositionSave() {
        clearTimeout(savePositionTimer);
        savePositionTimer = setTimeout(function () {
            if (!timeline) return;
            try {
                localStorage.setItem(POSITION_KEY, JSON.stringify({ centerMs: playheadMs, rangeMs: timeline.getRange() }));
            } catch (e) { /* private browsing or storage full — position just won't survive a reload */ }
        }, 500);
    }

    function loadHour24Preference() {
        try { return localStorage.getItem(HOUR24_KEY) === '1'; } catch (e) { return false; }
    }

    function saveHour24Preference(on) {
        try { localStorage.setItem(HOUR24_KEY, on ? '1' : '0'); } catch (e) { /* ignore */ }
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
                    'style="width:100%; height:100%; object-fit:contain; transform-origin:center center; cursor:default;"></video>' +
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
                        '<button type="button" class="btn btn-outline-light pb-fullscreen-toggle" title="Fullscreen" style="padding:.1rem .35rem;">⛶</button>' +
                    '</div>' +
                '</div>' +
            '</div>'
        );
    }

    // Digital zoom: CSS transform scale/translate on the <video> element itself, no server
    // involvement. Drag-to-pan only engages once zoomed in past 1x.
    function wireZoom(tileEl, videoEl) {
        var zoom = 1, panX = 0, panY = 0;
        function apply() { videoEl.style.transform = 'scale(' + zoom + ') translate(' + panX + 'px, ' + panY + 'px)'; }

        var inBtn = tileEl.querySelector('.pb-zoom-in');
        var outBtn = tileEl.querySelector('.pb-zoom-out');
        var resetBtn = tileEl.querySelector('.pb-zoom-reset');
        if (inBtn) inBtn.addEventListener('click', function () { zoom = Math.min(4, zoom + 0.5); apply(); });
        if (outBtn) outBtn.addEventListener('click', function () {
            zoom = Math.max(1, zoom - 0.5);
            if (zoom === 1) { panX = 0; panY = 0; }
            apply();
        });
        if (resetBtn) resetBtn.addEventListener('click', function () { zoom = 1; panX = 0; panY = 0; endDrag(); apply(); });

        // <video> is a native drag source in Chrome/Edge (you can drag a frame out like an image) —
        // with no preventDefault here, a mousedown-then-move over the video could kick off *that*
        // native drag concurrently with this pan handler. Once it does, the browser owns the mouse
        // gesture for the rest of that drag (showing the no-drop/circle-slash cursor over anything
        // that isn't a valid drop target, which is everywhere on this page, including this same
        // video and the timeline canvases below it) and our own mousemove/mouseup below stop getting
        // sane deltas — confirmed live as exactly this: dragging felt "random", working on one
        // timeline/tile but not another, because it depended on whether that particular mousedown
        // happened to also trigger a native dragstart. preventDefault on mousedown is the documented
        // way to suppress it. Returned early (no preventDefault) at zoom<=1 on purpose: that's a
        // plain click with nothing to pan, so page defaults like text selection elsewhere are left
        // alone.
        // Pointer events + setPointerCapture, not mouse events on window — see fullscreen-tile.js's
        // matching handler (and timeline.js's original write-up) for why: a lost mouseup left
        // `dragging` stuck true forever, and from then on every mousemove anywhere on the page kept
        // panning this video, intermittently stealing gestures aimed at other controls.
        var dragging = false, startX = 0, startY = 0, startPanX = 0, startPanY = 0, dragPointerId = null;

        function endDrag() {
            if (!dragging) return;
            dragging = false;
            if (dragPointerId !== null && videoEl.hasPointerCapture(dragPointerId)) {
                videoEl.releasePointerCapture(dragPointerId);
            }
            dragPointerId = null;
            videoEl.style.cursor = 'default';
        }

        videoEl.addEventListener('pointerdown', function (e) {
            if (e.button !== 0 || zoom <= 1) return;
            e.preventDefault();
            dragging = true;
            startX = e.clientX; startY = e.clientY;
            startPanX = panX; startPanY = panY;
            dragPointerId = e.pointerId;
            videoEl.setPointerCapture(e.pointerId);
            videoEl.style.cursor = 'grabbing';
        });
        videoEl.addEventListener('pointermove', function (e) {
            if (!dragging) return;
            panX = startPanX + (e.clientX - startX) / zoom;
            panY = startPanY + (e.clientY - startY) / zoom;
            apply();
        });
        videoEl.addEventListener('pointerup', endDrag);
        videoEl.addEventListener('pointercancel', endDrag);
    }

    // Double-click-to-fullscreen + wheel-zoom/drag-pan (fullscreen-tile.js), plus the audio/exit
    // controls that are the only ones left visible once this tile is the fullscreen element —
    // see .tile-fullscreen in site.css, which hides the normal-grid-view zoom buttons and the
    // primary-select badge while it's active. frameEl is .pb-tile-frame, not the outer grid-cell
    // wrapper, so fullscreening it doesn't also fullscreen this tile's grid-sizing wrapper element.
    function wireFullscreen(frameEl, videoEl) {
        var controls = frameEl.querySelector('.pb-fullscreen-controls');
        var fsBtn = frameEl.querySelector('.pb-fullscreen-toggle');

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
        }

        function restoreTimeline() {
            if (!timelineArea || !timelineHome) return;
            // fullscreen-tile.js's fullscreenchange handler fires for *every* wired tile, not just
            // the one whose state changed, so a tile that doesn't currently hold the timeline must
            // not yank it out of whichever tile does.
            if (timelineArea.parentElement !== frameEl) { timelineHome = null; return; }
            timelineArea.classList.remove('pb-timeline-fullscreen');
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

    function seekAll(targetMs, autoplay) {
        playheadMs = targetMs;
        Object.keys(tiles).forEach(function (id) { tiles[id].player.seekTo(targetMs, autoplay); });
        syncTimelineCenters();
        schedulePositionSave();
        var el = opts.currentTimeId && document.getElementById(opts.currentTimeId);
        if (el) el.textContent = new Date(targetMs).toLocaleString();
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

    async function rebuildTilesFromView(viewId) {
        Object.keys(tiles).forEach(function (id) { tiles[id].player.teardown(); });
        tiles = {};

        var tilesEl = document.getElementById(opts.tilesId);
        if (!tilesEl) return;
        tilesEl.innerHTML = '';

        reportViewOpened(viewId);

        var view = viewsById[viewId];
        var cells = view ? orderedCells(view) : [];

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
    }

    function togglePlay() {
        playing = !playing;
        var btn = document.getElementById(opts.playPauseBtnId);
        if (btn) btn.textContent = playing ? '⏸ Pause' : '▶ Play';
        Object.keys(tiles).forEach(function (id) {
            var v = tiles[id].videoEl;
            if (playing) v.play().catch(function () { /* autoplay policy — user can press play again */ });
            else v.pause();
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

    function init(o) {
        opts = o;
        cameraById = {};
        (o.cameras || []).forEach(function (c) { cameraById[c.id] = c; });
        viewsById = {};
        (o.views || []).forEach(function (v) { viewsById[v.id] = v; });

        var picker = document.getElementById(o.pickerId);
        if (picker) {
            picker.addEventListener('change', function () { rebuildTilesFromView(picker.value); });
        }

        var hour24 = loadHour24Preference();

        // Both timelines always show the same time window and zoom level — zooming or scrubbing
        // either one mirrors onto the other via setRange/setCenter (no-callback setters, so this
        // can't bounce a change back and forth between them).
        var canvas = document.getElementById(o.timelineCanvasId);
        if (canvas) {
            timeline = window.larisvmsTimeline.create(canvas, {
                hour24: hour24,
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
                onScrub: function (ms) { seekAll(ms, playing); },
                onRangeChange: function (ms) { if (globalTimeline) globalTimeline.setRange(ms); schedulePositionSave(); }
            });
        }

        var globalCanvas = o.globalTimelineCanvasId && document.getElementById(o.globalTimelineCanvasId);
        if (globalCanvas) {
            globalTimeline = window.larisvmsTimeline.create(globalCanvas, {
                hour24: hour24,
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

        var playBtn = document.getElementById(o.playPauseBtnId);
        if (playBtn) playBtn.addEventListener('click', togglePlay);

        if (o.currentTimeId) wireCurrentTimeEdit(o.currentTimeId);
        wireArrowKeyNudge();
        wireExportPanel();

        setInterval(updatePlayhead, 500);
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

    window.larisvmsPlaybackPage = { init: init };
})();

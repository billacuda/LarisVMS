// M7 playback: per-camera MSE player (nidusvmsPlaybackPlayer) plus the Pages/Playback page glue
// (nidusvmsPlaybackPage) that wires the view picker, both timelines (timeline.js), and one player
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
        var loadToken = 0; // bumped on every seek so a superseded in-flight fetch's *result* is a no-op on arrival
        var abortController = null; // actually cancels the superseded fetch itself, not just its result

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
                if (!resp.ok) return;
                var list = await resp.json();
                segments = list.map(function (s) {
                    return { id: s.id, startUtc: new Date(s.startUtc).getTime(), endUtc: new Date(s.endUtc).getTime() };
                });
            } catch (e) { /* aborted (superseded by a newer seek) or a real network error — either way, keep whatever was already loaded */ }
        }

        function teardown() {
            loadToken++;
            currentSegmentId = null;
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
                if (statusEl) statusEl.textContent = 'Playback error (' + resp.status + ').';
                return;
            }

            // Also inside try/catch, not just the fetch() call above: aborting mid-body-read
            // rejects here rather than at fetch(), which surfaced as an "Uncaught (in promise)
            // AbortError" on every drag-scrub once cancellation was added — harmless to playback
            // but real console noise, and an unhandled rejection either way.
            var bytes;
            try {
                bytes = new Uint8Array(await resp.arrayBuffer());
            } catch (e) {
                if (!signal.aborted && statusEl) statusEl.textContent = 'Playback error reading segment.';
                return;
            }
            if (myToken !== loadToken) return;

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
                sourceBuffer.addEventListener('updateend', function () {
                    if (myToken !== loadToken) return;
                    if (statusEl) statusEl.textContent = '';
                    videoEl.currentTime = seekSeconds;
                    if (autoplay) videoEl.play().catch(function () { /* blocked by autoplay policy — stays paused with a visible control */ });
                });
                try {
                    sourceBuffer.appendBuffer(bytes);
                } catch (e) {
                    if (statusEl) statusEl.textContent = 'Playback error: ' + e.message;
                }
            });
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

            if (segment.id === currentSegmentId) {
                videoEl.currentTime = (targetMs - segment.startUtc) / 1000;
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
                return seg ? seg.startUtc + videoEl.currentTime * 1000 : null;
            },
            teardown: teardown
        };
    }

    window.nidusvmsPlaybackPlayer = { createTile: createTile };
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

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // Bootstrap's ".ratio > *" rule forces every *direct* child of a .ratio container to
    // position:absolute;width:100%;height:100% — needed for the <video> itself, but it would do
    // the same to the zoom-button group if that were a direct child too, stretching each button to
    // fill the whole tile (confirmed: exactly this happened before this wrapper was added). The
    // video/status/buttons trio is wrapped in one plain inner div instead, so only that wrapper is
    // a direct .ratio child; no positioning classes on it on purpose, since it already becomes
    // position:absolute for free and is a valid containing block for its own absolutely-positioned
    // children — same fix Pages/Live's mute button needed (see CHANGELOG 0.7.0).
    function buildTileCardHtml(cam, isPrimary) {
        return (
            '<div class="card h-100' + (isPrimary ? ' border-primary border-2' : '') + '">' +
                // overflow-hidden here (safe on .ratio itself, unlike position-relative on a
                // .ratio child) clips the zoomed/panned video to the tile instead of letting it
                // spill over neighboring tiles once scaled past 1x.
                '<div class="ratio ratio-16x9 bg-dark overflow-hidden">' +
                    '<div>' +
                        '<video class="pb-video" muted playsinline ' +
                            'style="width:100%; height:100%; object-fit:contain; transform-origin:center center; cursor:default;"></video>' +
                        '<div class="position-absolute top-50 start-50 translate-middle text-white small text-center px-2 pb-status"></div>' +
                        '<div class="position-absolute top-0 end-0 m-1 btn-group btn-group-sm">' +
                            '<button type="button" class="btn btn-outline-light pb-zoom-out" title="Zoom out" style="padding:.1rem .35rem;">−</button>' +
                            '<button type="button" class="btn btn-outline-light pb-zoom-reset" title="Reset zoom" style="padding:.1rem .35rem;">⤢</button>' +
                            '<button type="button" class="btn btn-outline-light pb-zoom-in" title="Zoom in" style="padding:.1rem .35rem;">+</button>' +
                        '</div>' +
                    '</div>' +
                '</div>' +
                '<div class="card-body py-2 pb-select-primary" style="cursor:pointer;" ' +
                    'title="Click to make this camera drive the per-camera timeline">' +
                    (isPrimary ? '★ ' : '') + escHtml(cam.name) +
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
        if (resetBtn) resetBtn.addEventListener('click', function () { zoom = 1; panX = 0; panY = 0; apply(); });

        var dragging = false, startX = 0, startY = 0, startPanX = 0, startPanY = 0;
        videoEl.addEventListener('mousedown', function (e) {
            if (zoom <= 1) return;
            dragging = true;
            startX = e.clientX; startY = e.clientY;
            startPanX = panX; startPanY = panY;
            videoEl.style.cursor = 'grabbing';
        });
        window.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            panX = startPanX + (e.clientX - startX) / zoom;
            panY = startPanY + (e.clientY - startY) / zoom;
            apply();
        });
        window.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            videoEl.style.cursor = 'default';
        });
    }

    async function getBucketsForPrimary(fromIso, toIso, bucketCount) {
        if (!primaryCameraId) return [];
        var url = '/api/cameras/' + primaryCameraId + '/timeline?from=' + encodeURIComponent(fromIso) +
            '&to=' + encodeURIComponent(toIso) + '&buckets=' + bucketCount;
        var resp = await fetch(url);
        return resp.ok ? await resp.json() : [];
    }

    async function getGlobalBuckets(fromIso, toIso, bucketCount) {
        var url = '/api/timeline?from=' + encodeURIComponent(fromIso) + '&to=' + encodeURIComponent(toIso) + '&buckets=' + bucketCount;
        var resp = await fetch(url);
        return resp.ok ? await resp.json() : [];
    }

    function seekAll(targetMs, autoplay) {
        playheadMs = targetMs;
        Object.keys(tiles).forEach(function (id) { tiles[id].player.seekTo(targetMs, autoplay); });
        syncTimelineCenters();
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
            var cardEl = tileEl.querySelector('.card');
            var nameEl = tileEl.querySelector('.pb-select-primary');
            var isPrimary = id === cameraId;
            if (cardEl) {
                cardEl.classList.toggle('border-primary', isPrimary);
                cardEl.classList.toggle('border-2', isPrimary);
            }
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

    async function rebuildTilesFromView(viewId) {
        Object.keys(tiles).forEach(function (id) { tiles[id].player.teardown(); });
        tiles = {};

        var tilesEl = document.getElementById(opts.tilesId);
        if (!tilesEl) return;
        tilesEl.innerHTML = '';
        tilesEl.style.display = 'grid';
        tilesEl.style.gridTemplateColumns = 'repeat(12, 1fr)';
        tilesEl.style.gridAutoRows = '60px';
        tilesEl.style.gap = '6px';

        var view = viewsById[viewId];
        var cells = view ? orderedCells(view) : [];

        // Set before the loop below (not after, as this used to be) so the first render already
        // highlights the right tile instead of only picking it up on the next click.
        primaryCameraId = cells.length ? cells[0].cameraId : null;

        cells.forEach(function (cell) {
            var cam = cameraById[cell.cameraId];
            var el = document.createElement('div');
            el.setAttribute('data-playback-tile', cell.cameraId);
            el.style.gridColumn = (cell.x + 1) + ' / span ' + cell.w;
            el.style.gridRow = (cell.y + 1) + ' / span ' + cell.h;
            el.innerHTML = buildTileCardHtml(cam, cell.cameraId === primaryCameraId);

            var videoEl = el.querySelector('.pb-video');
            var statusEl = el.querySelector('.pb-status');
            wireZoom(el, videoEl);
            // Whole cell is clickable to select it as primary, not just the name label — the name
            // element (which bubbles up to this same listener) keeps its pointer cursor as a hint,
            // but clicking anywhere else on the tile (the video, its background) works too. Zoom
            // buttons bubble here as well, which is fine: interacting with a tile's zoom controls
            // is itself a reasonable signal that camera should become primary.
            el.style.cursor = 'pointer';
            el.addEventListener('click', function () { selectPrimary(cell.cameraId); });
            tilesEl.appendChild(el);

            var player = window.nidusvmsPlaybackPlayer.createTile(cell.cameraId, videoEl, statusEl, cam.codec, cam.hasAudio);
            tiles[cell.cameraId] = { player: player, videoEl: videoEl, statusEl: statusEl };
        });

        var playBtn = document.getElementById(opts.playPauseBtnId);
        if (playBtn) playBtn.disabled = cells.length === 0;

        var initialMs = await resolveInitialPlayheadMs(primaryCameraId);
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

    // Drives the timeline strip and the "current time" readout from the primary tile's own
    // <video>.currentTime while it plays — under the tape-scrubber model (timeline.js) the marker
    // is fixed at center, so "following playback" means recentering the strip on the advancing
    // time, not moving a marker across it. Not a separate driving clock, so long-running playback
    // can drift slightly between tiles started from the same synchronized seek. Acceptable for
    // this pass; see the CHANGELOG's Known limitations.
    function updatePlayhead() {
        var primary = primaryCameraId && tiles[primaryCameraId];
        if (!primary || primary.videoEl.paused) return;
        var current = primary.player.currentWallClockMs();
        if (current === null) return;
        playheadMs = current;
        syncTimelineCenters();
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

        var canvas = document.getElementById(o.timelineCanvasId);
        if (canvas) {
            timeline = window.nidusvmsTimeline.create(canvas, {
                getBuckets: getBucketsForPrimary,
                onScrub: function (ms) { seekAll(ms, playing); }
            });
        }

        var globalCanvas = o.globalTimelineCanvasId && document.getElementById(o.globalTimelineCanvasId);
        if (globalCanvas) {
            globalTimeline = window.nidusvmsTimeline.create(globalCanvas, {
                getBuckets: getGlobalBuckets,
                onScrub: function (ms) { seekAll(ms, playing); }
            });
        }

        var playBtn = document.getElementById(o.playPauseBtnId);
        if (playBtn) playBtn.addEventListener('click', togglePlay);

        setInterval(updatePlayhead, 500);
    }

    window.nidusvmsPlaybackPage = { init: init };
})();

// M6 read-only view display (Pages/Views/Play). Desktop renders the saved GridStack geometry with
// plain CSS grid (no GridStack JS needed just to display a fixed layout); phones get a derived
// single/two-column stack — ordered top-to-bottom/left-to-right from the desktop positions, sized
// from each cell's aspect ratio, cameras with hideOnPhone skipped — never persisted, same "a view
// built on desktop gets a usable phone view for free" idea as frcastr's dashboard.
window.larisvmsViewPlay = (function () {
    'use strict';

    var CELL_HEIGHT = 60; // matches the editor's GridStack cellHeight, for a consistent look
    var phoneQuery = window.matchMedia('(max-width: 767.98px)');

    var opts = null;
    var cells = [];
    var mobileTwoColumn = false;
    var cameraById = {};
    var stopFns = {};

    // Playback-toggle is single-cell-at-a-time, same as the flat Live grid this was ported from —
    // switching a second cell into playback mode reverts whichever cell was previously toggled back
    // to live first. Keyed by cell.id, not cameraId: a View can in principle place the same camera in
    // two cells, and stopFns above already uses cell.id for the same reason.
    var cellControllers = {};
    function exitAnyOtherPlaybackTile(exceptCellId) {
        Object.keys(cellControllers).forEach(function (id) {
            if (id !== exceptCellId) cellControllers[id].exitPlaybackModeIfActive();
        });
    }

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function buildCellHtml(cell) {
        var cam = cameraById[cell.cameraId];
        var name = cam ? cam.name : '';
        return (
            '<div class="h-100 d-flex flex-column border rounded overflow-hidden" data-camera-tile="' + cell.cameraId + '">' +
                '<div class="view-cell-frame position-relative flex-grow-1 bg-black" style="min-height: 0;">' +
                    '<video class="view-cell-video" style="width:100%; height:100%; object-fit:contain;" muted playsinline></video>' +
                    '<div class="position-absolute top-50 start-50 translate-middle text-white small text-center px-2 view-cell-status" role="status" aria-live="polite"></div>' +
                    '<span class="badge bg-danger position-absolute top-0 start-0 m-1 live-motion-badge d-none"' +
                        ' title="Motion detected">● Motion</span>' +
                    // Only shown once this one cell has been toggled into playback mode (see the
                    // view-cell-playback button below) — every other cell keeps showing pure live
                    // video with no timeline at all. Right clearance (96px) keeps it clear of the
                    // controls group's own corner, matching the flat grid this was ported from.
                    '<div class="position-absolute bottom-0 start-0 d-none d-flex align-items-end gap-1 p-1 view-cell-mini-timeline-area" style="right:96px;">' +
                        '<button type="button" class="btn btn-outline-light btn-sm view-cell-mini-playpause" style="padding:.1rem .35rem;" title="Play/Pause" aria-label="Play/Pause">▶</button>' +
                        // 30px, matching the Playback page's own timeline canvases — not smaller:
                        // timeline.js draws its coverage bar across the top ~55% and the tick marks
                        // plus their 9px labels *below* that, so anything much shorter clips the
                        // labels (and part of the bar) off the bottom of the canvas entirely.
                        '<canvas class="view-cell-mini-timeline flex-grow-1" style="height:30px; touch-action:none; cursor:pointer; display:block;"></canvas>' +
                    '</div>' +
                    // Exactly three buttons, shown on hover — not native <video controls>: that
                    // includes click-anywhere-on-the-video-to-pause in most browsers, which makes no
                    // sense for a continuous live feed and was confirmed live as unwanted.
                    '<div class="position-absolute bottom-0 end-0 m-1 btn-group btn-group-sm view-cell-controls d-none">' +
                        '<button type="button" class="btn btn-outline-light view-cell-mute" style="padding:.1rem .35rem;" title="Mute" aria-label="Mute">🔊</button>' +
                        '<button type="button" class="btn btn-outline-light view-cell-playback" style="padding:.1rem .35rem;" title="Playback" aria-label="Playback">⏱</button>' +
                        '<button type="button" class="btn btn-outline-light view-cell-fullscreen" style="padding:.1rem .35rem;" title="Fullscreen" aria-label="Fullscreen">⛶</button>' +
                    '</div>' +
                '</div>' +
                // flex-shrink-0: without it, a cell whose stored height is too short for its video
                // (e.g. a stale height from before an aspect-ratio change) squeezes this label toward
                // 0 first, clipping the camera name — better to let the video crop slightly than lose
                // the name entirely.
                '<div class="px-1 small text-truncate bg-body-tertiary flex-shrink-0">' + escHtml(name) + '</div>' +
            '</div>'
        );
    }

    // Reuses playback-player.js's own MSE/segment-fetch tile (exactly what the Playback page uses)
    // rather than reimplementing it — only the mini timeline + play/pause wrapper here is new. Ported
    // verbatim from the flat Live grid (removed — see CHANGELOG); the only real difference is
    // cellControllers/exitAnyOtherPlaybackTile above being keyed by cell.id instead of the flat
    // grid's cameraId, matching stopFns' own convention in this file.
    function startCellVideo(el, cell) {
        var cam = cameraById[cell.cameraId];
        var video = el.querySelector('.view-cell-video');
        var status = el.querySelector('.view-cell-status');
        if (!video || !cam) return;

        var hoverTarget = video.parentElement;
        var frameEl = el.querySelector('.view-cell-frame');
        var controls = el.querySelector('.view-cell-controls');
        var muteBtn = el.querySelector('.view-cell-mute');
        var fullscreenBtn = el.querySelector('.view-cell-fullscreen');
        var playbackToggleBtn = el.querySelector('.view-cell-playback');
        var miniTimelineArea = el.querySelector('.view-cell-mini-timeline-area');
        var miniTimelineCanvas = el.querySelector('.view-cell-mini-timeline');
        var miniPlayPauseBtn = el.querySelector('.view-cell-mini-playpause');

        var cameraId = cell.cameraId;
        var codec = cam.codec;
        var hasAudio = cam.hasAudio;

        var liveStop = null;
        var pbPlayer = null;
        var pbTimeline = null;
        var pbPlaying = false;
        var inPlaybackMode = false;

        if (hoverTarget && controls) {
            hoverTarget.addEventListener('mouseenter', function () { controls.classList.remove('d-none'); });
            hoverTarget.addEventListener('mouseleave', function () { controls.classList.add('d-none'); });
        }
        function setMuteIcon() {
            if (!muteBtn) return;
            muteBtn.textContent = video.muted ? '🔇' : '🔊';
            muteBtn.title = video.muted ? 'Unmute' : 'Mute';
        }
        if (muteBtn) {
            muteBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                video.muted = !video.muted;
                setMuteIcon();
            });
        }
        // Shared with Live/Playback (fullscreen-tile.js) — fullscreens frameEl (video + controls),
        // not the bare <video>, and wires the double-click gesture, same as those two pages. This
        // page's own fullscreenBtn just drives the same handle rather than calling
        // video.requestFullscreen() directly, which used to drop the mute/fullscreen buttons out of
        // the fullscreened render subtree entirely.
        var fsHandle = frameEl && window.larisvmsFullscreenTile
            ? window.larisvmsFullscreenTile.wire(frameEl, video, {
                onFullscreenChange: function (active) {
                    if (!fullscreenBtn) return;
                    fullscreenBtn.textContent = active ? '⤢' : '⛶';
                    fullscreenBtn.title = active ? 'Exit fullscreen' : 'Fullscreen';
                }
            })
            : null;
        if (fullscreenBtn) {
            fullscreenBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                if (fsHandle) {
                    if (fsHandle.isFullscreen()) fsHandle.exitFullscreen();
                    else frameEl.requestFullscreen().catch(function () {});
                } else if (video.requestFullscreen) {
                    video.requestFullscreen().catch(function () {});
                }
            });
        }

        function startLive() {
            // Retained per-cell specifically so entering playback mode can end this session cleanly
            // instead of leaving it running underneath the swapped-in player.
            liveStop = window.larisvmsLiveView.start(cameraId, video, status, codec, hasAudio);
        }

        async function enterPlaybackMode() {
            if (inPlaybackMode) return;
            exitAnyOtherPlaybackTile(cell.id);
            if (liveStop) { liveStop(); liveStop = null; }
            inPlaybackMode = true;
            if (playbackToggleBtn) { playbackToggleBtn.textContent = '📡'; playbackToggleBtn.title = 'Back to live'; }
            if (miniTimelineArea) miniTimelineArea.classList.remove('d-none');
            status.textContent = 'Loading…';

            pbPlayer = window.larisvmsPlaybackPlayer.createTile(cameraId, video, status, codec, hasAudio);
            // 30s back from now, not "most recent segment's start" — toggling playback on a *live*
            // cell is inherently "let me see what just happened," and on an actively-recording
            // camera the current segment's start can be minutes stale depending on where in the
            // segment rotation it lands.
            var initialMs = Date.now() - 30000;
            pbPlaying = false;
            if (miniPlayPauseBtn) miniPlayPauseBtn.textContent = '▶';
            if (miniTimelineCanvas) {
                pbTimeline = window.larisvmsTimeline.create(miniTimelineCanvas, {
                    initialCenterMs: initialMs,
                    initialRangeMs: 10 * 60 * 1000,
                    // Same per-camera coverage endpoint the Playback page's own timeline uses
                    // (getBucketsForPrimary) — without it timeline.js has no bucket data to draw and
                    // the strip renders as an empty gray track: no blue recorded coverage, no green
                    // motion, nothing to aim a scrub at.
                    getBuckets: async function (fromIso, toIso, bucketCount) {
                        var url = '/api/cameras/' + cameraId + '/timeline?from=' + encodeURIComponent(fromIso) +
                            '&to=' + encodeURIComponent(toIso) + '&buckets=' + bucketCount;
                        try {
                            var resp = await fetch(url);
                            return resp.ok ? await resp.json() : [];
                        } catch (e) {
                            return [];
                        }
                    },
                    onScrub: function (ms) { if (pbPlayer) pbPlayer.seekTo(ms, pbPlaying); }
                });
            }
            await pbPlayer.seekTo(initialMs, false);
        }

        function exitPlaybackModeIfActive() {
            if (!inPlaybackMode) return;
            inPlaybackMode = false;
            if (playbackToggleBtn) { playbackToggleBtn.textContent = '⏱'; playbackToggleBtn.title = 'Playback'; }
            if (miniTimelineArea) miniTimelineArea.classList.add('d-none');
            if (pbPlayer) { pbPlayer.teardown(); pbPlayer = null; }
            pbTimeline = null;
            startLive();
        }

        if (playbackToggleBtn) {
            playbackToggleBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                if (inPlaybackMode) exitPlaybackModeIfActive(); else enterPlaybackMode();
            });
        }
        if (miniPlayPauseBtn) {
            miniPlayPauseBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                if (!inPlaybackMode || !pbPlayer) return;
                pbPlaying = !pbPlaying;
                miniPlayPauseBtn.textContent = pbPlaying ? '⏸' : '▶';
                if (pbPlaying) video.play().catch(function () { /* autoplay policy */ }); else video.pause();
            });
        }

        cellControllers[cell.id] = { exitPlaybackModeIfActive: exitPlaybackModeIfActive };

        setMuteIcon();
        stopFns[cell.id] = function () {
            if (liveStop) liveStop();
            if (pbPlayer) pbPlayer.teardown();
        };
        startLive();
    }

    function stopAll() {
        Object.keys(stopFns).forEach(function (id) { stopFns[id](); });
        stopFns = {};
        // Cleared here too, not just left to be overwritten by the next startCellVideo pass — a
        // re-render (e.g. phone/desktop switch) can drop a cell entirely (hideOnPhone), which would
        // otherwise leave a stale controller referencing torn-down, detached DOM behind.
        cellControllers = {};
    }

    // Groups cells sharing a "row" the same way as the desktop layout reads: a row is defined by
    // the vertical span of its first cell — anything starting before that cell ends was beside it.
    function desktopRows(orderedCells) {
        var rows = [], bottom = -1;
        orderedCells.forEach(function (c) {
            if (rows.length && c.y < bottom) {
                rows[rows.length - 1].push(c);
            } else {
                rows.push([c]);
                bottom = c.y + c.h;
            }
        });
        return rows;
    }

    function renderDesktop(container) {
        var visible = cells.filter(function (c) { return cameraById[c.cameraId]; });
        container.style.display = 'grid';
        container.style.gridTemplateColumns = 'repeat(12, 1fr)';
        container.style.gridAutoRows = CELL_HEIGHT + 'px';
        container.style.gap = '6px';
        container.innerHTML = '';

        if (!visible.length) {
            container.innerHTML = '<p class="text-muted">This view has no cameras yet.</p>';
            return;
        }

        visible.forEach(function (cell) {
            var el = document.createElement('div');
            el.style.gridColumn = (cell.x + 1) + ' / span ' + cell.w;
            el.style.gridRow = (cell.y + 1) + ' / span ' + cell.h;
            el.innerHTML = buildCellHtml(cell);
            container.appendChild(el);
            startCellVideo(el, cell);
        });
    }

    function renderMobile(container) {
        var cols = mobileTwoColumn ? 2 : 1;
        var visible = cells.filter(function (c) { return !c.hideOnPhone && cameraById[c.cameraId]; });
        var ordered = visible.slice().sort(function (a, b) { return (a.y - b.y) || (a.x - b.x); });
        var rows = desktopRows(ordered);

        container.style.display = 'grid';
        container.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
        container.style.gap = '6px';
        container.style.gridAutoRows = '';
        container.innerHTML = '';

        if (!visible.length) {
            container.innerHTML = '<p class="text-muted">No cameras in this view are visible on phones.</p>';
            return;
        }

        rows.forEach(function (row) {
            for (var i = 0; i < row.length; i += cols) {
                var pair = row.slice(i, i + cols);
                pair.forEach(function (cell) {
                    var el = document.createElement('div');
                    var ratio = window.LarisVMSAspectRatio.isValid(cell.aspect) ? cell.aspect : window.LarisVMSAspectRatio.default;
                    el.style.aspectRatio = ratio.replace(':', '/');
                    if (pair.length < cols) el.style.gridColumn = '1 / -1'; // odd one out spans full width
                    el.innerHTML = buildCellHtml(cell);
                    container.appendChild(el);
                    startCellVideo(el, cell);
                });
            }
        });
    }

    function render() {
        stopAll();
        var container = document.getElementById(opts.gridElId);
        if (!container) return;
        if (phoneQuery.matches) renderMobile(container); else renderDesktop(container);
    }

    function wireKiosk(kioskBtnId, navElId, toolbarElId) {
        var btn = document.getElementById(kioskBtnId);
        if (!btn) return;
        btn.addEventListener('click', function () {
            if (!document.fullscreenElement) document.documentElement.requestFullscreen().catch(function () {});
            else document.exitFullscreen();
        });
        document.addEventListener('fullscreenchange', function () {
            var isFullscreen = !!document.fullscreenElement;
            var nav = document.getElementById(navElId);
            var toolbar = document.getElementById(toolbarElId);
            // The kiosk button lives in the toolbar being hidden here — the browser's own Esc
            // shortcut is what exits fullscreen while it's gone, same as any other fullscreen page.
            if (nav) nav.style.display = isFullscreen ? 'none' : '';
            if (toolbar) toolbar.style.display = isFullscreen ? 'none' : '';
        });
    }

    function wireTour(tourViewIds, tourIndex, tourIntervalSeconds) {
        if (!tourViewIds || tourViewIds.length < 2) return;
        var intervalMs = Math.max(3, tourIntervalSeconds || 15) * 1000;
        setTimeout(function () {
            var nextIndex = (tourIndex + 1) % tourViewIds.length;
            window.location.href = '/Views/Play/' + tourViewIds[nextIndex] + '?tour=true&i=' + nextIndex;
        }, intervalMs);
    }

    function init(o) {
        opts = o;
        var parsed = { cells: [], mobileTwoColumn: false };
        try {
            var p = JSON.parse(o.layoutJson);
            if (p && Array.isArray(p.cells)) parsed = p;
        } catch (e) { /* corrupted layout — render empty rather than fail the page */ }

        cells = parsed.cells;
        mobileTwoColumn = !!parsed.mobileTwoColumn;

        cameraById = {};
        (o.cameras || []).forEach(function (c) { cameraById[c.id] = c; });

        // What Pages/Live redirects to on its next visit. Not recorded for a tour hop — a tour
        // rotates through views on its own timer, so whichever one it happened to be sitting on when
        // the tab closed isn't a deliberate choice worth restoring later.
        if (o.currentViewId && !o.isTour) {
            try { localStorage.setItem('larisvms.lastViewId', o.currentViewId); } catch (e) { /* private mode */ }
        }

        render();

        var onModeChange = function () { render(); };
        if (phoneQuery.addEventListener) phoneQuery.addEventListener('change', onModeChange);
        else if (phoneQuery.addListener) phoneQuery.addListener(onModeChange); // Safari < 14

        wireKiosk(o.kioskBtnId, o.navElId, o.toolbarElId);
        if (o.isTour) wireTour(o.tourViewIds, o.tourIndex, o.tourIntervalSeconds);
    }

    return { init: init };
})();

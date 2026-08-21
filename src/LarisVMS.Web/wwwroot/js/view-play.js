// M6 read-only view display (Pages/Views/Play). Desktop renders the saved GridStack geometry with
// plain CSS grid (no GridStack JS needed just to display a fixed layout); phones get a derived
// single/two-column stack — ordered top-to-bottom/left-to-right from the desktop positions, sized
// from each cell's aspect ratio, cameras with hideOnPhone skipped — never persisted, same "a view
// built on desktop gets a usable phone view for free" idea as frcastr's dashboard.
window.larisvmsViewPlay = (function () {
    'use strict';

    var CELL_HEIGHT = 60; // matches the editor's GridStack cellHeight, for a consistent look
    var phoneQuery = window.matchMedia('(max-width: 767.98px)');
    // A phone held sideways is wider than the phone breakpoint but only ~400px tall, so it used to
    // fall through to the desktop grid — 12 columns squeezed into ~840px (≈70px each) while rows
    // stayed pinned at CELL_HEIGHT. Column width tracks the viewport; row height didn't, so every
    // cell came out far taller and narrower than the box the layout was designed in, and
    // object-fit:contain letterboxed the video into a thin band with black above and below. That's
    // the "squished vertically to almost nothing" report. Anything this short instead scales its
    // rows to fit the whole view on screen at once (see fittedRowHeight), which is what you want on
    // a genuinely short *desktop* window regardless of how the layout was built.
    var shortQuery = window.matchMedia('(max-height: 600px)');
    // A phone's own landscape width (up to ~930px on the largest current phones) exceeds phoneQuery's
    // portrait breakpoint, so a landscape phone used to fall into the plain shortQuery branch above —
    // the *desktop* grid, just fitted-to-height. That's a real fix for the squishing bug, but it also
    // meant a phone rotated to landscape lost its 1/2-column phone stack entirely and went back to
    // however many columns the desktop layout happened to use, confirmed live as "landscape doesn't
    // keep 1 or 2 columns". 950px comfortably covers real phone landscape widths while staying below
    // a tablet's (iPad landscape starts at 1024px) — narrow enough that this can't misfire for an
    // ordinary short desktop window, which is what plain shortQuery below still exists to handle.
    var phoneLandscapeQuery = window.matchMedia('(max-width: 950px) and (max-height: 600px)');
    var GRID_GAP = 6;
    var MIN_ROW_HEIGHT = 12; // a floor, so a pathological layout can't collapse cells to zero
    // Ceiling on the derived landscape column count (see landscapeColumns). Four cameras across a
    // ~850px-wide phone is already ~210px each — past that they're too small to be worth watching,
    // so a view with many cameras scrolls rather than shrinking them further.
    var MAX_LANDSCAPE_COLUMNS = 4;

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

    // Positioning classes for the motion/detection badges, from the admin's chosen corner
    // (Events.BadgeCorner — allowlisted server-side, so an unknown value here can only mean an older
    // page and falls back to top-left rather than emitting nothing).
    //
    // The bottom corners are already occupied: bottom-right holds every tile's hover controls, and
    // bottom-left holds a playback-mode cell's mini timeline. Badges placed there get lifted clear
    // of that row instead of landing on top of it — mb-5 (3rem) clears both the ~1.75rem control
    // buttons and the 30px timeline canvas with room to spare. Top corners need no such offset.
    function badgePositionClasses() {
        switch (opts && opts.badgeCorner) {
            case 'TopRight': return 'top-0 end-0 m-1';
            case 'BottomRight': return 'bottom-0 end-0 ms-1 me-1 mb-5';
            case 'BottomLeft': return 'bottom-0 start-0 ms-1 me-1 mb-5';
            default: return 'top-0 start-0 m-1';
        }
    }

    function buildCellHtml(cell) {
        var cam = cameraById[cell.cameraId];
        var name = cam ? cam.name : '';
        var badgePos = badgePositionClasses();
        return (
            '<div class="h-100 d-flex flex-column border rounded overflow-hidden" data-camera-tile="' + cell.cameraId + '">' +
                '<div class="view-cell-frame position-relative flex-grow-1 bg-black" style="min-height: 0;">' +
                    '<video class="view-cell-video" style="width:100%; height:100%; object-fit:contain;" muted playsinline></video>' +
                    '<div class="position-absolute top-50 start-50 translate-middle text-white small text-center px-2 view-cell-status" role="status" aria-live="polite"></div>' +
                    '<span class="badge bg-danger position-absolute ' + badgePos + ' live-motion-badge d-none"' +
                        ' title="Motion detected — movement with no object class attached">🌀 Motion</span>' +
                    // Filled in by live-view.js's poller from the camera's own object analytics.
                    // Sits under the motion badge's corner rather than beside it, since the two are
                    // mutually exclusive — a classified badge replaces the generic one.
                    //
                    // Wraps, and is width-capped to the cell: a camera can legitimately see several
                    // classes at once (a person walking a dog past a car is three), and every one of
                    // them gets its own badge. Without the cap they'd run off the edge of a small
                    // cell in a dense grid rather than stacking onto a second line.
                    '<div class="position-absolute ' + badgePos + ' d-flex flex-wrap gap-1 live-detection-badges"' +
                        ' style="max-width: calc(100% - .5rem);"></div>' +
                    // Only shown once this one cell has been toggled into playback mode (see the
                    // view-cell-playback button below) — every other cell keeps showing pure live
                    // video with no timeline at all. The right clearance keeps it clear of the
                    // controls in the opposite corner, which share this bottom edge and are drawn
                    // over the timeline whenever the cell is hovered. It depends on whether this
                    // camera has audio: with a volume slider the controls are roughly twice as wide,
                    // and a fixed clearance would either overlap them or waste timeline width on
                    // every silent camera.
                    '<div class="position-absolute bottom-0 start-0 d-none d-flex align-items-end gap-1 p-1 view-cell-mini-timeline-area"' +
                        ' style="right:' + (cam && cam.hasAudio ? 176 : 70) + 'px;">' +
                        '<button type="button" class="btn btn-outline-light btn-sm view-cell-mini-playpause" style="padding:.1rem .35rem;" title="Play/Pause" aria-label="Play/Pause">▶</button>' +
                        // 30px, matching the Playback page's own timeline canvases — not smaller:
                        // timeline.js draws its coverage bar across the top ~55% and the tick marks
                        // plus their 9px labels *below* that, so anything much shorter clips the
                        // labels (and part of the bar) off the bottom of the canvas entirely.
                        '<canvas class="view-cell-mini-timeline flex-grow-1" style="height:30px; touch-action:none; cursor:pointer; display:block;"></canvas>' +
                    '</div>' +
                    // Shown on hover — not native <video controls>: that includes
                    // click-anywhere-on-the-video-to-pause in most browsers, which makes no sense for
                    // a continuous live feed and was confirmed live as unwanted.
                    //
                    // A flex row rather than one btn-group, since the audio group leads with a volume
                    // slider (audio-controls.js) that isn't a button and shouldn't inherit
                    // btn-group's own joined-corners styling. The remaining buttons keep their group.
                    '<div class="position-absolute bottom-0 end-0 m-1 d-flex align-items-center gap-1 view-cell-controls d-none">' +
                        window.larisvmsAudioControls.html(cam && cam.hasAudio) +
                        '<div class="btn-group btn-group-sm">' +
                            (cam && cam.hasPtz
                                ? '<button type="button" class="btn btn-outline-light view-cell-ptz-toggle" style="padding:.1rem .35rem;" title="PTZ controls" aria-label="PTZ controls">🕹️</button>'
                                : '') +
                            // M18: only offered when both the deployment-wide toggle is on and this
                            // particular camera has a Sub stream to switch to — a camera without one
                            // is always Main regardless, same as the PTZ button's own cam.hasPtz gate
                            // just above. Starts as "Auto" (desiredRole decides); clicking cycles
                            // Auto -> HD (forces Main) -> SD (forces Sub) -> Auto. Label, not an icon:
                            // this is a three-state control, and a glyph set that reads unambiguously
                            // as "currently auto / forced high / forced low" at a glance doesn't exist
                            // the way ⏱/⛶'s do for a two-state toggle.
                            (opts.adaptiveStreamingEnabled && cam && cam.hasSub
                                ? '<button type="button" class="btn btn-outline-light view-cell-quality" style="padding:.1rem .35rem; font-size:.7rem;" title="Stream quality: Auto" aria-label="Stream quality">Auto</button>'
                                : '') +
                            '<button type="button" class="btn btn-outline-light view-cell-playback" style="padding:.1rem .35rem;" title="Playback" aria-label="Playback">⏱</button>' +
                            '<button type="button" class="btn btn-outline-light view-cell-fullscreen" style="padding:.1rem .35rem;" title="Fullscreen" aria-label="Fullscreen">⛶</button>' +
                        '</div>' +
                    '</div>' +
                    // Top-center, deliberately clear of every corner (badges) and both bottom edges
                    // (mini-timeline at bottom-start, hover controls at bottom-end) — toggled by the
                    // 🕹️ button above rather than shown on hover, since it's a deliberate action
                    // (issuing real camera movement), not a passive status readout.
                    '<div class="position-absolute top-0 start-50 translate-middle-x mt-1 view-cell-ptz-area">' +
                        window.larisvmsPtzControls.html(cam && cam.hasPtz) +
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

    // M18: how many cameras are actually on screen right now — part of the adaptive-streaming size
    // signal below (desiredRole), since a tile's *stored* w/h can understate how small it actually
    // renders on a phone stack (renderMobile keeps each cell's original w/h even though the derived
    // layout no longer uses them for sizing the way the desktop grid does).
    function visibleCellCount() {
        return cells.filter(function (c) { return cameraById[c.cameraId]; }).length;
    }

    // M18: which live source a tile should ask for absent any manual override or fullscreen — a
    // small tile has nothing to gain from Main's extra resolution, so Sub costs the node/network
    // less for the same perceived picture. Deliberately driven by the *stored* GridStack geometry
    // (columns/rows out of the 12-wide grid) rather than measuring actual rendered pixels: an
    // element's real box isn't known until after layout, which would mean either a ResizeObserver
    // per tile or a live-restart on every resize — both rejected in favor of reusing geometry this
    // page already has up front, matching the existing "only a mode change rebuilds tiles" policy
    // (see the resize listener's own comment in init()). visibleCellCount() is folded in as a second
    // signal specifically for the phone stack, whose cells keep their desktop-authored w/h even
    // though they're laid out completely differently there.
    function desiredRole(cell, cam) {
        if (!opts.adaptiveStreamingEnabled || !cam || !cam.hasSub) return 'main';
        if (visibleCellCount() <= 1) return 'main'; // nothing else on screen to save bandwidth for
        if (cell.w <= 4 || cell.h <= 3) return 'sub';
        if (visibleCellCount() >= 6) return 'sub';
        return 'main';
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
        var fullscreenBtn = el.querySelector('.view-cell-fullscreen');
        var playbackToggleBtn = el.querySelector('.view-cell-playback');
        var miniTimelineArea = el.querySelector('.view-cell-mini-timeline-area');
        var miniTimelineCanvas = el.querySelector('.view-cell-mini-timeline');
        var miniPlayPauseBtn = el.querySelector('.view-cell-mini-playpause');
        var ptzToggleBtn = el.querySelector('.view-cell-ptz-toggle');
        var ptzArea = el.querySelector('.view-cell-ptz-area');
        var qualityBtn = el.querySelector('.view-cell-quality');

        var cameraId = cell.cameraId;
        var codec = cam.codec;
        var hasAudio = cam.hasAudio;

        var liveStop = null;
        var pbPlayer = null;
        var pbTimeline = null;
        var pbPlaying = false;
        var inPlaybackMode = false;
        // M18: null = Auto (desiredRole decides); 'main'/'sub' = pinned by the quality button below.
        // Per-cell, not per-camera — the same camera in two cells of a view can be pinned differently.
        var qualityOverride = null;

        // Fullscreen always forces Main regardless of Auto/override — the one case where showing Sub
        // would be actively wrong (a viewer who just asked to see this camera as large as possible is
        // exactly who benefits from Main's extra resolution), so it overrides even a manual "SD" pin
        // rather than deferring to it.
        function effectiveRole() {
            if (fsHandle && fsHandle.isFullscreen()) return 'main';
            if (qualityOverride) return qualityOverride;
            return desiredRole(cell, cam);
        }

        if (hoverTarget && controls) {
            hoverTarget.addEventListener('mouseenter', function () { controls.classList.remove('d-none'); });
            hoverTarget.addEventListener('mouseleave', function () { controls.classList.add('d-none'); });
        }
        // Mute toggle + volume slider, and the reapply-on-load handling that keeps an unmuted cell
        // unmuted across a live reconnect or a switch into playback mode — both of which reload this
        // same <video> element, which otherwise resets it to the `muted` attribute's default. Wired
        // once here, not per mode, so it spans every live/playback transition this cell makes.
        window.larisvmsAudioControls.wire(controls, video);
        // Shared with Live/Playback (fullscreen-tile.js) — fullscreens frameEl (video + controls),
        // not the bare <video>, and wires the double-click gesture, same as those two pages. This
        // page's own fullscreenBtn just drives the same handle rather than calling
        // video.requestFullscreen() directly, which used to drop the mute/fullscreen buttons out of
        // the fullscreened render subtree entirely.
        var fsHandle = frameEl && window.larisvmsFullscreenTile
            ? window.larisvmsFullscreenTile.wire(frameEl, video, {
                onFullscreenChange: function (active) {
                    if (fullscreenBtn) {
                        fullscreenBtn.textContent = active ? '⤢' : '⛶';
                        fullscreenBtn.title = active ? 'Exit fullscreen' : 'Fullscreen';
                    }
                    // M18: entering/exiting fullscreen can change effectiveRole() (see its own doc
                    // comment on why fullscreen always wins) — restart live with the new role. Only
                    // when actually live: a cell toggled into playback mode has no liveStop to swap,
                    // and exitPlaybackModeIfActive already calls startLive() fresh on its way back.
                    if (!inPlaybackMode && liveStop) { liveStop(); startLive(); }
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
        // PTZ (M18) — wired once regardless of whether the pad is currently shown; wire() itself is a
        // no-op for a non-PTZ camera (html() rendered nothing for ptzArea to contain).
        window.larisvmsPtzControls.wire(ptzArea, cameraId);
        if (ptzToggleBtn && ptzArea) {
            ptzToggleBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                ptzArea.querySelector('.ptz-pad').classList.toggle('d-none');
            });
        }
        // M18: Auto -> HD (pin Main) -> SD (pin Sub) -> Auto. A pin set while fullscreen still takes
        // effect once fullscreen is exited (effectiveRole() only overrides *while* fullscreen is
        // active, it doesn't clear qualityOverride) — the button's own label always reflects the pin
        // itself, not whatever fullscreen is temporarily forcing on top of it.
        if (qualityBtn) {
            qualityBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                qualityOverride = qualityOverride === null ? 'main' : (qualityOverride === 'main' ? 'sub' : null);
                var label = qualityOverride === 'main' ? 'HD' : (qualityOverride === 'sub' ? 'SD' : 'Auto');
                qualityBtn.textContent = label;
                qualityBtn.title = 'Stream quality: ' + label;
                if (!inPlaybackMode && liveStop) { liveStop(); startLive(); }
            });
        }

        function startLive() {
            // Codec/audio must follow the role, not the camera: Sub is a separately encoded stream
            // and routinely differs from Main (H.264 vs HEVC, and usually no audio track at all).
            // MSE treats a SourceBuffer whose declared codec or track list doesn't match the arriving
            // init segment as a hard failure, not a downgrade — see the Sub fields' own comment in
            // Play.cshtml for the confirmed symptom this caused.
            var role = effectiveRole();
            var roleCodec = role === 'sub' ? cam.subCodec : codec;
            var roleHasAudio = role === 'sub' ? cam.subHasAudio : hasAudio;
            // Retained per-cell specifically so entering playback mode can end this session cleanly
            // instead of leaving it running underneath the swapped-in player.
            liveStop = window.larisvmsLiveView.start(cameraId, video, status, roleCodec, roleHasAudio, role);
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
                    // Same hover-preview behavior as the Playback page's per-camera timeline —
                    // timeline.js only registers it when this option is present, which is why the
                    // mini timeline had none. Simpler than Playback's version: a View cell's
                    // timeline is bound to one camera for its whole life, so there's no primary
                    // selection to read at hover time.
                    getThumbnailUrl: function (atMs) {
                        return '/playback-thumbnail/' + cameraId + '?atUtc=' + encodeURIComponent(new Date(atMs).toISOString());
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

    // Row height that makes rowCount equal-height rows fill exactly the available vertical space.
    // Measured from the grid's own live position rather than a guessed toolbar height, so it stays
    // correct in kiosk mode (nav + toolbar hidden) and on any device chrome. Returns null when
    // there's nothing to fit, leaving the caller on its own unfitted default.
    function fittedRowHeight(container, rowCount) {
        if (rowCount <= 0) return null;
        var available = window.innerHeight - container.getBoundingClientRect().top - GRID_GAP;
        var forGaps = GRID_GAP * (rowCount - 1);
        return Math.max(MIN_ROW_HEIGHT, (available - forGaps) / rowCount);
    }

    // How many row-units the replayed desktop layout occupies — the tallest row-span among its
    // cells, since a cell can span more than one grid row (renderDesktop's own gridRow spans).
    function desktopRowCount(visible) {
        return visible.reduce(function (max, c) { return Math.max(max, c.y + c.h); }, 0);
    }

    // Re-fits without re-rendering. A full render() tears down and restarts every camera's MSE
    // session, which is far too heavy for a resize or a fullscreen toggle — only the container's
    // row height actually needs to change. Desktop-grid fitting only: the derived phone stack
    // (renderMobile, portrait or landscape) never uses a fitted height at all — see renderMobile's
    // own comment for why forcing one there was a real regression, not a fix.
    function applyFittedRowHeight() {
        if (!shortQuery.matches) return;
        var container = document.getElementById(opts.gridElId);
        if (!container) return;
        var visible = cells.filter(function (c) { return cameraById[c.cameraId]; });
        var rowHeight = fittedRowHeight(container, desktopRowCount(visible));
        if (rowHeight !== null) container.style.gridAutoRows = rowHeight + 'px';
    }

    function renderDesktop(container, fitToHeight) {
        var visible = cells.filter(function (c) { return cameraById[c.cameraId]; });
        container.style.display = 'grid';
        container.style.gridTemplateColumns = 'repeat(12, 1fr)';
        container.style.gap = GRID_GAP + 'px';
        container.innerHTML = '';

        if (!visible.length) {
            container.style.gridAutoRows = CELL_HEIGHT + 'px';
            container.innerHTML = '<p class="text-muted">This view has no cameras yet.</p>';
            return;
        }

        var rowHeight = fitToHeight ? fittedRowHeight(container, desktopRowCount(visible)) : null;
        container.style.gridAutoRows = (rowHeight === null ? CELL_HEIGHT : rowHeight) + 'px';

        visible.forEach(function (cell) {
            var el = document.createElement('div');
            el.style.gridColumn = (cell.x + 1) + ' / span ' + cell.w;
            el.style.gridRow = (cell.y + 1) + ' / span ' + cell.h;
            el.innerHTML = buildCellHtml(cell);
            container.appendChild(el);
            startCellVideo(el, cell);
        });
    }

    // The camera's *real* aspect ratio wins over the cell's stored one here, because the phone stack
    // is a layout this app derives rather than replays — nothing about it is a user's saved choice,
    // so it should be shaped by ground truth. The stored aspect is only a fallback for a camera whose
    // resolution isn't known yet (never probed, or an older page not sending it).
    //
    // Confirmed live as "landscape on phone still squishing cameras, but not all of them": the editor
    // defaults every newly-added cell to 16:9 regardless of the camera (view-editor.js), so any camera
    // that isn't actually 16:9 had a cell shaped wrong for it, and object-fit:contain letterboxed the
    // video into a band inside that cell. Always the same cameras, because it follows the camera's own
    // resolution rather than anything about position or count. Desktop still replays the saved layout
    // untouched — a deliberate editor choice there is a real choice, unlike this derived stack.
    function mobileCellRatio(cell) {
        var cam = cameraById[cell.cameraId];
        return (cam && window.LarisVMSAspectRatio.nearest(cam.width, cam.height))
            || (window.LarisVMSAspectRatio.isValid(cell.aspect) ? cell.aspect : window.LarisVMSAspectRatio.default);
    }

    // Landscape needs its own column count, not the portrait one. `mobileTwoColumn` is a choice made
    // about a *portrait* phone — tall and narrow, where 1 (or optionally 2) columns is right. Reusing
    // it in landscape reads badly: a landscape phone is ~850x400, so a single column makes each cell
    // the full ~850px wide and therefore ~478px tall at 16:9 — taller than the entire viewport. Every
    // camera then fills more than the screen and you scroll through them one at a time, confirmed
    // live as "all of the cameras keep getting stacked on top of each other in landscape".
    //
    // Adding columns is specifically the safe lever here, and is not a repeat of the fitted-height
    // attempt that was reverted earlier (see renderMobile's own comment): more columns makes each cell
    // narrower and therefore proportionally shorter, with its aspect ratio fully intact — nothing is
    // squashed. Picks the fewest columns (largest cells) that still let a whole row fit the viewport
    // height, using the *tallest* cell among the visible cameras so none of them overflow.
    function landscapeColumns(container, visible) {
        var available = window.innerHeight - container.getBoundingClientRect().top - GRID_GAP;
        var width = container.clientWidth;
        if (available <= 0 || width <= 0) return mobileTwoColumn ? 2 : 1;

        // Smallest ratio = tallest cell for a given width; sizing to that keeps every other camera
        // comfortably within the height too.
        var tallest = visible.reduce(function (min, cell) {
            var r = window.LarisVMSAspectRatio.ratio(mobileCellRatio(cell));
            return Math.min(min, r);
        }, Infinity);
        if (!isFinite(tallest) || tallest <= 0) return mobileTwoColumn ? 2 : 1;

        for (var cols = 1; cols <= MAX_LANDSCAPE_COLUMNS; cols++) {
            var cellWidth = (width - GRID_GAP * (cols - 1)) / cols;
            if (cellWidth / tallest <= available) return cols;
        }
        return MAX_LANDSCAPE_COLUMNS;
    }

    function renderMobile(container, landscape) {
        var visible = cells.filter(function (c) { return !c.hideOnPhone && cameraById[c.cameraId]; });
        var cols = landscape && visible.length
            ? landscapeColumns(container, visible)
            : (mobileTwoColumn ? 2 : 1);
        var ordered = visible.slice().sort(function (a, b) { return (a.y - b.y) || (a.x - b.x); });
        var rows = desktopRows(ordered);

        container.style.display = 'grid';
        container.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
        container.style.gap = GRID_GAP + 'px';
        container.innerHTML = '';

        if (!visible.length) {
            container.style.gridAutoRows = '';
            container.innerHTML = '<p class="text-muted">No cameras in this view are visible on phones.</p>';
            return;
        }

        // Always sized from each cell's own aspect ratio, never fitted to a computed row height —
        // unlike the replayed desktop layout (whose cells have no intrinsic size of their own, only
        // a row-count that a fixed CELL_HEIGHT can get wrong for the viewport), every cell here
        // already renders at the right proportions regardless of viewport height. Fitting the stack's
        // *height* to a short viewport (like the desktop grid does) was tried and reverted: it
        // squashed every cell down to a sliver once a view held more than two or three cameras,
        // confirmed live as "vertically squished and tiny".
        //
        // Landscape's fix for the same underlying problem is columns, not heights (see
        // landscapeColumns): more columns shrinks cells proportionally, aspect ratio intact, which is
        // exactly what fitting a row height could never do. A view with more cameras than fit even at
        // MAX_LANDSCAPE_COLUMNS still scrolls to see the rest, same as portrait.
        container.style.gridAutoRows = '';

        function appendCell(cell, spanFullWidth) {
            var el = document.createElement('div');
            el.style.aspectRatio = mobileCellRatio(cell).replace(':', '/');
            if (spanFullWidth) el.style.gridColumn = '1 / -1';
            el.innerHTML = buildCellHtml(cell);
            container.appendChild(el);
            startCellVideo(el, cell);
        }

        if (landscape) {
            // Straight sequential flow across the computed columns, deliberately ignoring the desktop
            // row grouping below. That grouping (plus its "odd one out spans full width" rule) exists
            // to keep a portrait 1/2-column stack reading like the saved layout, but it actively works
            // against the point of landscape's extra columns: a desktop row holding a single camera
            // would claim an entire grid row on its own, and a lone trailing camera would stretch
            // across every column — both re-creating exactly the oversized cells this branch exists to
            // avoid. Ordering is still the same top-to-bottom/left-to-right reading of the saved
            // layout, so which camera comes first doesn't change, only how they pack.
            ordered.forEach(function (cell) { appendCell(cell, false); });
            return;
        }

        rows.forEach(function (row) {
            for (var i = 0; i < row.length; i += cols) {
                var pair = row.slice(i, i + cols);
                pair.forEach(function (cell) { appendCell(cell, pair.length < cols); });
            }
        });
    }

    // Four layouts, checked in this order:
    //   phone landscape → the same derived single/two-column phone stack as portrait, unfitted — a
    //                     phone rotated sideways keeps its 1/2-column shape and scrolls to see the
    //                     rest, rather than falling back to however many columns the desktop layout
    //                     happens to use (confirmed live: "landscape doesn't keep 1 or 2 columns" was
    //                     exactly this falling through to the branch below instead). See
    //                     phoneLandscapeQuery's own comment for why 950px, not phoneQuery's narrower
    //                     portrait breakpoint.
    //   short viewport  → the saved layout with rows scaled to fit the window (any other short
    //                     window — a resized desktop browser, a short non-phone display). Uses the
    //                     real layout, so `hideOnPhone` does not apply — that flag belongs to the
    //                     derived stack above/below, which is the only layout this app invents
    //                     rather than replays.
    //   narrow viewport → the derived single/two-column phone stack (phone in portrait), same
    //                     unfitted rendering as the landscape case above.
    //   otherwise       → the saved layout at the editor's own fixed row height.
    function render() {
        stopAll();
        var container = document.getElementById(opts.gridElId);
        if (!container) return;
        if (phoneLandscapeQuery.matches) renderMobile(container, true);
        else if (shortQuery.matches) renderDesktop(container, true);
        else if (phoneQuery.matches) renderMobile(container, false);
        else renderDesktop(container, false);
    }

    // Single-camera picker beside the View dropdown — explicit user ask: "list all cameras with a
    // quick filter at the top, select a single camera to view." A <select> can't hold a filter input,
    // so this is a <details> popover (same "small filterable list in a dropdown" shape
    // _ColumnPicker.cshtml/column-picker.js already establish elsewhere in this app) rather than a
    // new UI convention. Navigates via a full page load (?cameraId=X), same as the View picker's own
    // location.href — bookmarkable, server-authorized against the same CameraAccess-narrowed list
    // either way, and no separate client-only render path to keep in sync with the server one.
    function wireCameraPicker(o) {
        var details = o.cameraPickerDetailsId && document.getElementById(o.cameraPickerDetailsId);
        var filterInput = o.cameraPickerFilterId && document.getElementById(o.cameraPickerFilterId);
        var list = o.cameraPickerListId && document.getElementById(o.cameraPickerListId);
        if (!details || !filterInput || !list) return;

        var cameras = (o.cameras || []).slice().sort(function (a, b) {
            return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
        });

        function render(filterText) {
            var term = (filterText || '').trim().toLowerCase();
            list.innerHTML = '';
            cameras
                .filter(function (c) { return !term || c.name.toLowerCase().indexOf(term) !== -1; })
                .forEach(function (c) {
                    var a = document.createElement('a');
                    a.className = 'list-group-item list-group-item-action py-1 px-2 small';
                    a.href = '/Views/Play?cameraId=' + encodeURIComponent(c.id);
                    a.textContent = (c.id === o.singleCameraId ? '★ ' : '') + c.name;
                    list.appendChild(a);
                });
            if (!list.children.length) {
                var empty = document.createElement('div');
                empty.className = 'text-muted small px-2 py-1';
                empty.textContent = 'No cameras match.';
                list.appendChild(empty);
            }
        }
        render('');

        filterInput.addEventListener('input', function () { render(filterInput.value); });
        // Opened fresh every time, not left showing whatever was typed last visit — the summary
        // click that reveals the popover doesn't otherwise touch the input at all.
        details.addEventListener('toggle', function () {
            if (details.open) { filterInput.value = ''; render(''); filterInput.focus(); }
        });

        // Same "close on outside click" as column-picker.js's own <details> popover — a plain
        // <details> only closes its own <summary> click natively, not a click anywhere else on the
        // page, so without this the popover stays open indefinitely once opened.
        document.addEventListener('click', function (e) {
            if (details.open && !details.contains(e.target)) details.open = false;
        });
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
            // Hiding the nav and toolbar just handed the grid ~100px of height it didn't have a
            // moment ago (and takes it back on exit) — re-fit rather than leave the view sized for
            // the wrong window. Not a re-render: nothing about the tiles themselves changed.
            applyFittedRowHeight();
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
        cameraById = {};
        (o.cameras || []).forEach(function (c) { cameraById[c.id] = c; });

        // Single-camera picker (new dropdown beside the View picker, explicit user ask): one ad hoc
        // cell for this camera, filling the whole grid exactly as if it were a 1-camera View — reuses
        // every existing rendering path (renderDesktop/renderMobile, PTZ, audio, fullscreen, the
        // mobile aspect-ratio fix) with no new rendering code, the same "render it directly, no saved
        // layout involved" approach playback-player.js's own single-camera deep link uses. w:12/h:8 is
        // simply the editor's own placement math for a freshly-added camera at that width (see
        // view-editor.js's computeInitialHeight) rather than anything specific to this mode — on a
        // short viewport applyFittedRowHeight already rescales every row to fit regardless of h, and a
        // real 1-camera View gets exactly the same starting shape today.
        if (o.singleCameraId) {
            cells = [{ id: 'single-camera', x: 0, y: 0, w: 12, h: 8, cameraId: o.singleCameraId, hideOnPhone: false }];
            mobileTwoColumn = false;
        } else {
            var parsed = { cells: [], mobileTwoColumn: false };
            try {
                var p = JSON.parse(o.layoutJson);
                if (p && Array.isArray(p.cells)) parsed = p;
            } catch (e) { /* corrupted layout — render empty rather than fail the page */ }

            cells = parsed.cells;
            mobileTwoColumn = !!parsed.mobileTwoColumn;
        }

        // What Pages/Live redirects to on its next visit — read server-side now (Live/Index.cshtml.cs
        // OnGetAsync), not client-side, so the choice follows the user to another browser/device
        // instead of resetting there. Not recorded for a tour hop — a tour rotates through views on
        // its own timer, so whichever one it happened to be sitting on when the tab closed isn't a
        // deliberate choice worth restoring later. Not recorded for single-camera mode either — an ad
        // hoc camera pick is a one-off destination, not a standing choice the way selecting a View
        // from the dropdown is (same reasoning Playback's own deep link uses for not persisting).
        if (o.currentViewId && !o.isTour && !o.singleCameraId) {
            window.larisvmsPreferences.set('lastViewId', o.currentViewId);
        }

        render();

        // A full re-render only when the layout *mode* actually changes — crossing either breakpoint
        // means a different layout, which does need the tiles rebuilt.
        var onModeChange = function () { render(); };
        [phoneQuery, shortQuery, phoneLandscapeQuery].forEach(function (query) {
            if (query.addEventListener) query.addEventListener('change', onModeChange);
            else if (query.addListener) query.addListener(onModeChange); // Safari < 14
        });

        // Ordinary resizes inside the same mode only need the row height recomputed. Debounced and
        // deliberately not a re-render: rebuilding tiles would drop and restart every camera's MSE
        // session, and iOS fires resize repeatedly through an orientation change and again as the
        // URL bar collapses.
        var resizeTimer = null;
        window.addEventListener('resize', function () {
            if (resizeTimer) clearTimeout(resizeTimer);
            resizeTimer = setTimeout(applyFittedRowHeight, 150);
        });

        wireKiosk(o.kioskBtnId, o.navElId, o.toolbarElId);
        wireCameraPicker(o);
        if (o.isTour) wireTour(o.tourViewIds, o.tourIndex, o.tourIntervalSeconds);
    }

    return { init: init };
})();

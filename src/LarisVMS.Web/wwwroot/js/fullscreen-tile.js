// Shared tile behavior for a single camera video, used by both Live/Views and Playback:
//   * double-click toggles real browser Fullscreen on the tile's own container element (not the
//     bare <video>), so sibling overlay controls stay in the fullscreened render subtree;
//   * digital zoom — the ONE zoom state for the tile. Drag a box to zoom into it; once zoomed, drag
//     pans; back at 1x (Out/Reset, wheel or pinch), drag selects again. Wheel and pinch zoom while
//     fullscreen. The page's own zoom buttons (Playback's In/Out/Reset) drive it through the returned
//     handle rather than keeping a second state.
//
// Playback used to run its own drag-select zoom (wireZoom) beside this file's fullscreen zoom, both
// writing the same <video> transform from separate state. Leaving fullscreen cleared the transform
// while wireZoom still believed it was zoomed, so the next drag panned an unzoomed frame instead of
// selecting — and the two handlers stole pointer capture from each other. One state fixes both.
//
// Because this uses the real Fullscreen API rather than an in-page "big tile" CSS toggle, every other
// tile on the page keeps decoding/playing off-screen the whole time — nothing is torn down or hidden.
window.larisvmsFullscreenTile = (function () {
    'use strict';

    // Shared across every tile using this module — only one element can be entering/exiting
    // fullscreen at a time, so this doesn't need to be per-tile. Set synchronously the instant a
    // fullscreen request/exit is *issued*, not once it completes: v0.148.1 gated view-play.js's
    // matchMedia mode-change listener on document.fullscreenElement alone, but that still raced and
    // still tore every camera down on live testing — a mobile browser's viewport resize (collapsing
    // address-bar chrome) that requestFullscreen() triggers isn't guaranteed to fire after
    // 'fullscreenchange' does, so matchMedia's own 'change' event can still land while
    // document.fullscreenElement is still null. This flag covers the whole request from the moment
    // it's issued, closing that race instead of racing it.
    var transitioning = false;

    // A drag smaller than this reads as a plain click (Playback's select-primary click still fires
    // afterward — preventDefault on pointerdown doesn't suppress 'click'), not "zoom to this region".
    var MIN_SELECT_PX = 24;

    // Pointer gestures starting on one of the tile's own controls are left to that control.
    var INTERACTIVE = 'button, a, input, select, textarea, label, [role="button"], .dropdown-menu, .pb-fullscreen-controls';

    function wire(containerEl, videoEl, opts) {
        opts = opts || {};
        var minScale = 1;
        var maxScale = opts.maxScale || 4;
        var scale = 1, panX = 0, panY = 0;
        var transformCss = '';

        function isFs() { return document.fullscreenElement === containerEl; }

        // Keeps the zoomed frame covering the whole tile: the transform is scale(s) translate(p)
        // around the element's centre, so a point d from centre lands at s*(d+p); the frame's edges
        // stay outside the viewport while |p| <= (size/2)(1 - 1/s).
        function clampPan() {
            var limitX = (videoEl.clientWidth / 2) * (1 - 1 / scale);
            var limitY = (videoEl.clientHeight / 2) * (1 - 1 / scale);
            panX = Math.max(-limitX, Math.min(limitX, panX));
            panY = Math.max(-limitY, Math.min(limitY, panY));
        }

        // Shown only while zoomed: a drag-to-zoom on a tile with no zoom buttons of its own (every
        // live/View tile, and any tile in fullscreen) otherwise left no visible way back out. Lifted
        // clear of the hover controls that share the bottom-right corner (same mb-5 clearance the
        // corner badges use). Right-click while zoomed does the same.
        var resetBtn = document.createElement('button');
        resetBtn.type = 'button';
        resetBtn.className = 'btn btn-sm btn-dark position-absolute bottom-0 end-0 me-1 mb-5 tile-zoom-reset';
        resetBtn.style.cssText = 'z-index:6; display:none; opacity:.85; padding:.1rem .45rem;';
        resetBtn.title = 'Reset zoom (or right-click the video)';
        resetBtn.textContent = '⤺ 1×';
        resetBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            resetZoom();
        });
        containerEl.appendChild(resetBtn);

        function apply() {
            if (scale <= minScale) { scale = minScale; panX = 0; panY = 0; }
            clampPan();
            transformCss = scale === minScale ? '' : 'scale(' + scale + ') translate(' + panX + 'px, ' + panY + 'px)';
            videoEl.style.transform = transformCss;
            videoEl.style.cursor = scale > minScale ? 'grab' : '';
            resetBtn.style.display = scale > minScale ? '' : 'none';
            if (opts.onZoomChange) opts.onZoomChange(scale);
        }

        function setScale(next) {
            scale = Math.min(maxScale, Math.max(minScale, next));
            apply();
        }

        function resetZoom() {
            endGesture(false);
            activePointers = {};
            scale = minScale;
            apply();
        }

        // Right-click while zoomed goes straight back to 1x; at 1x the browser's own menu is left alone.
        containerEl.addEventListener('contextmenu', function (e) {
            if (scale <= minScale || (e.target.closest && e.target.closest(INTERACTIVE))) return;
            e.preventDefault();
            resetZoom();
        });

        containerEl.addEventListener('dblclick', function (e) {
            if (e.target.closest && e.target.closest(INTERACTIVE)) return;
            e.stopPropagation();
            transitioning = true;
            if (isFs()) {
                document.exitFullscreen().catch(function () { transitioning = false; /* ignore */ });
            } else {
                // requestFullscreen()'s own rejection (e.g. iframe policy) never fires
                // 'fullscreenchange', so this catch is the only place that clears the flag on that
                // path — without it a denied request would leave transitioning stuck true forever.
                containerEl.requestFullscreen().catch(function () { transitioning = false; /* ignore */ });
            }
        });

        document.addEventListener('fullscreenchange', function () {
            transitioning = false;
            var active = isFs();
            containerEl.classList.toggle('tile-fullscreen', active);
            // Leaving fullscreen returns to the grid at 1x — the same single state, so the next drag
            // in the grid selects rather than panning a zoom the user can no longer see.
            if (!active) resetZoom();
            if (opts.onFullscreenChange) opts.onFullscreenChange(active);
        });

        // Wheel up = zoom in, down = zoom out — only while this tile is fullscreen, so scrolling the
        // page itself is never hijacked in the grid.
        containerEl.addEventListener('wheel', function (e) {
            if (!isFs()) return;
            e.preventDefault();
            setScale(scale * (e.deltaY < 0 ? 1.25 : 0.8));
        }, { passive: false });

        // Pointer events + setPointerCapture (on the container only — one capture owner per gesture)
        // rather than mouse events on window: a lost mouseup (released outside the window, or
        // swallowed by the fullscreen transition when Esc is hit mid-drag) used to leave a drag stuck
        // on forever, after which every mousemove anywhere kept panning this video. Capture guarantees
        // the matching up/cancel comes back here, and lostpointercapture ends the gesture regardless.
        var gesture = null; // null | { kind: 'pan'|'select', pointerId, startX, startY, startPanX, startPanY }
        var selRectEl = null;

        // ── Pinch-to-zoom (M16) ──────────────────────────────────────────────
        // Every currently-down pointer's last known position, keyed by pointerId — two entries means
        // a pinch is in progress. A touchscreen delivers each finger as its own Pointer Events stream,
        // so pinch is "the same events, tracked for up to two pointers at once".
        var activePointers = {};
        var pinchStartDist = null, pinchStartScale = 1, pinchStartMidX = 0, pinchStartMidY = 0;
        var pinchStartPanX = 0, pinchStartPanY = 0;

        function dist(a, b) { return Math.hypot(a.x - b.x, a.y - b.y); }
        function midpoint(a, b) { return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }; }

        function ensureSelRectEl() {
            if (selRectEl) return selRectEl;
            selRectEl = document.createElement('div');
            selRectEl.style.cssText = 'position:absolute; border:1px dashed #fff; ' +
                'background:rgba(255,255,255,.15); pointer-events:none; display:none; z-index:5;';
            videoEl.parentElement.appendChild(selRectEl);
            return selRectEl;
        }

        // The selection box is positioned in the video's parent's coordinates (it's appended there),
        // and the zoom math below works in the video element's own box — both from client coords.
        function localTo(el, e) {
            var r = el.getBoundingClientRect();
            return { x: e.clientX - r.left, y: e.clientY - r.top };
        }

        // commit=false (pointercancel, lost capture, Esc) discards a selection instead of zooming to
        // wherever it happened to be when interrupted.
        function endGesture(commit) {
            if (!gesture) return;
            var g = gesture;
            gesture = null;
            if (containerEl.hasPointerCapture && containerEl.hasPointerCapture(g.pointerId)) {
                try { containerEl.releasePointerCapture(g.pointerId); } catch (_) { /* already released */ }
            }
            if (g.kind === 'select') {
                var el = ensureSelRectEl();
                el.style.display = 'none';
                var w = Math.abs(g.curX - g.startX), h = Math.abs(g.curY - g.startY);
                if (commit && w >= MIN_SELECT_PX && h >= MIN_SELECT_PX) {
                    // Zoom so the selection fills the frame, centred on it: the transform maps a
                    // point d from the frame's centre to scale*(d+pan), so pan = -(selection centre's
                    // offset from the frame centre) lands it in the middle at any zoom.
                    var vw = videoEl.clientWidth, vh = videoEl.clientHeight;
                    var cx = (g.startX + g.curX) / 2, cy = (g.startY + g.curY) / 2;
                    scale = Math.min(maxScale, Math.max(minScale, Math.min(vw / w, vh / h)));
                    panX = vw / 2 - cx;
                    panY = vh / 2 - cy;
                    apply();
                }
            }
            videoEl.style.cursor = scale > minScale ? 'grab' : '';
        }

        containerEl.addEventListener('pointerdown', function (e) {
            if (e.target.closest && e.target.closest(INTERACTIVE)) return;
            var fs = isFs();

            if (fs) {
                activePointers[e.pointerId] = { x: e.clientX, y: e.clientY };
                var ids = Object.keys(activePointers);
                if (ids.length === 2) {
                    // A second finger landed — hand off from any single-pointer gesture to a pinch
                    // anchored on both fingers' current midpoint/spacing.
                    endGesture(false);
                    e.preventDefault();
                    containerEl.setPointerCapture(e.pointerId);
                    var p1 = activePointers[ids[0]], p2 = activePointers[ids[1]];
                    pinchStartDist = dist(p1, p2);
                    pinchStartScale = scale;
                    var mid = midpoint(p1, p2);
                    pinchStartMidX = mid.x; pinchStartMidY = mid.y;
                    pinchStartPanX = panX; pinchStartPanY = panY;
                    return;
                }
                if (ids.length > 2) return; // a third finger: ignored, the first two keep driving the pinch
            }

            if (!e.isPrimary || e.button !== 0 || gesture) return;
            // Outside fullscreen a touch drag must keep scrolling the page, so touch only zooms/pans
            // while fullscreen (where .tile-fullscreen sets touch-action:none).
            if (!fs && e.pointerType === 'touch') return;

            // <video> is a native drag source in Chrome/Edge (you can drag a frame out like an image);
            // without this a drag here can start that native drag instead, which then owns the mouse
            // and shows the no-drop cursor rather than selecting/panning.
            e.preventDefault();
            containerEl.setPointerCapture(e.pointerId);

            if (scale > minScale) {
                gesture = { kind: 'pan', pointerId: e.pointerId, startX: e.clientX, startY: e.clientY, startPanX: panX, startPanY: panY };
                videoEl.style.cursor = 'grabbing';
            } else {
                var p = localTo(videoEl, e);
                gesture = { kind: 'select', pointerId: e.pointerId, startX: p.x, startY: p.y, curX: p.x, curY: p.y };
                var parentP = localTo(videoEl.parentElement, e);
                gesture.parentStartX = parentP.x; gesture.parentStartY = parentP.y;
                var el = ensureSelRectEl();
                el.style.left = parentP.x + 'px'; el.style.top = parentP.y + 'px';
                el.style.width = '0px'; el.style.height = '0px';
                el.style.display = '';
                videoEl.style.cursor = 'crosshair';
            }
        });

        containerEl.addEventListener('pointermove', function (e) {
            if (activePointers[e.pointerId]) activePointers[e.pointerId] = { x: e.clientX, y: e.clientY };

            var ids = Object.keys(activePointers);
            if (ids.length === 2 && pinchStartDist) {
                e.preventDefault();
                var p1 = activePointers[ids[0]], p2 = activePointers[ids[1]];
                var newDist = dist(p1, p2);
                // Guards against a division blip if both pointers briefly report the exact same
                // point (seen on some touch digitizers for one frame at gesture start).
                if (newDist > 0 && pinchStartDist > 0) {
                    scale = Math.min(maxScale, Math.max(minScale, pinchStartScale * (newDist / pinchStartDist)));
                }
                var mid = midpoint(p1, p2);
                panX = pinchStartPanX + (mid.x - pinchStartMidX) / scale;
                panY = pinchStartPanY + (mid.y - pinchStartMidY) / scale;
                apply();
                return;
            }

            if (!gesture || e.pointerId !== gesture.pointerId) return;
            if (gesture.kind === 'pan') {
                panX = gesture.startPanX + (e.clientX - gesture.startX) / scale;
                panY = gesture.startPanY + (e.clientY - gesture.startY) / scale;
                apply();
                videoEl.style.cursor = 'grabbing';
            } else {
                var p = localTo(videoEl, e);
                gesture.curX = Math.max(0, Math.min(videoEl.clientWidth, p.x));
                gesture.curY = Math.max(0, Math.min(videoEl.clientHeight, p.y));
                var parentP = localTo(videoEl.parentElement, e);
                var el = ensureSelRectEl();
                el.style.left = Math.min(gesture.parentStartX, parentP.x) + 'px';
                el.style.top = Math.min(gesture.parentStartY, parentP.y) + 'px';
                el.style.width = Math.abs(parentP.x - gesture.parentStartX) + 'px';
                el.style.height = Math.abs(parentP.y - gesture.parentStartY) + 'px';
            }
        });

        function onPointerEnd(e, commit) {
            delete activePointers[e.pointerId];
            if (Object.keys(activePointers).length < 2) pinchStartDist = null;
            if (gesture && e.pointerId === gesture.pointerId) endGesture(commit);
        }
        containerEl.addEventListener('pointerup', function (e) { onPointerEnd(e, true); });
        containerEl.addEventListener('pointercancel', function (e) { onPointerEnd(e, false); });
        containerEl.addEventListener('lostpointercapture', function (e) { onPointerEnd(e, false); });

        return {
            isFullscreen: isFs,
            exitFullscreen: function () { if (isFs()) document.exitFullscreen().catch(function () { /* ignore */ }); },
            zoomIn: function () { setScale(scale + 0.5); },
            zoomOut: function () { setScale(scale - 0.5); },
            resetZoom: resetZoom,
            getScale: function () { return scale; },
            // The exact CSS transform string currently applied to videoEl (empty at 1x) — a caller
            // drawing an overlay meant to track the video's own content (the AI-detection bounding-box
            // canvas in live-view.js) applies this same string to its own element rather than
            // reimplementing the scale/translate math, so the two can never drift out of sync.
            getTransformCss: function () { return transformCss; }
        };
    }

    return { wire: wire, isTransitioning: function () { return transitioning; } };
})();

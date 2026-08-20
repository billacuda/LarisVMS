// Shared "focused/fullscreen view" behavior for a single camera tile, used by both Live and
// Playback. Double-click enters real browser Fullscreen API on the tile's own container element
// (not the bare <video>) so sibling overlay controls stay in the fullscreened render subtree and
// can still be interacted with — fullscreening only the <video> would drop every sibling button
// out of the render tree entirely, with no way back in except Esc. Because this uses the real
// Fullscreen API rather than an in-page "big tile" CSS toggle, every other tile on the page simply
// keeps decoding/playing off-screen the whole time — nothing needs to be torn down or hidden.
window.larisvmsFullscreenTile = (function () {
    'use strict';

    // Deliberately a separate implementation/state from Playback's own button-driven digital zoom
    // (wireZoom/.pb-zoom-in/out/reset in playback-player.js), not a shared one — that zoom stays
    // exactly as-is for the normal (non-fullscreen) grid view; this one only ever runs while this
    // specific tile is the fullscreen element.
    function wire(containerEl, videoEl, opts) {
        opts = opts || {};
        var minScale = opts.minScale || 1;
        var maxScale = opts.maxScale || 4;
        var scale = 1, panX = 0, panY = 0;

        function apply() {
            videoEl.style.transform = 'scale(' + scale + ') translate(' + panX + 'px, ' + panY + 'px)';
        }

        function resetZoom() {
            scale = 1; panX = 0; panY = 0;
            videoEl.style.transform = '';
            videoEl.style.cursor = '';
            endDrag();
            endPinch();
            activePointers = {};
        }

        function isFs() { return document.fullscreenElement === containerEl; }

        containerEl.addEventListener('dblclick', function (e) {
            e.stopPropagation();
            if (isFs()) {
                document.exitFullscreen().catch(function () { /* ignore */ });
            } else {
                containerEl.requestFullscreen().catch(function () { /* ignore — e.g. iframe policy */ });
            }
        });

        document.addEventListener('fullscreenchange', function () {
            var active = isFs();
            containerEl.classList.toggle('tile-fullscreen', active);
            if (!active) resetZoom();
            if (opts.onFullscreenChange) opts.onFullscreenChange(active);
        });

        // Wheel up = zoom in, down = zoom out, floored at minScale (normal fill size) — only live
        // while this tile is actually the fullscreen element, so wheel scrolling the page itself is
        // never hijacked outside of fullscreen.
        containerEl.addEventListener('wheel', function (e) {
            if (!isFs()) return;
            e.preventDefault();
            var factor = e.deltaY < 0 ? 1.25 : 0.8;
            var next = Math.min(maxScale, Math.max(minScale, scale * factor));
            if (next === scale) return;
            scale = next;
            if (scale === minScale) { panX = 0; panY = 0; }
            apply();
            videoEl.style.cursor = scale > minScale ? 'grab' : '';
        }, { passive: false });

        // Pointer events + setPointerCapture rather than plain mouse events on window, for exactly
        // the reason timeline.js's own drag handler documents: a mouseup that never reaches the page
        // (released outside the window, or swallowed by the fullscreen transition when Esc is hit
        // mid-drag) used to leave `dragging` stuck true forever, after which every later mousemove
        // anywhere on the page kept panning this video and stole gestures meant for other controls —
        // an intermittent "some other control just stops responding" bug that only ever showed up
        // after a zoom/fullscreen pan. Capture guarantees the matching pointerup/pointercancel comes
        // back to this element, and endDrag is idempotent so resetZoom can also call it directly.
        var dragging = false, startX = 0, startY = 0, startPanX = 0, startPanY = 0, dragPointerId = null;

        // ── Pinch-to-zoom (M16) ──────────────────────────────────────────────
        // Every currently-down pointer's last known position, keyed by pointerId — two entries means
        // a pinch is in progress. A touchscreen delivers each finger as its own Pointer Events stream
        // (same pointerdown/move/up/cancel this file already used for mouse drag-to-pan), so pinch is
        // "the same events, tracked for up to two pointers at once" rather than a separate touch API.
        var activePointers = {};
        var pinchStartDist = null, pinchStartScale = 1, pinchStartMidX = 0, pinchStartMidY = 0;
        var pinchStartPanX = 0, pinchStartPanY = 0;

        function dist(a, b) { return Math.hypot(a.x - b.x, a.y - b.y); }
        function midpoint(a, b) { return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }; }

        function endDrag() {
            if (!dragging) return;
            dragging = false;
            if (dragPointerId !== null && containerEl.hasPointerCapture(dragPointerId)) {
                containerEl.releasePointerCapture(dragPointerId);
            }
            dragPointerId = null;
            videoEl.style.cursor = isFs() && scale > minScale ? 'grab' : '';
        }

        function endPinch() {
            pinchStartDist = null;
        }

        containerEl.addEventListener('pointerdown', function (e) {
            if (!isFs()) return;
            activePointers[e.pointerId] = { x: e.clientX, y: e.clientY };
            var ids = Object.keys(activePointers);

            if (ids.length === 2) {
                // A second finger landed — hand off from single-pointer pan (if one was active) to
                // a pinch anchored on both fingers' current midpoint/spacing.
                endDrag();
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

            if (e.button !== 0 || scale <= minScale) return;
            // See playback-player.js's wireZoom for why this matters: without it, dragging the
            // <video> can also kick off the browser's own native drag-out-the-frame gesture, which
            // then owns the mouse for the rest of that gesture and shows the no-drop cursor instead
            // of actually panning.
            e.preventDefault();
            dragging = true;
            startX = e.clientX; startY = e.clientY;
            startPanX = panX; startPanY = panY;
            dragPointerId = e.pointerId;
            containerEl.setPointerCapture(e.pointerId);
            videoEl.style.cursor = 'grabbing';
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
                videoEl.style.cursor = scale > minScale ? 'grab' : '';
                return;
            }

            if (!dragging) return;
            panX = startPanX + (e.clientX - startX) / scale;
            panY = startPanY + (e.clientY - startY) / scale;
            apply();
        });
        function onPointerEnd(e) {
            delete activePointers[e.pointerId];
            if (Object.keys(activePointers).length < 2) endPinch();
            endDrag();
        }
        containerEl.addEventListener('pointerup', onPointerEnd);
        containerEl.addEventListener('pointercancel', onPointerEnd);

        return {
            isFullscreen: isFs,
            exitFullscreen: function () { if (isFs()) document.exitFullscreen().catch(function () { /* ignore */ }); }
        };
    }

    return { wire: wire };
})();

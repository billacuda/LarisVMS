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

        var dragging = false, startX = 0, startY = 0, startPanX = 0, startPanY = 0;
        containerEl.addEventListener('mousedown', function (e) {
            if (!isFs() || scale <= minScale) return;
            // See playback-player.js's wireZoom for why this matters: without it, dragging the
            // <video> can also kick off the browser's own native drag-out-the-frame gesture, which
            // then owns the mouse for the rest of that gesture and shows the no-drop cursor instead
            // of actually panning.
            e.preventDefault();
            dragging = true;
            startX = e.clientX; startY = e.clientY;
            startPanX = panX; startPanY = panY;
            videoEl.style.cursor = 'grabbing';
        });
        window.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            panX = startPanX + (e.clientX - startX) / scale;
            panY = startPanY + (e.clientY - startY) / scale;
            apply();
        });
        window.addEventListener('mouseup', function () {
            if (!dragging) return;
            dragging = false;
            videoEl.style.cursor = isFs() && scale > minScale ? 'grab' : '';
        });

        return {
            isFullscreen: isFs,
            exitFullscreen: function () { if (isFs()) document.exitFullscreen().catch(function () { /* ignore */ }); }
        };
    }

    return { wire: wire };
})();

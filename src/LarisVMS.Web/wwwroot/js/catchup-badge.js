// Shared "catching up" cell overlay for the live grid (live-view.js) and Playback (playback-player.js):
// a small badge on a tile while its video is running faster than 1x to close a gap — 1.5x drift
// catch-up on live, stepped fast-forward or a drift resync on playback. Insert/remove only; the
// callers decide when. Mirrors the overlay-sibling pattern of live-view.js's freeze overlay.
(function () {
    'use strict';

    // container: the position:relative tile element the badge sits in (a .pb-tile-frame, or a live
    // .view-cell / the video's parent). text (optional): e.g. "1.5×" or "16×" — the recycle
    // glyph is prepended here. Pinned top-center by .catchup-badge (site.css) to stay clear of the
    // corner badges and the hover controls along the bottom.
    function show(container, text) {
        if (!container) return;
        var badge = container.querySelector(':scope > .catchup-badge');
        if (!badge) {
            badge = document.createElement('span');
            badge.className = 'badge catchup-badge position-absolute top-0 start-50 translate-middle-x mt-1';
            container.appendChild(badge);
        }
        // ︎: text-style the recycle glyph so it doesn't render as a full-color emoji on some platforms.
        badge.textContent = '♻︎' + (text ? ' ' + text : '');
    }

    function hide(container) {
        if (!container) return;
        var badge = container.querySelector(':scope > .catchup-badge');
        if (badge) badge.remove();
    }

    window.larisvmsCatchupBadge = { show: show, hide: hide };
})();

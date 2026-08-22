// Live "N of M ready" counter for the Snapshots grid. Each card's thumbnail is generated on demand
// the first time it's requested (see IndexModel's own doc comment) — there's no pre-generated backlog
// behind it the way hover-preview thumbnails have a background backfill service for — so a fresh page
// full of never-before-viewed instants can take a few real seconds per image while ffmpeg extracts
// each frame. This gives that a visible sense of progress instead of a silently, unevenly filling grid.
//
// Page-scoped only: counts this page's own cards (up to 24), not a system-wide count of every
// snapshot anywhere that hasn't been viewed yet — there's no such number to report, since nothing
// pre-generates these ahead of a viewer actually looking.
(function () {
    'use strict';

    function init() {
        var statusEl = document.getElementById('snapshotLoadStatus');
        var images = document.querySelectorAll('[data-snapshot-thumb]');
        if (!statusEl || images.length === 0) return;

        var total = images.length;
        var resolved = 0;

        function render() {
            if (resolved >= total) {
                statusEl.textContent = 'All ' + total + ' ready';
                // Fades out shortly after finishing rather than lingering forever once there's
                // nothing left to report — a live counter that never goes away starts reading as
                // broken once it's stuck at "24 of 24" for the rest of the visit.
                setTimeout(function () { statusEl.classList.add('d-none'); }, 3000);
            } else {
                statusEl.textContent = resolved + ' of ' + total + ' ready (' + (total - resolved) + ' generating…)';
            }
        }

        function onResolve() {
            resolved++;
            render();
        }

        statusEl.classList.remove('d-none');
        render();
        images.forEach(function (img) {
            // A lazy-loaded image (loading="lazy") still off-screen hasn't started fetching yet, and
            // img.complete correctly stays false until the browser actually finishes attempting the
            // load (success or failure) — so a not-yet-requested lazy image is never miscounted as
            // already done here.
            if (img.complete) {
                onResolve();
            } else {
                img.addEventListener('load', onResolve, { once: true });
                img.addEventListener('error', onResolve, { once: true });
            }
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();

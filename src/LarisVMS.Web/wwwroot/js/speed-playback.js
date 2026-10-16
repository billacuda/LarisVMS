// Shows a speed badge over a playback tile while an object a speed trap measured is in view
// ("⏱ car (red) 42 km/h", red when it beat the trap's limit). Follows the tile's own playhead, the
// same way talk-playback.js follows it for recorded talk audio; playback-player.js attaches one per
// tile.
window.larisvmsSpeedPlayback = (function () {
    'use strict';

    var TICK_MS = 500;
    var FETCH_SPAN_MS = 2 * 3600 * 1000;    // fetch measurements ±2 h around the playhead
    var REFRESH_MS = 60000;                 // re-fetch near "now", where new ones can appear

    function attach(cameraId, videoEl, wallClockMs) {
        var frame = videoEl.parentNode;
        var badge = document.createElement('span');
        badge.className = 'badge pb-speed-badge';
        badge.style.cssText = 'position:absolute; left:8px; bottom:8px; z-index:5; display:none; pointer-events:none; font-size:.8rem;';
        if (frame) frame.appendChild(badge);

        var markers = [];
        var fetchedFrom = null, fetchedTo = null, fetchedAt = 0, fetching = false;

        function fetchAround(t) {
            if (fetching) return;
            fetching = true;
            var from = new Date(t - FETCH_SPAN_MS).toISOString();
            var to = new Date(t + FETCH_SPAN_MS).toISOString();
            fetch('/api/cameras/' + cameraId + '/speeds?from=' + encodeURIComponent(from) + '&to=' + encodeURIComponent(to))
                .then(function (r) { return r.ok ? r.json() : []; })
                .then(function (list) {
                    markers = (list || []).map(function (m) {
                        return {
                            first: window.larisvmsTime.parse(m.firstSeenUtc).getTime(),
                            last: window.larisvmsTime.parse(m.lastSeenUtc).getTime(),
                            text: '⏱ ' + m.label + (m.color ? ' (' + m.color + ')' : '') + ' ' + m.speedText,
                            kph: m.speedKph,
                            overLimit: !!m.overLimit
                        };
                    });
                    fetchedFrom = t - FETCH_SPAN_MS;
                    fetchedTo = t + FETCH_SPAN_MS;
                    fetchedAt = Date.now();
                })
                .catch(function () { /* nothing to show this time; the next tick retries */ })
                .then(function () { fetching = false; });
        }

        function tick() {
            var t = wallClockMs();
            if (t === null || t === undefined || isNaN(t)) { badge.style.display = 'none'; return; }

            var stale = fetchedFrom === null || t < fetchedFrom + 60000 || t > fetchedTo - 60000
                || (fetchedTo > Date.now() && Date.now() - fetchedAt > REFRESH_MS);
            if (stale) fetchAround(t);

            // The fastest object in view right now.
            var best = null;
            for (var i = 0; i < markers.length; i++) {
                var m = markers[i];
                if (t >= m.first && t <= m.last && (!best || m.kph > best.kph)) best = m;
            }
            if (!best) { badge.style.display = 'none'; return; }
            badge.textContent = best.text;
            badge.className = 'badge pb-speed-badge ' + (best.overLimit ? 'bg-danger' : 'bg-dark');
            badge.style.display = '';
        }

        var events = ['seeked', 'play', 'pause'];
        events.forEach(function (e) { videoEl.addEventListener(e, tick); });
        var timer = setInterval(tick, TICK_MS);

        return {
            detach: function () {
                clearInterval(timer);
                events.forEach(function (e) { videoEl.removeEventListener(e, tick); });
                if (badge.parentNode) badge.parentNode.removeChild(badge);
            }
        };
    }

    return { attach: attach };
})();

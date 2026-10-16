// Plays recorded push-to-talk presses (two-way audio) in sync with a playback tile's video. The
// camera's own microphone is in the video's audio track already; this adds the other direction —
// what an operator said through the camera's speaker — from GET /talk-audio/{cameraId}/{id}.
//
// Follows the tile: plays only while the video plays, at the same speed, seeks with it, and uses
// the tile's own mute and volume (so, like every stream, it starts silent). playback-player.js
// attaches one per tile.
window.larisvmsTalkPlayback = (function () {
    'use strict';

    var TICK_MS = 500;
    var FETCH_SPAN_MS = 2 * 3600 * 1000;    // fetch presses ±2 h around the playhead
    var REFRESH_MS = 60000;                 // re-fetch near "now", where new presses can appear
    var DRIFT_S = 0.35;

    function attach(cameraId, videoEl, wallClockMs) {
        var audio = new Audio();
        audio.preload = 'auto';
        var recordings = [];
        var fetchedFrom = null, fetchedTo = null, fetchedAt = 0, fetching = false;
        var currentId = null;

        function fetchAround(t) {
            if (fetching) return;
            fetching = true;
            var from = new Date(t - FETCH_SPAN_MS).toISOString();
            var to = new Date(t + FETCH_SPAN_MS).toISOString();
            fetch('/api/cameras/' + cameraId + '/talk-recordings?from=' + encodeURIComponent(from) + '&to=' + encodeURIComponent(to))
                .then(function (r) { return r.ok ? r.json() : []; })
                .then(function (list) {
                    recordings = (list || []).map(function (r) {
                        return { id: r.id, start: window.larisvmsTime.parse(r.startUtc).getTime(), end: window.larisvmsTime.parse(r.endUtc).getTime() };
                    });
                    fetchedFrom = t - FETCH_SPAN_MS;
                    fetchedTo = t + FETCH_SPAN_MS;
                    fetchedAt = Date.now();
                })
                .catch(function () { /* no talk audio this time; the next tick retries */ })
                .then(function () { fetching = false; });
        }

        function stopAudio() {
            if (!audio.paused) audio.pause();
        }

        function tick() {
            var t = wallClockMs();
            if (t === null || t === undefined || isNaN(t)) { stopAudio(); return; }

            var stale = fetchedFrom === null || t < fetchedFrom + 60000 || t > fetchedTo - 60000
                || (fetchedTo > Date.now() && Date.now() - fetchedAt > REFRESH_MS);
            if (stale) fetchAround(t);

            var rec = null;
            for (var i = 0; i < recordings.length; i++) {
                if (t >= recordings[i].start && t < recordings[i].end) { rec = recordings[i]; break; }
            }
            if (!rec) { stopAudio(); return; }

            if (currentId !== rec.id) {
                currentId = rec.id;
                audio.src = '/talk-audio/' + cameraId + '/' + rec.id;
            }

            audio.muted = videoEl.muted;
            audio.volume = videoEl.volume;
            var rate = videoEl.playbackRate;
            if (videoEl.paused || videoEl.seeking || rate < 0.25 || rate > 4) { stopAudio(); return; }
            if (audio.playbackRate !== rate) audio.playbackRate = rate;

            var expected = (t - rec.start) / 1000;
            if (audio.readyState >= 1 && Math.abs(audio.currentTime - expected) > DRIFT_S) audio.currentTime = expected;
            if (audio.paused) audio.play().catch(function () { /* autoplay policy — follows the next user action */ });
        }

        var events = ['play', 'pause', 'seeking', 'seeked', 'ratechange', 'volumechange'];
        events.forEach(function (e) { videoEl.addEventListener(e, tick); });
        var timer = setInterval(tick, TICK_MS);

        return {
            detach: function () {
                clearInterval(timer);
                events.forEach(function (e) { videoEl.removeEventListener(e, tick); });
                audio.pause();
                audio.removeAttribute('src');
                audio.load();
            }
        };
    }

    return { attach: attach };
})();

/*
 * Failover plan phase 1: resolves how a browser should reach a camera's recorder node for live
 * viewing — through this server (proxy, the default) or straight to the node over HTTPS (direct).
 *
 * Everything here fails safe: if the ticket call errors or returns something unexpected, callers get
 * a plain proxy ticket pointing back at this host's own /live/{id}, i.e. exactly what live-view.js
 * did before this file existed.
 *
 * Playback does not go through here — in direct mode the /playback-segment endpoint 302-redirects the
 * browser to the node itself, so playback-player.js needs no changes.
 */
(function () {
    'use strict';

    var ticketCache = {}; // cameraId -> { ticket, expiresAtMs }

    function proxyTicket(cameraId) {
        return { mode: 'proxy', insecure: false, videoUrl: '/live/' + encodeURIComponent(cameraId), token: null, trustUrl: null, expiresInSeconds: 0 };
    }

    function resolveLive(cameraId) {
        var cached = ticketCache[cameraId];
        if (cached && cached.expiresAtMs > Date.now()) {
            return Promise.resolve(cached.ticket);
        }

        return fetch('/api/media/live-ticket/' + encodeURIComponent(cameraId), { credentials: 'same-origin' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (ticket) {
                if (!ticket || (ticket.mode !== 'direct' && ticket.mode !== 'proxy')) {
                    return proxyTicket(cameraId);
                }
                // Cache until ~10s before the token expires; a proxy ticket has no token so cache it
                // briefly just to avoid a call per reconnect.
                var ttlMs = ticket.mode === 'direct'
                    ? Math.max(5, (ticket.expiresInSeconds || 60) - 10) * 1000
                    : 15000;
                ticketCache[cameraId] = { ticket: ticket, expiresAtMs: Date.now() + ttlMs };
                return ticket;
            })
            .catch(function () { return proxyTicket(cameraId); });
    }

    function invalidate(cameraId) {
        delete ticketCache[cameraId];
    }

    function reportTiming(cameraId, mode, msToFirstFrame) {
        try {
            fetch('/api/media/timing', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ cameraId: cameraId, mode: mode, msToFirstFrame: Math.round(msToFirstFrame) }),
                keepalive: true
            }).catch(function () { });
        } catch (e) { /* best effort */ }
    }

    // Stutter-investigation beacon (see MediaStreamEventBeacon's own doc comment) — same best-effort,
    // never-blocks-playback shape as reportTiming above.
    function reportStreamEvent(cameraId, role, streamMode, eventType, magnitude, detail) {
        try {
            fetch('/api/media/stream-event', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    cameraId: cameraId, role: role, streamMode: streamMode, eventType: eventType,
                    magnitude: (typeof magnitude === 'number' && isFinite(magnitude)) ? magnitude : null,
                    detail: detail || null
                }),
                keepalive: true
            }).catch(function () { });
        } catch (e) { /* best effort */ }
    }

    window.larisvmsMediaEndpoint = {
        resolveLive: resolveLive,
        invalidate: invalidate,
        reportTiming: reportTiming,
        reportStreamEvent: reportStreamEvent
    };
})();

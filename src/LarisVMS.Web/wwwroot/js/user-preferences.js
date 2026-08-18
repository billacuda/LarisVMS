// Server-backed replacement for what used to live only in localStorage (theme, last-watched view,
// table page size, playback clock format) — a preference set on one browser used to simply not
// exist on another, and reset outright on a fresh sign-in from a new machine. Loaded once, near the
// top of _Layout.cshtml's own script block, so every other script on the page can read/write
// through it.
//
// The API is deliberately localStorage-shaped (get/set) but NOT synchronous the way localStorage
// is — a network round trip can't be. get() reads an in-memory cache populated once by a single
// GET /api/preferences fetched on load; before that fetch resolves, get() returns the caller's own
// fallback, exactly as if the preference had never been set. Anything that must have a value before
// first paint (the theme anti-flash script) cannot use this and keeps reading localStorage directly
// — see theme.js's own comment for how the two are reconciled once this cache does load.
window.larisvmsPreferences = (function () {
    'use strict';

    var cache = null; // null until the initial fetch resolves; a plain {key: value} map after that

    var ready = fetch('/api/preferences')
        .then(function (r) { return r.ok ? r.json() : {}; })
        .catch(function () { return {}; }) // offline, expired session, private mode with cookies blocked — degrade to "no preferences saved"
        .then(function (data) {
            cache = data || {};
            return cache;
        });

    // Synchronous by design, matching how every existing call site already reads localStorage —
    // returns fallback both when the key was never set and when the initial fetch hasn't resolved
    // yet. A caller whose first read must reflect a just-loaded value should await whenReady()
    // instead (see playback-player.js/dashboard.js's own init sequences).
    function get(key, fallback) {
        if (cache && Object.prototype.hasOwnProperty.call(cache, key)) return cache[key];
        return fallback;
    }

    function set(key, value) {
        var stringValue = String(value);
        if (!cache) cache = {};
        cache[key] = stringValue; // updates the local read-back immediately, not just after the PUT resolves

        // Fire-and-forget: a preference write must never block whatever UI action triggered it, and
        // a failed write just means it doesn't outlive this page load — the same failure mode every
        // existing localStorage call site already tolerates (private browsing throwing on write).
        fetch('/api/preferences/' + encodeURIComponent(key), {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ value: stringValue })
        }).catch(function () { /* best-effort */ });
    }

    return { get: get, set: set, whenReady: function () { return ready; } };
})();

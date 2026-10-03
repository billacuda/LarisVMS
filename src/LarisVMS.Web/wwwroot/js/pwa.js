// Registers the (no-op) service worker that makes the site installable as an app — see /sw.js.
(function () {
    'use strict';
    if (!('serviceWorker' in navigator)) return;
    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/sw.js').catch(function () { /* non-fatal: site works without it */ });
    });
})();

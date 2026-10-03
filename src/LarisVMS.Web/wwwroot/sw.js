// Minimal service worker — exists so browsers treat the site as an installable app (PWA). Deliberately
// no fetch handler and no caching: every page is auth-gated live data or video, and a cache layer
// would risk serving stale or another user's content. Requests go straight to the network as if this
// file didn't exist.
self.addEventListener('install', function () {
    self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(self.clients.claim());
});

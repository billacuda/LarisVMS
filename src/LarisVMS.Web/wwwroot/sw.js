// Service worker — makes the site installable as an app (PWA) and shows push notifications.
// Deliberately no fetch handler and no caching: every page is auth-gated live data or video, and a
// cache layer would risk serving stale or another user's content. Requests go straight to the
// network as if this file didn't exist.
self.addEventListener('install', function () {
    self.skipWaiting();
});

self.addEventListener('activate', function (event) {
    event.waitUntil(self.clients.claim());
});

// Push notifications. Web Push messages carry the notification JSON directly; Firebase messages carry
// it in their "data" map. Either way it's { title, body, url, tag }. The tag is shared with the
// in-page transport, so the same notification arriving both ways shows once.
self.addEventListener('push', function (event) {
    var message = {};
    try { message = event.data ? event.data.json() : {}; } catch (e) { message = { body: event.data ? event.data.text() : '' }; }
    var n = message.data && message.data.title ? message.data : message;
    event.waitUntil(self.registration.showNotification(n.title || 'LarisVMS', {
        body: n.body || '',
        tag: n.tag || undefined,
        icon: '/icons/icon-192.png',
        badge: '/icons/favicon-32.png',
        data: { url: n.url || '/' }
    }));
});

// Clicking a notification focuses an open LarisVMS tab and takes it to the notification's page, or
// opens one.
self.addEventListener('notificationclick', function (event) {
    event.notification.close();
    var url = new URL((event.notification.data && event.notification.data.url) || '/', self.location.origin).href;
    event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (clients) {
        for (var i = 0; i < clients.length; i++) {
            var client = clients[i];
            if (new URL(client.url).origin === self.location.origin && 'focus' in client) {
                return client.focus().then(function (c) { return c.navigate ? c.navigate(url) : c; });
            }
        }
        return self.clients.openWindow(url);
    }));
});

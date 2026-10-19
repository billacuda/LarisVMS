// Personal notifications in the browser. Loaded on every page.
//
// - On-premises transport: while a LarisVMS tab is open it holds /api/notifications/stream (Server-Sent
//   Events) and shows each notification as a system notification (when allowed) or an in-page toast.
// - Closed-browser transports: enableThisBrowser() asks for permission and registers this browser for
//   Web Push (VAPID) — or, when only Firebase is on, Firebase Cloud Messaging. One browser can hold only
//   one push registration, so Web Push wins when both are on. The service worker (/sw.js) shows those.
//
// Every transport uses the same tag per notification, so one arriving twice shows once.
window.larisvmsNotifications = (function () {
    'use strict';

    var configPromise = null;

    function getConfig() {
        if (!configPromise) {
            configPromise = fetch('/api/notifications/config', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
                .then(function (r) { return r.ok && (r.headers.get('content-type') || '').indexOf('json') !== -1 ? r.json() : null; })
                .catch(function () { return null; });
        }
        return configPromise;
    }

    function supported() { return 'Notification' in window && 'serviceWorker' in navigator; }

    function permission() { return supported() ? Notification.permission : 'unsupported'; }

    // ── Showing ───────────────────────────────────────────────────────────

    function show(n) {
        if (permission() === 'granted') {
            return navigator.serviceWorker.ready.then(function (reg) {
                return reg.showNotification(n.title || 'LarisVMS', {
                    body: n.body || '', tag: n.tag || undefined, icon: '/icons/icon-192.png', data: { url: n.url || '/' }
                });
            }).catch(function () { toast(n); });
        }
        toast(n);
        return Promise.resolve();
    }

    function toast(n) {
        var host = document.getElementById('lvNotificationToasts');
        if (!host) {
            host = document.createElement('div');
            host.id = 'lvNotificationToasts';
            host.className = 'lv-toasts';
            host.setAttribute('aria-live', 'polite');
            document.body.appendChild(host);
        }
        if (n.tag) {
            var same = host.querySelector('[data-tag="' + CSS.escape(n.tag) + '"]');
            if (same) same.remove();
        }
        var el = document.createElement('div');
        el.className = 'lv-toast shadow';
        if (n.tag) el.setAttribute('data-tag', n.tag);
        var title = document.createElement('div');
        title.className = 'fw-semibold small';
        title.textContent = n.title || 'LarisVMS';
        var body = document.createElement('div');
        body.className = 'small';
        body.textContent = n.body || '';
        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'btn-close btn-close-sm float-end';
        close.setAttribute('aria-label', 'Dismiss');
        close.addEventListener('click', function (e) { e.stopPropagation(); el.remove(); });
        el.appendChild(close);
        el.appendChild(title);
        el.appendChild(body);
        if (n.url) {
            el.style.cursor = 'pointer';
            el.addEventListener('click', function () { window.location.href = n.url; });
        }
        host.appendChild(el);
        setTimeout(function () { el.remove(); }, 15000);
    }

    // ── On-premises stream ────────────────────────────────────────────────

    function openStream(config) {
        if (!config || !config.onPrem || !('EventSource' in window)) return;
        var source = new EventSource('/api/notifications/stream');
        source.addEventListener('notification', function (e) {
            try { show(JSON.parse(e.data)); } catch (err) { /* malformed — ignore */ }
        });
        // EventSource reconnects on its own after a drop; a 204 (transport turned off) ends it for good.
    }

    // ── Registering this browser for push ─────────────────────────────────

    function deviceLabel() {
        var ua = navigator.userAgent;
        var browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
        var os = /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows' : /Mac OS/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
        return browser + (os ? ' on ' + os : '');
    }

    function urlBase64ToUint8Array(base64) {
        var padded = (base64 + '===='.slice((base64.length + 3) % 4 + 1)).replace(/-/g, '+').replace(/_/g, '/');
        var raw = atob(padded);
        var out = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
        return out;
    }

    function register(body) {
        return fetch('/api/notifications/devices', {
            method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
        }).then(function (r) { if (!r.ok) throw new Error('The server didn’t accept this browser (' + r.status + ').'); });
    }

    function loadScript(src) {
        return new Promise(function (resolve, reject) {
            var s = document.createElement('script');
            s.src = src;
            s.onload = resolve;
            s.onerror = function () { reject(new Error('Couldn’t load ' + src)); };
            document.head.appendChild(s);
        });
    }

    function subscribeWebPush(reg, config) {
        return reg.pushManager.getSubscription().then(function (existing) {
            return existing || reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(config.webPush.publicKey) });
        }).then(function (sub) {
            var json = sub.toJSON();
            return register({ kind: 'WebPush', endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth, deviceLabel: deviceLabel() });
        });
    }

    function subscribeFcm(reg, config) {
        // Served by this site (wwwroot/lib/firebase), not loaded from Google's CDN at run time.
        var base = '/lib/firebase/';
        return loadScript(base + 'firebase-app-compat.js')
            .then(function () { return loadScript(base + 'firebase-messaging-compat.js'); })
            .then(function () {
                var app = window.firebase.apps.length ? window.firebase.app() : window.firebase.initializeApp(config.fcm.webConfig);
                return window.firebase.messaging(app).getToken({ vapidKey: config.fcm.vapidKey || undefined, serviceWorkerRegistration: reg });
            })
            .then(function (token) {
                if (!token) throw new Error('Firebase didn’t return a token for this browser.');
                return register({ kind: 'Fcm', endpoint: token, deviceLabel: deviceLabel() + ' (Firebase)' });
            });
    }

    /// Asks for permission and registers this browser with whichever push transport is on.
    /// Resolves to a short message for the page to show.
    function enableThisBrowser() {
        if (!supported()) return Promise.reject(new Error('This browser doesn’t support notifications.'));
        return Notification.requestPermission().then(function (result) {
            if (result !== 'granted') throw new Error('Notifications are blocked for this site. Allow them in the browser’s site settings, then try again.');
            return Promise.all([getConfig(), navigator.serviceWorker.register('/sw.js').then(function () { return navigator.serviceWorker.ready; })]);
        }).then(function (both) {
            var config = both[0], reg = both[1];
            if (!config) throw new Error('Couldn’t read the notification settings.');
            if (config.webPush) return subscribeWebPush(reg, config).then(function () { return 'This browser will get notifications, even when LarisVMS isn’t open.'; });
            if (config.fcm) return subscribeFcm(reg, config).then(function () { return 'This browser will get notifications through Firebase, even when LarisVMS isn’t open.'; });
            return config.onPrem
                ? 'Notifications will show while a LarisVMS tab is open. (Push to a closed browser isn’t turned on.)'
                : 'Browser notifications are turned off in Settings.';
        });
    }

    function sendTest() {
        return fetch('/api/notifications/test', { method: 'POST', credentials: 'same-origin' }).then(function (r) {
            if (!r.ok) throw new Error('The test couldn’t be sent (' + r.status + ').');
            return r.json();
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        // The login and setup pages have no session — the config call fails there and nothing starts.
        getConfig().then(openStream);
    });

    return { enableThisBrowser: enableThisBrowser, sendTest: sendTest, permission: permission, getConfig: getConfig, show: show };
})();

(function () {
    'use strict';

    var STORAGE_KEY = 'larisvms.theme';
    var html = document.documentElement;

    function updateThemeIcon() {
        var icon = document.getElementById('themeIcon');
        if (!icon) return;
        icon.textContent = html.getAttribute('data-bs-theme') === 'dark' ? '☀️' : '🌙';
    }

    function applyTheme(theme) {
        if (theme === 'auto') {
            var prefersDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
            html.setAttribute('data-bs-theme', prefersDark ? 'dark' : 'light');
        } else {
            html.setAttribute('data-bs-theme', theme);
        }
        updateThemeIcon();
    }

    function getStoredTheme() {
        return localStorage.getItem(STORAGE_KEY) || 'auto';
    }

    // The anti-flash inline script in <head> already applied the stored/system theme before this
    // ran, so all that's left here is to sync the toggle icon and wire up live switching.
    //
    // localStorage stays the source of truth for the *instant*, before-first-paint apply — a network
    // round trip through user-preferences.js can't run early enough to avoid a flash, so it can't
    // replace localStorage here the way it does for every other preference. Every write still also
    // goes to the server (best-effort, via larisvmsPreferences.set), so the choice follows the user
    // to a browser/device that hasn't set a theme of its own yet — see the ready-time reconciliation
    // below for exactly when a device without its own choice picks up the server's.
    window.toggleTheme = function () {
        var current = html.getAttribute('data-bs-theme');
        var next = current === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem(STORAGE_KEY, next); } catch (e) { /* private browsing */ }
        if (window.larisvmsPreferences) window.larisvmsPreferences.set('theme', next);
        applyTheme(next);
    };

    // Exposed so blazor-chrome-sync.js can re-run this after every Blazor enhanced navigation —
    // this script itself only runs once, on the true first page load (App.razor's own <script> tags
    // live outside the region enhanced nav re-executes; only a page's own SectionContent-declared
    // scripts get that treatment). Each fresh Static SSR response's own markup starts at
    // data-bs-theme="light" (the server has no way to read localStorage), so without a re-apply
    // hook the theme would silently revert to light on every in-app navigation. Harmless to call
    // redundantly on the true first load too, where the <head> anti-flash script already set the
    // same value.
    window.larisvmsTheme = { reapply: function () { applyTheme(getStoredTheme()); } };
    applyTheme(getStoredTheme());

    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (getStoredTheme() === 'auto') applyTheme('auto');
    });

    // One-time cross-device pickup: only for a browser that has never stored a theme of its own
    // (a fresh profile, private window, or new device) — deliberately not "server always wins on
    // every load", which would flash the theme on every normal reload for the (common) case where a
    // user is mid-session on the same device the value came from. A device with its own stored
    // choice keeps it; a device with none adopts whatever the user last set anywhere else.
    if (window.larisvmsPreferences && localStorage.getItem(STORAGE_KEY) === null) {
        window.larisvmsPreferences.whenReady().then(function () {
            var remote = window.larisvmsPreferences.get('theme', null);
            if (remote && remote !== getStoredTheme()) {
                try { localStorage.setItem(STORAGE_KEY, remote); } catch (e) { /* private browsing */ }
                applyTheme(remote);
            }
        });
    }
})();

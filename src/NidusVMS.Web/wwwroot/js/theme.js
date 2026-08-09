(function () {
    'use strict';

    var STORAGE_KEY = 'nidusvms.theme';
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
    window.toggleTheme = function () {
        var current = html.getAttribute('data-bs-theme');
        var next = current === 'dark' ? 'light' : 'dark';
        try { localStorage.setItem(STORAGE_KEY, next); } catch (e) { /* private browsing */ }
        applyTheme(next);
    };

    updateThemeIcon();

    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (getStoredTheme() === 'auto') applyTheme('auto');
    });
})();

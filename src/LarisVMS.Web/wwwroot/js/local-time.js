// Times from the server are UTC; everything shown on screen is the viewer's local time. parse()
// reads a server timestamp as UTC even when it arrives without an offset — a browser otherwise reads
// "2026-10-13T15:00:00" as *local*, which is how UTC clock readings ended up on screen as if they were
// local. format() renders one the way the rest of the UI does (toLocaleString).
window.larisvmsTime = (function () {
    'use strict';

    var hasOffset = /(Z|[+-]\d{2}:?\d{2})$/i;

    function parse(value) {
        if (value === null || value === undefined || value === '') return null;
        if (value instanceof Date) return value;
        if (typeof value === 'number') return new Date(value);
        var s = String(value).trim();
        var d = new Date(hasOffset.test(s) || s.indexOf('T') < 0 ? s : s + 'Z');
        return isNaN(d.getTime()) ? null : d;
    }

    function format(value) {
        var d = parse(value);
        return d ? d.toLocaleString() : '';
    }

    // Server-rendered times (LocalTimeMarkup.cs): <time datetime="…Z" data-local="g|G|d"> holds the
    // UTC instant, and its text — formatted in the server's time zone as a no-script fallback — is
    // replaced with the viewer's local time. The tooltip gets the full local date and time.
    var STYLES = {
        g: { dateStyle: 'short', timeStyle: 'short' },
        G: { dateStyle: 'short', timeStyle: 'medium' },
        d: { dateStyle: 'short' }
    };

    function localize(root) {
        Array.prototype.forEach.call((root || document).querySelectorAll('time[data-local]'), function (el) {
            var d = parse(el.getAttribute('datetime'));
            if (!d) return;
            el.textContent = d.toLocaleString(undefined, STYLES[el.getAttribute('data-local')] || STYLES.g);
            el.title = d.toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'long' });
        });
        // A time that only appears in a tooltip: data-local-title="…Z" plus an optional
        // data-local-title-prefix ("Retired ").
        Array.prototype.forEach.call((root || document).querySelectorAll('[data-local-title]'), function (el) {
            var d = parse(el.getAttribute('data-local-title'));
            if (d) el.title = (el.getAttribute('data-local-title-prefix') || '') + d.toLocaleString(undefined, STYLES.g);
        });
        // A tooltip with several times in it: data-local-title-template holds the text with each time
        // as {{…Z}}; the server-formatted title stays as the no-script fallback.
        Array.prototype.forEach.call((root || document).querySelectorAll('[data-local-title-template]'), function (el) {
            el.title = el.getAttribute('data-local-title-template').replace(/\{\{([^}]+)\}\}/g, function (all, iso) {
                var d = parse(iso);
                return d ? d.toLocaleString(undefined, STYLES.g) : all;
            });
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        localize(document);
        // Blazor enhanced navigation and form posts patch the page without a new DOMContentLoaded.
        // Checked here rather than at load: blazor.web.js loads after this script.
        if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', function () { localize(document); });
    });

    return { parse: parse, format: format, localize: localize };
})();

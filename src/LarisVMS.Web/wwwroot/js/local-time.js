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

    return { parse: parse, format: format };
})();

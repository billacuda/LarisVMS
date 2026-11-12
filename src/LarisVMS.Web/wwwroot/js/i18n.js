// Script-side UI text. /locales/{culture}.js (loaded just before this file) sets
// window.larisvmsI18nData = { lang, strings }: every "js." key in the page's language, with English
// already filled in for anything not translated. Pages call:
//   larisvmsI18n.t('js.exports.deleteConfirm', { name: job.name })
//   larisvmsI18n.plural('js.cameras.selected', count)            // fills {count} too
// A key that doesn't exist comes back as the key itself, so a typo is visible rather than blank.
(function () {
    var data = window.larisvmsI18nData || { lang: document.documentElement.lang || 'en', strings: {} };
    var strings = data.strings || {};
    var lang = data.lang || 'en';
    var pluralRules = null;
    var numberFormat = null;

    // The locale for dates and numbers (toLocaleString(larisvmsI18n.locale, …)): the browser's own
    // when it's the same language as the page, so an English page in a British browser keeps
    // day/month dates; otherwise the page's language, so a French page reads in French.
    var locale = lang;
    try {
        var browser = (navigator.languages && navigator.languages[0]) || navigator.language || '';
        if (browser && browser.split('-')[0].toLowerCase() === lang.split('-')[0].toLowerCase()) locale = browser;
    } catch (e) { /* keep the page language */ }

    function fill(text, args) {
        if (!args) return text;
        return text.replace(/\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}/g, function (whole, name) {
            var value = args[name];
            if (value === undefined) {
                // Placeholder names are case-insensitive, as on the server.
                for (var k in args) {
                    if (Object.prototype.hasOwnProperty.call(args, k) && k.toLowerCase() === name.toLowerCase()) { value = args[k]; break; }
                }
            }
            if (value === undefined || value === null) return value === null ? '' : whole;
            return typeof value === 'number' ? formatNumber(value) : String(value);
        });
    }

    function formatNumber(n) {
        try {
            numberFormat = numberFormat || new Intl.NumberFormat(locale);
            return numberFormat.format(n);
        } catch (e) {
            return String(n);
        }
    }

    function t(key, args) {
        var text = strings[key];
        return text === undefined ? key : fill(text, args);
    }

    function plural(key, count, args) {
        var category = 'other';
        try {
            pluralRules = pluralRules || new Intl.PluralRules(lang);
            category = pluralRules.select(count);
        } catch (e) {
            category = count === 1 ? 'one' : 'other';
        }
        var text = strings[key + '_' + category];
        if (text === undefined) text = strings[key + '_other'];
        if (text === undefined) text = strings[key];
        if (text === undefined) return key;
        var all = { count: count };
        if (args) for (var k in args) if (Object.prototype.hasOwnProperty.call(args, k)) all[k] = args[k];
        return fill(text, all);
    }

    // The same text HTML-escaped, for building markup strings: el.innerHTML = '<p>' + larisvmsI18n.html('js.x') + '</p>'.
    function html(key, args) {
        return t(key, args).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    // A canvas font in the page's own font stack: ctx.font = larisvmsI18n.font('bold 12px'). A bare
    // 'sans-serif' leaves Chinese, Japanese, and Korean labels to whatever the browser picks, which
    // can be the wrong regional glyphs or missing characters; the page stack has per-language
    // fallbacks (theme.css --larisvms-cjk-fonts).
    var fontFamily = null;
    function font(sizeAndWeight) {
        if (!fontFamily) {
            try {
                fontFamily = document.body ? getComputedStyle(document.body).fontFamily : '';
            } catch (e) { fontFamily = ''; }
            if (!fontFamily) return sizeAndWeight + ' sans-serif';
        }
        return sizeAndWeight + ' ' + fontFamily;
    }

    window.larisvmsI18n = { lang: lang, locale: locale, t: t, html: html, plural: plural, formatNumber: formatNumber, font: font };
})();

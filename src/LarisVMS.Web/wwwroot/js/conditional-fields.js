// Shows/hides form fields based on another field's value, so a page only shows the settings that
// apply. Markup: data-show-when="FieldName=Value1,Value2" on the element to toggle (usually a
// .lv-field). FieldName is the controlling input's name attribute; for a checkbox the value is
// "true"/"false". Prefix the value list with "!" to invert ("Backend=!BuiltIn"). Hidden fields still
// post their values, so saving never silently clears a setting.
//
// Also: a form[data-dirty-guard] warns before leaving the page with unsaved changes.
(function () {
    function valueOf(form, name) {
        var els = (form || document).querySelectorAll('[name="' + CSS.escape(name) + '"]');
        for (var i = 0; i < els.length; i++) {
            var el = els[i];
            if (el.type === 'checkbox') return el.checked ? 'true' : 'false';
            if (el.type === 'radio') { if (el.checked) return el.value; continue; }
            if (el.type === 'hidden' && els.length > 1) continue; // checkbox's hidden companion
            return el.value;
        }
        return '';
    }

    function apply() {
        document.querySelectorAll('[data-show-when]').forEach(function (el) {
            var spec = el.getAttribute('data-show-when');
            var eq = spec.indexOf('=');
            if (eq < 0) return;
            var name = spec.substring(0, eq);
            var list = spec.substring(eq + 1);
            var negate = list.charAt(0) === '!';
            if (negate) list = list.substring(1);
            var values = list.split(',');
            var match = values.indexOf(valueOf(el.closest('form'), name)) >= 0
                || values.indexOf(valueOf(null, name)) >= 0;
            el.hidden = negate ? match : !match;
        });
    }

    var dirty = false;
    document.addEventListener('change', function (e) {
        apply();
        if (e.target.closest && e.target.closest('form[data-dirty-guard]')) dirty = true;
    });
    document.addEventListener('input', function (e) {
        if (e.target.closest && e.target.closest('form[data-dirty-guard]')) dirty = true;
    });
    document.addEventListener('submit', function () { dirty = false; });
    window.addEventListener('beforeunload', function (e) {
        if (dirty && document.querySelector('form[data-dirty-guard]')) { e.preventDefault(); e.returnValue = ''; }
    });
    // Blazor's enhanced navigation handles in-app link clicks without unloading the page, so
    // beforeunload never fires for them — ask here instead (capture phase, ahead of Blazor).
    document.addEventListener('click', function (e) {
        if (!dirty || !document.querySelector('form[data-dirty-guard]')) return;
        var a = e.target.closest && e.target.closest('a[href]');
        if (!a || a.target === '_blank' || a.hasAttribute('download')) return;
        var href = a.getAttribute('href');
        if (!href || href.charAt(0) === '#' || a.origin !== location.origin) return;
        if (confirm('Leave this page? Your changes have not been saved.')) dirty = false;
        else { e.preventDefault(); e.stopPropagation(); }
    }, true);

    function init() { dirty = false; apply(); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
    if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', init);
})();

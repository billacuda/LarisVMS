// Toggles a masked <input type="password"> between hidden and shown, driven by a sibling button
// carrying data-secret-toggle="<target input id>". Modeled on help-popover.js: an IIFE that wires up
// on DOMContentLoaded and is completely inert on any page with no [data-secret-toggle] element, so
// it costs nothing to load globally from _Layout.cshtml.
//
// The revealed state lives only in the DOM — deliberately NOT persisted through user-preferences.js
// the way the app's other toggles are — so a page refresh always re-masks the value. Emoji icons,
// matching this app's icon convention (see theme.js's moon/sun swap).
(function () {
    var SHOWN = '🙈';   // 🙈 — currently visible, click to hide
    var HIDDEN = '👁️'; // 👁️ — currently hidden, click to reveal

    function apply(button, input, reveal) {
        input.type = reveal ? 'text' : 'password';
        button.setAttribute('aria-pressed', reveal ? 'true' : 'false');
        var label = button.getAttribute('data-secret-label') || 'value';
        button.setAttribute('aria-label', (reveal ? 'Hide ' : 'Show ') + label);
        var glyph = button.querySelector('[data-secret-glyph]') || button;
        glyph.textContent = reveal ? SHOWN : HIDDEN;
    }

    function wire(button) {
        var input = document.getElementById(button.getAttribute('data-secret-toggle'));
        if (!input) return;

        apply(button, input, input.type === 'text');
        button.addEventListener('click', function () {
            apply(button, input, input.type === 'password');
        });

        // Let other page scripts force the field visible (e.g. after generating a fresh value the
        // user needs to read) via: input.dispatchEvent(new Event('secret-reveal:show'))
        input.addEventListener('secret-reveal:show', function () {
            apply(button, input, true);
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('button[data-secret-toggle]').forEach(wire);
    });
})();

// What inline event handlers (onclick="…", onerror="…") used to do, as data- attributes, because the
// strict Content-Security-Policy runs no inline handlers. Loaded in <head> on every layout, so it's
// listening before any image can fail or any button be clicked.
//
//   data-confirm="message"        button/link: asks first and cancels the click if declined;
//                                 <form>: asks before it submits.
//   data-action="toggle-theme"    toggleTheme()
//   data-action="reload"          reloads the page
//   data-action="select"          selects the field's text
//   data-copy-from="#id"          copies that element's value (or text) to the clipboard, flashes ✅
//   data-submit-on-change         submits the field's form when its value changes
//   data-navigate-prefix="/x/"    <select>: goes to prefix + value when a value is chosen
//   data-stop-propagation         a click on it doesn't reach its ancestors (e.g. a <summary>)
//   data-on-error="hide"          <img> that fails to load: hidden (display: none)
//   data-on-error="invisible"     … keeps its space (visibility: hidden)
//   data-on-error="hide-show-next" … hidden, and its next sibling's d-none removed
(function () {
    'use strict';

    // Capture phase, so a declined confirm stops the click before anything else (a form submit,
    // Bootstrap) sees it.
    document.addEventListener('click', function (e) {
        var confirmEl = e.target.closest && e.target.closest('[data-confirm]:not(form)');
        if (confirmEl && !window.confirm(confirmEl.getAttribute('data-confirm'))) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }

        var actionEl = e.target.closest && e.target.closest('[data-action]');
        if (actionEl) {
            var action = actionEl.getAttribute('data-action');
            if (action === 'toggle-theme' && typeof window.toggleTheme === 'function') window.toggleTheme();
            else if (action === 'reload') window.location.reload();
            else if (action === 'select' && typeof actionEl.select === 'function') actionEl.select();
        }

        var copyEl = e.target.closest && e.target.closest('[data-copy-from]');
        if (copyEl && navigator.clipboard) {
            var source = document.querySelector(copyEl.getAttribute('data-copy-from'));
            if (source) {
                var text = 'value' in source && source.value !== undefined ? source.value : source.textContent;
                navigator.clipboard.writeText(text).then(function () {
                    var label = copyEl.textContent;
                    copyEl.textContent = '✅';
                    setTimeout(function () { copyEl.textContent = label; }, 1500);
                });
            }
        }
    }, true);

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (form && form.hasAttribute && form.hasAttribute('data-confirm')
            && !window.confirm(form.getAttribute('data-confirm'))) {
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }, true);

    document.addEventListener('change', function (e) {
        var el = e.target;
        if (!el || !el.hasAttribute) return;
        if (el.hasAttribute('data-submit-on-change') && el.form) el.form.submit();
        var prefix = el.getAttribute('data-navigate-prefix');
        if (prefix && el.value) window.location.href = prefix + el.value;
    });

    // Load errors don't bubble, but they can be caught on the way down.
    document.addEventListener('error', function (e) {
        var img = e.target;
        if (!img || img.tagName !== 'IMG' || !img.hasAttribute('data-on-error')) return;
        var mode = img.getAttribute('data-on-error');
        if (mode === 'invisible') {
            img.style.visibility = 'hidden';
        } else {
            img.style.display = 'none';
            if (mode === 'hide-show-next' && img.nextElementSibling) img.nextElementSibling.classList.remove('d-none');
        }
    }, true);

    // Stopping propagation has to happen on the element itself, before the event reaches its parent.
    function wireStopPropagation(root) {
        Array.prototype.forEach.call((root || document).querySelectorAll('[data-stop-propagation]'), function (el) {
            if (el.__lvStop) return;
            el.__lvStop = true;
            el.addEventListener('click', function (e) { e.stopPropagation(); });
        });
    }
    document.addEventListener('DOMContentLoaded', function () { wireStopPropagation(); });
    window.larisvmsWireStopPropagation = wireStopPropagation;
})();

// Ties form errors to their fields for screen readers. Runs on every page and does nothing without an
// error. A field the server marked invalid, or the browser refused on submit, gets aria-invalid until
// it's edited, and points at its own validation message. An error banner present on load takes focus,
// because it arrived with a whole new page and would otherwise go unannounced.
(function () {
    'use strict';

    function markInvalid(field) {
        field.setAttribute('aria-invalid', 'true');
        if (!field.name) return;
        var message = document.querySelector('[data-valmsg-for="' + field.name.replace(/"/g, '\\"') + '"]');
        if (!message) return;
        if (!message.id) message.id = (field.id || field.name) + '-error';
        field.setAttribute('aria-describedby', message.id);
    }

    function applyServerErrors() {
        document.querySelectorAll('.input-validation-error').forEach(markInvalid);
        var banner = document.querySelector('main .alert-danger[role="alert"], .card .alert-danger[role="alert"]');
        if (!banner || document.querySelector('[autofocus]')) return;
        banner.setAttribute('tabindex', '-1');
        banner.focus();
    }

    document.addEventListener('invalid', function (e) {
        if (e.target && e.target.setAttribute) e.target.setAttribute('aria-invalid', 'true');
    }, true);

    document.addEventListener('input', function (e) {
        if (e.target && e.target.removeAttribute) e.target.removeAttribute('aria-invalid');
    });

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', applyServerErrors);
    else applyServerErrors();
})();

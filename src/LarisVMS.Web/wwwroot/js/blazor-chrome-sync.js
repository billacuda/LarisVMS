// Keeps layout "chrome" — the sidebar's active-nav highlight and the light/dark theme — correct
// across Blazor's enhanced navigation. Static SSR always computes both correctly, fresh, server-side
// on every request, but confirmed live: enhanced navigation's client-side DOM patching doesn't
// reliably apply that diff to markup living outside the routed page's own content region — MainLayout's
// sidebar and App.razor's <html> tag both count as "outside". Re-applying both from plain client-side
// JS, keyed off the current URL/localStorage rather than whatever the last real page load happened to
// render, sidesteps that gap entirely.
//
// Hooked to Blazor's own 'enhancedload' event (fires after every enhanced navigation's DOM update),
// not to script re-execution — a script tag's own placement in the document determines whether/when
// it re-runs on enhanced nav, which isn't reliable for anything living outside a page's own
// SectionContent-declared scripts (see dashboard.js). This event fires regardless of where the
// listener was registered, so it's the correct, documented mechanism for "run this after every
// client-side navigation."
(function () {
    'use strict';

    function applyActiveNav() {
        var path = location.pathname;
        document.querySelectorAll('.lv-navitem').forEach(function (link) {
            var href = link.getAttribute('href');
            if (!href) return;
            var isActive = href === '/' ? path === '/' : path.indexOf(href) === 0;
            link.classList.toggle('active', isActive);
        });
    }

    // location.pathname can lag behind the DOM update that fires 'enhancedload' by more than one
    // tick — confirmed live that a single setTimeout(0) wasn't always enough (worse, and later,
    // for a navigation landing on a non-Blazor page like /Live than for a Blazor-to-Blazor one).
    // Rather than guess one exact delay, poll a few times a short interval apart until it settles —
    // cheap, self-correcting regardless of which navigation kind is involved.
    function applyActiveNavSettled(attemptsLeft) {
        applyActiveNav();
        if (attemptsLeft > 0) setTimeout(function () { applyActiveNavSettled(attemptsLeft - 1); }, 50);
    }

    function sync() {
        // Theme reapply doesn't depend on the URL, so it's safe to run immediately here.
        if (window.larisvmsTheme) window.larisvmsTheme.reapply();
        applyActiveNavSettled(6);
    }

    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', sync);
    }
})();

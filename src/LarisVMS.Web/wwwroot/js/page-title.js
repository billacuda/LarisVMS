// Shows each page's title in the top bar (#lvPageTitle) instead of at the top of the page: the first
// <h1> inside .lv-content is moved there as-is — the element itself, so anything inside it with an id
// (the Snapshots status badge) keeps working. A page with no <h1> (Live, Playback, …) gets the page
// name from document.title ("Live - LarisVMS" → "Live").
//
// Runs once straight away (the script sits right after the page content in both layouts), and again
// after every Blazor enhanced navigation — that swaps the content without a page load, and doesn't
// reliably update markup outside the routed content, which the top bar is (see blazor-chrome-sync.js).
(function () {
    'use strict';

    function apply() {
        var slot = document.getElementById('lvPageTitle');
        if (!slot) return;

        var heading = document.querySelector('.lv-content h1');
        slot.textContent = '';
        if (heading) {
            heading.classList.add('lv-page-heading');
            slot.appendChild(heading);
            return;
        }
        var title = document.title || '';
        var cut = title.lastIndexOf(' - ');
        slot.textContent = cut > 0 ? title.substring(0, cut) : title;
    }

    apply();

    // blazor.web.js loads after this script, so hook its event once everything has loaded. On a
    // Razor Pages page there is no Blazor and nothing else to do.
    window.addEventListener('load', function () {
        if (window.Blazor && window.Blazor.addEventListener) {
            window.Blazor.addEventListener('enhancedload', apply);
        }
    });
})();

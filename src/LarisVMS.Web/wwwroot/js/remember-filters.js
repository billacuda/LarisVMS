// Remembers a server-rendered filter form's selections across navigation and refreshes, for pages
// whose entire state lives in the query string (Snapshots, Audit Logs) rather than in JS the way
// Playback's own timeline position/zoom/view already persist through larisvmsPreferences directly.
//
// Opt in by marking the form: <form method="get" data-remember-filters="snapshots">. The key names
// the preference slot, so two pages never share one; it is not derived from the URL, which would
// silently change meaning if a page ever moved.
//
// How it works, and why this shape:
//   - Arriving *with* a query string (a Filter submit, a pagination link, a bookmarked/shared URL)
//     saves that query. The URL is always the source of truth — this only records what the page was
//     already told to show, and never overrides an explicit one.
//   - Arriving with *no* query string (clicking Snapshots in the nav, or a plain refresh of the bare
//     page) restores the saved query, if there is one. location.replace, not assign: the bare URL was
//     a way-station the user never chose to be on, so it has no business sitting in history where
//     Back would land on it and immediately bounce forward again.
//   - "Clear" wipes the saved query rather than being immediately undone by it. Mark that link
//     data-clear-filters; without this the restore above would fire on the very next load and Clear
//     would appear to do nothing at all.
//
// Server-backed via larisvmsPreferences, so filters follow the user across devices like every other
// preference — not localStorage, which is per-browser and was exactly what that module replaced.
(function () {
    'use strict';

    var PREF_PREFIX = 'filters.';

    function init() {
        var form = document.querySelector('form[data-remember-filters]');
        var key = form && form.dataset.rememberFilters;
        if (!key) return;
        var prefKey = PREF_PREFIX + key;

        // Wired before the preferences fetch resolves, deliberately: a Clear click must take effect
        // even if the user gets there faster than the round trip. set() updates the local cache
        // immediately and the PUT catches up on its own.
        document.querySelectorAll('[data-clear-filters]').forEach(function (el) {
            el.addEventListener('click', function () {
                window.larisvmsPreferences.set(prefKey, '');
            });
        });

        window.larisvmsPreferences.whenReady().then(function () {
            var search = window.location.search;
            if (search && search !== '?') {
                window.larisvmsPreferences.set(prefKey, search);
                return;
            }

            var saved = window.larisvmsPreferences.get(prefKey, '');
            // The '?' guard is what stops a redirect loop: restoring an empty (or bare-'?') query
            // would land back on this same branch and try again forever.
            if (!saved || saved === '?') return;
            window.location.replace(window.location.pathname + saved);
        });
    }

    // Classic Razor Pages (Audit Logs, and any hard load) fires DOMContentLoaded once per real
    // navigation — sufficient there. Blazor enhanced navigation (Snapshots) never fires it again
    // after the first load, so also hook 'enhancedload' (fires after every enhanced-nav DOM
    // update, including the very first load — see blazor-chrome-sync.js for the same mechanism).
    // init() running twice on a Snapshots hard load is harmless: the restore branch calls
    // location.replace with the same target both times (duplicate no-op navigation), and the save
    // branch PUTs the same value twice (the preferences endpoint is a plain upsert).
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();

    // Guarded because window.Blazor doesn't exist yet at the point this IIFE runs today — this
    // script's <script> tag must load after blazor.web.js (see App.razor) for this branch to ever
    // fire. No-op (as intended) on _Layout.cshtml's classic Razor Pages, which never loads
    // blazor.web.js at all.
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', init);
    }
})();

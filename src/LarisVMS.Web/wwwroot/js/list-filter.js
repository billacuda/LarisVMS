// Client-side filter convention for any list page: an <input data-filter-table="tableId"> hides
// tbody rows whose text doesn't contain the typed term (case-insensitive substring across the
// whole row), same "no server round-trip, list sizes are small" reasoning as sortable-table.js.
//
// The typed term is remembered per table through larisvmsPreferences, the same server-backed store
// this table's page size (table-pagination.js) and column choices (column-picker.js) already use —
// so a list page comes back the way it was left, rather than every visit starting from an unfiltered
// list while the two neighbouring controls in the same toolbar remembered themselves.
(function () {
    'use strict';

    var PREF_PREFIX = 'tableFilter.';

    function applyFilter(input) {
        var table = document.getElementById(input.getAttribute('data-filter-table'));
        if (!table || !table.tBodies[0]) return;

        var term = input.value.trim().toLowerCase();
        Array.prototype.forEach.call(table.tBodies[0].rows, function (row) {
            var matches = term === '' || (row.textContent || '').toLowerCase().indexOf(term) !== -1;
            row.style.display = matches ? '' : 'none';
        });
        // The only coupling to table-pagination.js: lets pagination re-slice against the rows this
        // filter just changed (paginating the post-filter set, not the full table) without either
        // module reaching into the other's internals.
        document.dispatchEvent(new CustomEvent('larisvms:table-changed', { detail: { tableId: table.id } }));
    }

    document.addEventListener('DOMContentLoaded', function () {
        var inputs = Array.prototype.slice.call(document.querySelectorAll('input[data-filter-table]'));
        inputs.forEach(function (input) {
            var prefKey = PREF_PREFIX + input.getAttribute('data-filter-table');
            input.addEventListener('input', function () {
                applyFilter(input);
                window.larisvmsPreferences.set(prefKey, input.value);
            });
        });

        // Restoring waits for the preferences fetch, same as table-pagination.js's own first render.
        // A term typed before it resolves wins: overwriting what someone is actively typing would be
        // worse than losing a stale saved value, and the input handler above has already saved it.
        window.larisvmsPreferences.whenReady().then(function () {
            inputs.forEach(function (input) {
                if (input.value) return;
                var saved = window.larisvmsPreferences.get(PREF_PREFIX + input.getAttribute('data-filter-table'), '');
                if (!saved) return;
                input.value = saved;
                applyFilter(input);
            });
        });
    });
})();

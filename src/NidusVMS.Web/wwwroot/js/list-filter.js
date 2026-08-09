// Client-side filter convention for any list page: an <input data-filter-table="tableId"> hides
// tbody rows whose text doesn't contain the typed term (case-insensitive substring across the
// whole row), same "no server round-trip, list sizes are small" reasoning as sortable-table.js.
(function () {
    'use strict';

    function applyFilter(input) {
        var table = document.getElementById(input.getAttribute('data-filter-table'));
        if (!table || !table.tBodies[0]) return;

        var term = input.value.trim().toLowerCase();
        Array.prototype.forEach.call(table.tBodies[0].rows, function (row) {
            var matches = term === '' || (row.textContent || '').toLowerCase().indexOf(term) !== -1;
            row.style.display = matches ? '' : 'none';
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.forEach.call(document.querySelectorAll('input[data-filter-table]'), function (input) {
            input.addEventListener('input', function () { applyFilter(input); });
        });
    });
})();

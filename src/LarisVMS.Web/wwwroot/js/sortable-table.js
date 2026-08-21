// Click-to-sort convention for any <table data-sortable>. Client-side re-sort of the rendered
// rows — no server round-trip — since every list this app deals with fits comfortably in one
// page. A header cell opts out with data-no-sort (action columns with no meaningful order), and
// opts into numeric comparison with data-sort-numeric. An individual <td> can override what gets
// compared via data-sort-value (e.g. raw bytes/epoch instead of a formatted display string).
//
// The chosen column and direction are remembered per table through larisvmsPreferences, the same
// server-backed store this table's page size, column choices, and filter term already use — a list
// page comes back sorted the way it was left.
(function () {
    'use strict';

    var PREF_PREFIX = 'tableSort.';

    function cellSortValue(cell) {
        if (!cell) return '';
        var explicit = cell.getAttribute('data-sort-value');
        return (explicit !== null ? explicit : cell.textContent || '').trim();
    }

    function compare(a, b, numeric) {
        if (numeric) {
            var an = parseFloat(a), bn = parseFloat(b);
            var aValid = !isNaN(an), bValid = !isNaN(bn);
            if (!aValid && !bValid) return 0;
            if (!aValid) return 1; // blanks/non-numeric sort last regardless of direction
            if (!bValid) return -1;
            return an - bn;
        }
        return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
    }

    function sortRows(table, columnIndex, numeric, ascending) {
        var tbody = table.tBodies[0];
        if (!tbody) return;
        var rows = Array.prototype.slice.call(tbody.rows);
        rows.sort(function (r1, r2) {
            var result = compare(cellSortValue(r1.cells[columnIndex]), cellSortValue(r2.cells[columnIndex]), numeric);
            return ascending ? result : -result;
        });
        rows.forEach(function (row) { tbody.appendChild(row); });
    }

    function initTable(table) {
        if (!table.tHead || !table.tHead.rows.length) return;
        var headers = Array.prototype.slice.call(table.tHead.rows[0].cells);
        var prefKey = PREF_PREFIX + table.id;

        // Extracted from the click handler so a restored sort produces exactly the same DOM state a
        // click would have — indicator, data-sort-dir, row order — rather than a second, subtly
        // different code path that reapply() and table-pagination.js would then disagree about.
        function applySort(th, columnIndex, ascending) {
            headers.forEach(function (h) {
                h.removeAttribute('data-sort-dir');
                var existingIndicator = h.querySelector('.sort-indicator');
                if (existingIndicator) existingIndicator.remove();
            });

            th.setAttribute('data-sort-dir', ascending ? 'asc' : 'desc');
            var indicator = document.createElement('span');
            indicator.className = 'sort-indicator ms-1';
            indicator.textContent = ascending ? '▲' : '▼';
            th.appendChild(indicator);

            sortRows(table, columnIndex, th.hasAttribute('data-sort-numeric'), ascending);
            // Same coupling as list-filter.js's own dispatch — lets table-pagination.js re-slice
            // against the now-reordered rows instead of the pre-sort order.
            document.dispatchEvent(new CustomEvent('larisvms:table-changed', { detail: { tableId: table.id } }));
        }

        headers.forEach(function (th, columnIndex) {
            if (th.hasAttribute('data-no-sort')) return;

            th.classList.add('sortable-header');
            th.style.cursor = 'pointer';
            th.style.userSelect = 'none';

            th.addEventListener('click', function () {
                var ascending = th.getAttribute('data-sort-dir') !== 'asc';
                applySort(th, columnIndex, ascending);
                if (table.id) window.larisvmsPreferences.set(prefKey, columnIndex + ':' + (ascending ? 'asc' : 'desc'));
            });
        });

        if (!table.id) return; // nothing to key a preference on
        window.larisvmsPreferences.whenReady().then(function () {
            var saved = window.larisvmsPreferences.get(prefKey, '');
            if (!saved) return;
            var parts = String(saved).split(':');
            var columnIndex = parseInt(parts[0], 10);
            // Column count/order can change between releases, and a header can gain data-no-sort —
            // a saved index that no longer names a sortable column is dropped rather than applied to
            // whatever now happens to sit at that position.
            var th = headers[columnIndex];
            if (!th || th.hasAttribute('data-no-sort')) return;
            applySort(th, columnIndex, parts[1] !== 'desc');
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.forEach.call(document.querySelectorAll('table[data-sortable]'), initTable);
    });

    // For a table whose rows get replaced wholesale by something other than a user click here (e.g.
    // dashboard.js's 60s AJAX refresh) — re-applies whichever column is currently marked
    // data-sort-dir (set by the click handler above) against the table's current rows. A plain DOM
    // attribute read, not a stored instance, so it works even for a table not present when this
    // module's own DOMContentLoaded ran. No-op if the table has never been sorted (rows stay in
    // whatever order the caller just put them in).
    function reapply(tableId) {
        var table = document.getElementById(tableId);
        if (!table || !table.tHead || !table.tHead.rows.length) return;
        var headers = Array.prototype.slice.call(table.tHead.rows[0].cells);
        for (var i = 0; i < headers.length; i++) {
            var dir = headers[i].getAttribute('data-sort-dir');
            if (dir) {
                sortRows(table, i, headers[i].hasAttribute('data-sort-numeric'), dir === 'asc');
                return;
            }
        }
    }

    window.larisvmsSortableTable = { reapply: reapply };
})();

// Click-to-sort convention for any <table data-sortable>. Client-side re-sort of the rendered
// rows — no server round-trip — since every list this app deals with fits comfortably in one
// page. A header cell opts out with data-no-sort (action columns with no meaningful order), and
// opts into numeric comparison with data-sort-numeric. An individual <td> can override what gets
// compared via data-sort-value (e.g. raw bytes/epoch instead of a formatted display string).
(function () {
    'use strict';

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

        headers.forEach(function (th, columnIndex) {
            if (th.hasAttribute('data-no-sort')) return;

            th.classList.add('sortable-header');
            th.style.cursor = 'pointer';
            th.style.userSelect = 'none';

            th.addEventListener('click', function () {
                var ascending = th.getAttribute('data-sort-dir') !== 'asc';

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
                document.dispatchEvent(new CustomEvent('nidusvms:table-changed', { detail: { tableId: table.id } }));
            });
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.forEach.call(document.querySelectorAll('table[data-sortable]'), initTable);
    });
})();

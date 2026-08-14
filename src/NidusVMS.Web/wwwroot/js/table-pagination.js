// Client-side pagination layered on top of list-filter.js's filtering and sortable-table.js's
// sorting — opts in per table via <table data-paginate>. Composes with those two purely through
// the 'nidusvms:table-changed' event they each dispatch after changing which rows are visible or
// what order they're in; this file never reaches into their internals, and they don't know this
// file exists. Page-size <select data-paginate-size-for="tableId"> and a pager container
// <div data-paginate-controls-for="tableId"> are expected to already be in the page's own markup,
// same convention as list-filter.js's own data-filter-table input.
(function () {
    'use strict';

    var PAGE_SIZE_OPTIONS = [10, 25, 50, 100];
    var DEFAULT_PAGE_SIZE = 25;
    var STORAGE_PREFIX = 'nidusvms.tablePageSize.';

    function loadPageSize(tableId) {
        try {
            var raw = localStorage.getItem(STORAGE_PREFIX + tableId);
            if (raw === 'all') return 'all';
            var n = parseInt(raw, 10);
            if (PAGE_SIZE_OPTIONS.indexOf(n) !== -1) return n;
        } catch (e) { /* private browsing/storage full — fall back to the default */ }
        return DEFAULT_PAGE_SIZE;
    }

    function savePageSize(tableId, size) {
        try { localStorage.setItem(STORAGE_PREFIX + tableId, String(size)); } catch (e) { /* ignore */ }
    }

    function initTable(table) {
        var tableId = table.id;
        if (!tableId || !table.tBodies[0]) return;

        var sizeSelect = document.querySelector('[data-paginate-size-for="' + tableId + '"]');
        var controlsEl = document.querySelector('[data-paginate-controls-for="' + tableId + '"]');
        var pageSize = loadPageSize(tableId);
        var currentPage = 1;

        if (sizeSelect) sizeSelect.value = String(pageSize);

        // Rows this module itself hid for being off the current page carry this marker so a later
        // re-render (from a filter/sort change, or a page/page-size change) can tell "hidden because
        // off-page" apart from "hidden because filter says it doesn't match" — both use the same
        // row.style.display = 'none', but only the filter's own opinion should survive a re-slice.
        // Un-hiding our own marked rows first, before reading which rows currently show, is what
        // makes the two compose correctly regardless of whether the triggering event came from a
        // filter change (which unconditionally re-sets every row's display itself) or a sort
        // (which reorders rows but never touches display at all, leaving our own prior hides intact).
        function unhideOwnRows(tbody) {
            Array.prototype.forEach.call(tbody.rows, function (row) {
                if (row.dataset.pgHidden === '1') {
                    row.style.display = '';
                    delete row.dataset.pgHidden;
                }
            });
        }

        function render() {
            var tbody = table.tBodies[0];
            unhideOwnRows(tbody);

            var matching = Array.prototype.filter.call(tbody.rows, function (row) {
                return row.style.display !== 'none';
            });
            var total = matching.length;
            var effectiveSize = pageSize === 'all' ? Math.max(1, total) : pageSize;
            var totalPages = Math.max(1, Math.ceil(total / effectiveSize));
            if (currentPage > totalPages) currentPage = totalPages;
            if (currentPage < 1) currentPage = 1;

            if (pageSize !== 'all') {
                var startIdx = (currentPage - 1) * effectiveSize;
                var endIdx = startIdx + effectiveSize;
                matching.forEach(function (row, idx) {
                    if (idx < startIdx || idx >= endIdx) {
                        row.style.display = 'none';
                        row.dataset.pgHidden = '1';
                    }
                });
            }

            renderControls(total, totalPages);
        }

        function renderControls(total, totalPages) {
            if (!controlsEl) return;
            controlsEl.innerHTML = '';
            if (total === 0) return;

            var wrap = document.createElement('div');
            wrap.className = 'd-flex align-items-center gap-2 small';

            var prevBtn = document.createElement('button');
            prevBtn.type = 'button';
            prevBtn.className = 'btn btn-sm btn-outline-secondary';
            prevBtn.textContent = '‹ Prev';
            prevBtn.disabled = currentPage <= 1;
            prevBtn.addEventListener('click', function () { currentPage--; render(); });

            var info = document.createElement('span');
            info.className = 'text-muted';
            info.textContent = 'Page ' + currentPage + ' of ' + totalPages + ' (' + total + (total === 1 ? ' row' : ' rows') + ')';

            var nextBtn = document.createElement('button');
            nextBtn.type = 'button';
            nextBtn.className = 'btn btn-sm btn-outline-secondary';
            nextBtn.textContent = 'Next ›';
            nextBtn.disabled = currentPage >= totalPages;
            nextBtn.addEventListener('click', function () { currentPage++; render(); });

            wrap.appendChild(prevBtn);
            wrap.appendChild(info);
            wrap.appendChild(nextBtn);
            controlsEl.appendChild(wrap);
        }

        if (sizeSelect) {
            sizeSelect.addEventListener('change', function () {
                pageSize = sizeSelect.value === 'all' ? 'all' : parseInt(sizeSelect.value, 10);
                savePageSize(tableId, pageSize);
                currentPage = 1;
                render();
            });
        }

        document.addEventListener('nidusvms:table-changed', function (e) {
            if (e.detail && e.detail.tableId === tableId) {
                currentPage = 1;
                render();
            }
        });

        render();
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.forEach.call(document.querySelectorAll('table[data-paginate]'), initTable);
    });
})();

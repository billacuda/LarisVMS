// Client-side pagination layered on top of list-filter.js's filtering and sortable-table.js's
// sorting — opts in per table via <table data-paginate>. Composes with those two purely through
// the 'larisvms:table-changed' event they each dispatch after changing which rows are visible or
// what order they're in; this file never reaches into their internals, and they don't know this
// file exists. Page-size <select data-paginate-size-for="tableId"> and a pager container
// <div data-paginate-controls-for="tableId"> are expected to already be in the page's own markup,
// same convention as list-filter.js's own data-filter-table input.
(function () {
    'use strict';

    var FALLBACK_PAGE_SIZE = 25; // only used if a table somehow has no size <select> of its own
    var PREF_PREFIX = 'tablePageSize.';

    // instances[tableId] -> that table's own render(), so a caller whose data changed for a reason
    // other than a user sort/filter/page-size action (dashboard.js's 60s AJAX refresh) can re-slice
    // against the fresh rows via refresh() below without resetting currentPage back to 1 the way the
    // 'larisvms:table-changed' listener below deliberately does for real user actions.
    var instances = {};

    // Each page's own markup — via <option selected> — decides its own default, not a single
    // hardcoded value shared across every paginated table site-wide: Cameras/Index defaults to 25,
    // Dashboard to 20, simply by which <option> each page marks selected.
    function defaultPageSize(sizeSelect) {
        if (!sizeSelect) return FALLBACK_PAGE_SIZE;
        if (sizeSelect.value === 'all') return 'all';
        var n = parseInt(sizeSelect.value, 10);
        return isNaN(n) ? FALLBACK_PAGE_SIZE : n;
    }

    // Validated against sizeSelect's own <option> values rather than a hardcoded list — different
    // pages offer different size sets (e.g. Cameras/Index's 10/25/50/100 vs. Dashboard's
    // 10/20/50/100), and a persisted value valid for one page but not this one should fall back to
    // this page's own default rather than silently accepting a size this page never offered.
    //
    // Server-backed (see user-preferences.js) — initTable() itself waits for the preferences fetch
    // to resolve before its first render, so this read (called from inside that wait) already sees
    // real data rather than needing its own fallback-then-flash handling the way dashboard.js's
    // thumbnail toggle does.
    function loadPageSize(tableId, sizeSelect) {
        var fallback = defaultPageSize(sizeSelect);
        var raw = window.larisvmsPreferences.get(PREF_PREFIX + tableId, null);
        if (raw === null) return fallback;
        if (raw === 'all') return 'all';
        var n = parseInt(raw, 10);
        var validSizes = sizeSelect
            ? Array.prototype.map.call(sizeSelect.options, function (o) { return o.value; })
            : [];
        return validSizes.indexOf(String(n)) !== -1 ? n : fallback;
    }

    function savePageSize(tableId, size) {
        window.larisvmsPreferences.set(PREF_PREFIX + tableId, size);
    }

    function initTable(table) {
        var tableId = table.id;
        if (!tableId || !table.tBodies[0]) return;

        var sizeSelect = document.querySelector('[data-paginate-size-for="' + tableId + '"]');
        var controlsEl = document.querySelector('[data-paginate-controls-for="' + tableId + '"]');
        var pageSize = loadPageSize(tableId, sizeSelect);
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

        document.addEventListener('larisvms:table-changed', function (e) {
            if (e.detail && e.detail.tableId === tableId) {
                currentPage = 1;
                render();
            }
        });

        instances[tableId] = { render: render };
        render();
    }

    document.addEventListener('DOMContentLoaded', function () {
        // Waits for the preferences fetch so each table's first render already uses the right page
        // size rather than the default-then-correct flash a synchronous localStorage read used to
        // avoid entirely on its own — user-preferences.js's GET already started well before
        // DOMContentLoaded fires (its script tag loads earlier in _Layout.cshtml, before full-page
        // parsing even finishes), so this adds negligible delay in practice.
        window.larisvmsPreferences.whenReady().then(function () {
            Array.prototype.forEach.call(document.querySelectorAll('table[data-paginate]'), initTable);
        });
    });

    // Re-slices against the table's current rows using whatever page/page-size it's already on — see
    // the `instances` doc comment above for when to use this instead of the 'larisvms:table-changed'
    // event. No-op for a table this module never initialized (data-paginate missing, or not yet
    // DOMContentLoaded).
    function refresh(tableId) {
        var inst = instances[tableId];
        if (inst) inst.render();
    }

    window.larisvmsTablePagination = { refresh: refresh };
})();

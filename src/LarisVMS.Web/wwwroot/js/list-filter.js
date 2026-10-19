// Client-side filter convention for any list page: an <input data-filter-table="tableId"> hides
// tbody rows whose text doesn't contain the typed term (case-insensitive substring across the
// whole row), same "no server round-trip, list sizes are small" reasoning as sortable-table.js.
//
// Per-column filters, opt-in with <table data-column-filters>: every <th> carrying data-filter gets a
// compact control in a second header row this script adds:
//   data-filter="text"    a "contains" box
//   data-filter="select"  a dropdown of the column's distinct values, or of data-filter-options="a|b|c"
//                         when the th gives a fixed list (e.g. size buckets)
//   data-filter="date"    preset ranges (last 24 hours, last 7 days, older, never); the cell's
//                         data-filter-value is a UTC epoch-milliseconds timestamp, empty for never
// A cell's data-filter-value (values separated by "|") overrides its text — for emoji/badge cells and
// for cells holding several values (a camera in two groups matches either). The column's key is
// data-filter-key, falling back to data-col (the column picker's key).
//
// The search box and every column filter combine (AND) here, and this stays the only code that sets
// a row's display for filtering, so table-pagination.js keeps paginating the filtered set unchanged. A
// filter on a column the column picker has hidden is ignored, so rows are never filtered by something
// the user can't see.
//
// The typed term and the column filters are remembered per table through larisvmsPreferences, the
// same server-backed store this table's page size (table-pagination.js) and column choices
// (column-picker.js) already use — so a list page comes back the way it was left, on any device.
(function () {
    'use strict';

    var PREF_PREFIX = 'tableFilter.';
    var COLUMN_PREF_PREFIX = 'tableColumnFilters.';
    var NONE = '(none)';
    var DAY_MS = 24 * 60 * 60 * 1000;
    var DATE_PRESETS = [
        { value: '24h', label: 'Last 24 hours' },
        { value: '7d', label: 'Last 7 days' },
        { value: 'older7d', label: 'Older than 7 days' },
        { value: 'never', label: 'Never' }
    ];

    var tables = {}; // tableId -> { table, input, columns: [{ key, index, type, control }], filters: {} }

    function state(tableId) {
        var table = document.getElementById(tableId);
        if (!table) return null;
        if (!tables[tableId]) tables[tableId] = { table: table, input: null, columns: [], filters: {} };
        return tables[tableId];
    }

    function cellValues(cell) {
        if (!cell) return [];
        var raw = cell.getAttribute('data-filter-value');
        if (raw !== null) return raw === '' ? [] : raw.split('|');
        var text = (cell.textContent || '').trim();
        return [text === '' || text === '—' ? NONE : text];
    }

    function columnVisible(st, col) {
        var th = st.table.tHead.rows[0].cells[col.index];
        return th && window.getComputedStyle(th).display !== 'none';
    }

    function matchesColumn(row, col, value) {
        var cell = row.cells[col.index];
        if (col.type === 'text') {
            return (cell ? cell.textContent || '' : '').toLowerCase().indexOf(value.toLowerCase()) !== -1;
        }
        if (col.type === 'date') {
            var raw = cell ? cell.getAttribute('data-filter-value') : '';
            var ms = raw ? Number(raw) : NaN;
            var age = Date.now() - ms;
            if (value === 'never') return isNaN(ms);
            if (isNaN(ms)) return false;
            if (value === '24h') return age <= DAY_MS;
            if (value === '7d') return age <= 7 * DAY_MS;
            if (value === 'older7d') return age > 7 * DAY_MS;
            return true;
        }
        return cellValues(cell).some(function (v) { return v === value; });
    }

    function activeColumnFilters(st) {
        return st.columns.filter(function (c) { return st.filters[c.key] && columnVisible(st, c); });
    }

    function applyTable(tableId) {
        var st = state(tableId);
        if (!st || !st.table.tBodies[0]) return;

        var term = st.input ? st.input.value.trim().toLowerCase() : '';
        var active = activeColumnFilters(st);
        var rows = st.table.tBodies[0].rows;
        var shown = 0;
        Array.prototype.forEach.call(rows, function (row) {
            var matches = (term === '' || (row.textContent || '').toLowerCase().indexOf(term) !== -1)
                && active.every(function (c) { return matchesColumn(row, c, st.filters[c.key]); });
            // Clearing the pagination marker too: a row this filter hides isn't "off-page", and one it
            // reveals is re-sliced by table-pagination.js right after the event below.
            if (row.dataset.pgHidden === '1') delete row.dataset.pgHidden;
            row.style.display = matches ? '' : 'none';
            if (matches) shown++;
        });
        updateStatus(st, shown, rows.length, term !== '' || active.length > 0);
        // The only coupling to table-pagination.js: lets pagination re-slice against the rows this
        // filter just changed (paginating the post-filter set, not the full table) without either
        // module reaching into the other's internals.
        document.dispatchEvent(new CustomEvent('larisvms:table-changed', { detail: { tableId: tableId } }));
    }

    // Optional <span data-filter-status-for="tableId">: "12 of 40" plus a Clear filters link, shown
    // only while a filter is active.
    function updateStatus(st, shown, total, filtered) {
        var status = document.querySelector('[data-filter-status-for="' + st.table.id + '"]');
        if (!status) return;
        status.innerHTML = '';
        if (!filtered) return;
        status.appendChild(document.createTextNode(shown + ' of ' + total + ' ' + (status.getAttribute('data-filter-noun') || 'rows') + ' '));
        var clear = document.createElement('button');
        clear.type = 'button';
        clear.className = 'btn btn-link btn-sm p-0 align-baseline';
        clear.textContent = 'Clear filters';
        clear.addEventListener('click', function () { clearAll(st); });
        status.appendChild(clear);
    }

    function clearAll(st) {
        if (st.input) {
            st.input.value = '';
            window.larisvmsPreferences.set(PREF_PREFIX + st.table.id, '');
        }
        st.filters = {};
        st.columns.forEach(function (c) { c.control.value = ''; });
        saveColumnFilters(st);
        applyTable(st.table.id);
    }

    function saveColumnFilters(st) {
        var kept = {};
        Object.keys(st.filters).forEach(function (k) { if (st.filters[k]) kept[k] = st.filters[k]; });
        window.larisvmsPreferences.set(COLUMN_PREF_PREFIX + st.table.id, Object.keys(kept).length ? JSON.stringify(kept) : '');
    }

    function distinctValues(st, index) {
        var seen = {};
        Array.prototype.forEach.call(st.table.tBodies[0] ? st.table.tBodies[0].rows : [], function (row) {
            cellValues(row.cells[index]).forEach(function (v) { seen[v] = true; });
        });
        var values = Object.keys(seen);
        var numeric = values.every(function (v) { return v === NONE || !isNaN(parseFloat(v)); });
        values.sort(function (a, b) {
            if (a === NONE) return 1;
            if (b === NONE) return -1;
            return numeric ? parseFloat(a) - parseFloat(b) : a.localeCompare(b, undefined, { sensitivity: 'base', numeric: true });
        });
        return values;
    }

    function buildColumnFilters(st) {
        var headRow = st.table.tHead && st.table.tHead.rows[0];
        if (!headRow) return;
        var filterRow = document.createElement('tr');
        filterRow.className = 'lv-filter-row';

        Array.prototype.forEach.call(headRow.cells, function (th, index) {
            var cell = document.createElement('th');
            var type = th.getAttribute('data-filter');
            var key = th.getAttribute('data-filter-key') || th.getAttribute('data-col');
            filterRow.appendChild(cell);
            if (!type || !key) return;

            var label = th.getAttribute('data-col-label') || th.textContent.trim() || key;
            var control;
            if (type === 'text') {
                control = document.createElement('input');
                control.type = 'search';
                control.className = 'form-control form-control-sm';
                control.placeholder = 'Filter';
                control.addEventListener('input', function () { st.filters[key] = control.value.trim(); saveColumnFilters(st); applyTable(st.table.id); });
            } else {
                control = document.createElement('select');
                control.className = 'form-select form-select-sm';
                var options = type === 'date'
                    ? DATE_PRESETS
                    : (th.getAttribute('data-filter-options') ? th.getAttribute('data-filter-options').split('|') : distinctValues(st, index))
                        .map(function (v) { return { value: v, label: v }; });
                control.appendChild(new Option('All', ''));
                options.forEach(function (o) { control.appendChild(new Option(o.label, o.value)); });
                control.addEventListener('change', function () { st.filters[key] = control.value; saveColumnFilters(st); applyTable(st.table.id); });
            }
            control.setAttribute('aria-label', 'Filter by ' + label);
            cell.appendChild(control);
            st.columns.push({ key: key, index: index, type: type, control: control });
        });

        st.table.tHead.appendChild(filterRow);
    }

    function restoreColumnFilters(st) {
        var saved = {};
        try { saved = JSON.parse(window.larisvmsPreferences.get(COLUMN_PREF_PREFIX + st.table.id, '') || '{}') || {}; }
        catch (e) { saved = {}; }
        st.columns.forEach(function (c) {
            var v = saved[c.key];
            if (!v) return;
            // A saved dropdown value that no longer exists (e.g. a node since renamed) is dropped
            // rather than silently filtering everything out.
            if (c.control.tagName === 'SELECT' && !Array.prototype.some.call(c.control.options, function (o) { return o.value === v; })) return;
            c.control.value = v;
            st.filters[c.key] = v;
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        var inputs = Array.prototype.slice.call(document.querySelectorAll('input[data-filter-table]'));
        inputs.forEach(function (input) {
            var tableId = input.getAttribute('data-filter-table');
            var st = state(tableId);
            if (!st) return;
            st.input = input;
            input.addEventListener('input', function () {
                applyTable(tableId);
                window.larisvmsPreferences.set(PREF_PREFIX + tableId, input.value);
            });
        });

        var filterTables = Array.prototype.slice.call(document.querySelectorAll('table[data-column-filters]'));
        filterTables.forEach(function (table) { if (table.id) buildColumnFilters(state(table.id)); });

        // A column hidden or shown in the column picker changes which column filters count.
        document.addEventListener('larisvms:columns-changed', function (e) {
            if (e.detail && tables[e.detail.tableId]) applyTable(e.detail.tableId);
        });

        // Restoring waits for the preferences fetch, same as table-pagination.js's own first render.
        // A term typed before it resolves wins: overwriting what someone is actively typing would be
        // worse than losing a stale saved value, and the input handler above has already saved it.
        window.larisvmsPreferences.whenReady().then(function () {
            Object.keys(tables).forEach(function (tableId) {
                var st = tables[tableId];
                var changed = false;
                if (st.input && !st.input.value) {
                    var saved = window.larisvmsPreferences.get(PREF_PREFIX + tableId, '');
                    if (saved) { st.input.value = saved; changed = true; }
                }
                if (st.columns.length) {
                    restoreColumnFilters(st);
                    changed = changed || Object.keys(st.filters).length > 0;
                }
                if (changed) applyTable(tableId);
            });
        });
    });
})();

// Per-user column visibility, opt-in per table via <table data-column-picker> plus a
// data-col="<key>" attribute on every <th> that should be toggleable — a <th> with no data-col
// (a row-select checkbox, the primary name/link column, an actions column) is never offered and
// always shows. The toggle UI itself is a <details data-column-picker-for="tableId"> element already
// present in the page's own markup; this file only fills in its menu and reacts to checkbox changes,
// it never builds the toggle button itself.
//
// Columns are hidden by injected CSS (nth-child, scoped to this one table's id) rather than by
// touching every row's own <td> elements directly — the same reason table-pagination.js hides
// off-page rows via a data attribute rather than removing them: it composes for free with every
// other thing that redraws this table's rows (sortable-table.js's reorder, table-pagination.js's
// slice, a page's own AJAX refresh like dashboard.js's), since none of them need to know a column
// picker exists.
(function () {
    'use strict';

    function initTable(table) {
        var tableId = table.id;
        var headerRow = table.tHead && table.tHead.rows[0];
        var toggle = tableId && document.querySelector('[data-column-picker-for="' + tableId + '"]');
        if (!tableId || !headerRow || !toggle) return;

        var menu = toggle.querySelector('.column-picker-menu');
        if (!menu) return;

        // 1-based to match nth-child's own counting.
        var columns = [];
        Array.prototype.forEach.call(headerRow.cells, function (th, idx) {
            var key = th.getAttribute('data-col');
            if (key) columns.push({ key: key, label: th.getAttribute('data-col-label') || th.textContent.trim(), index: idx + 1 });
        });
        if (columns.length === 0) return;

        var prefKey = 'columns.' + tableId;
        var styleEl = document.createElement('style');
        document.head.appendChild(styleEl);

        function loadHidden() {
            var raw = window.larisvmsPreferences.get(prefKey, null);
            if (!raw) return [];
            try {
                var parsed = JSON.parse(raw);
                return Array.isArray(parsed) ? parsed : [];
            } catch (e) { return []; }
        }

        function applyHidden(hidden) {
            styleEl.textContent = columns
                .filter(function (c) { return hidden.indexOf(c.key) !== -1; })
                .map(function (c) {
                    // > thead > tr > *  /  > tbody > tr > * (not "th, td" specifically) so this
                    // still hides a <td> that happens to be a <th> (a row header cell), matching
                    // whichever tag the markup actually used at that position.
                    return '#' + tableId + ' > thead > tr > :nth-child(' + c.index + '),\n' +
                           '#' + tableId + ' > tbody > tr > :nth-child(' + c.index + ') { display: none; }';
                })
                .join('\n');
        }

        menu.innerHTML = '';
        columns.forEach(function (c) {
            var wrap = document.createElement('div');
            wrap.className = 'form-check';
            var input = document.createElement('input');
            input.type = 'checkbox';
            input.className = 'form-check-input';
            input.id = tableId + '-col-' + c.key;
            var label = document.createElement('label');
            label.className = 'form-check-label small';
            label.setAttribute('for', input.id);
            label.textContent = c.label;
            wrap.appendChild(input);
            wrap.appendChild(label);
            menu.appendChild(wrap);

            input.addEventListener('change', function () {
                var hidden = loadHidden().filter(function (k) { return k !== c.key; });
                if (!input.checked) hidden.push(c.key);
                window.larisvmsPreferences.set(prefKey, JSON.stringify(hidden));
                applyHidden(hidden);
            });
        });

        // <details> closes on an outside click natively in most browsers via its own toggle
        // behavior only for the <summary>; a click anywhere else on the page doesn't close it on
        // its own, so this closes it explicitly — otherwise the panel stays open indefinitely once
        // opened, covering whatever's beneath it.
        document.addEventListener('click', function (e) {
            if (toggle.open && !toggle.contains(e.target)) toggle.open = false;
        });

        window.larisvmsPreferences.whenReady().then(function () {
            var hidden = loadHidden();
            Array.prototype.forEach.call(menu.querySelectorAll('input[type="checkbox"]'), function (input, idx) {
                input.checked = hidden.indexOf(columns[idx].key) === -1;
            });
            applyHidden(hidden);
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        Array.prototype.forEach.call(document.querySelectorAll('table[data-column-picker]'), initTable);
    });
})();

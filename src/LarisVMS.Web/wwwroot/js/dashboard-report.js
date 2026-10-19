// Dashboard report popup (Pages/Index): the full list behind a widget's title. Pages through
// GET /api/dashboard/widgets/{key}/report 100 rows at a time, with a text filter, a node filter and
// sortable Name/Node/Value columns; "Download CSV" fetches every page of the current filter. A report
// can instead declare its own columns (data.columns, e.g. the speed log), filled from each row's cells.
// dashboard-widgets.js opens it through window.larisvmsDashboardReport.open(def, options).
window.larisvmsDashboardReport = (function () {
    'use strict';

    var PAGE_SIZE = 100;
    var CSV_MAX_PAGES = 200;
    var FILTER_DELAY_MS = 300;

    var modalEl = document.getElementById('dashboardReportModal');
    if (!modalEl) return { open: function () { } };

    var titleEl = document.getElementById('dashboardReportTitle');
    var filterEl = document.getElementById('dashboardReportFilter');
    var nodeEl = document.getElementById('dashboardReportNode');
    var countEl = document.getElementById('dashboardReportCount');
    var bodyEl = document.getElementById('dashboardReportBody');
    var pageEl = document.getElementById('dashboardReportPage');
    var prevEl = document.getElementById('dashboardReportPrev');
    var nextEl = document.getElementById('dashboardReportNext');
    var csvEl = document.getElementById('dashboardReportCsv');
    var valueHeaderEl = document.getElementById('dashboardReportValueHeader');
    var nodeColEl = modalEl.querySelector('[data-report-node-col]');
    var headEl = document.getElementById('dashboardReportHead');
    var standardHead = headEl ? headEl.innerHTML : '';
    var filterPlaceholder = filterEl.getAttribute('placeholder');

    var state = null;      // { def, options, page, q, nodeId, sort, desc }
    var lastPage = null;   // most recent response
    var requestSeq = 0;    // drops responses that arrive after a newer request was made
    var filterTimer = null;

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    (function fillNodes() {
        var configEl = document.getElementById('dashboardConfig');
        if (!configEl || !nodeEl) return;
        try {
            (JSON.parse(configEl.textContent || '{}').nodes || []).forEach(function (n) {
                var opt = document.createElement('option');
                opt.value = n.id;
                opt.textContent = n.name;
                nodeEl.appendChild(opt);
            });
        } catch (e) { /* no node filter options; "All nodes" still works */ }
    })();

    function url(page) {
        var params = new URLSearchParams();
        params.set('page', page);
        params.set('pageSize', PAGE_SIZE);
        if (state.q) params.set('q', state.q);
        if (state.nodeId) params.set('nodeId', state.nodeId);
        if (state.sort) { params.set('sort', state.sort); params.set('dir', state.desc ? 'desc' : 'asc'); }
        if (state.options.window) params.set('window', state.options.window);
        if (state.options.asc) params.set('asc', 'true');
        if (state.options.kinds) params.set('kinds', state.options.kinds.join(','));
        return '/api/dashboard/widgets/' + encodeURIComponent(state.def.key) + '/report?' + params.toString();
    }

    function fetchPage(page) {
        return fetch(url(page)).then(function (resp) {
            return resp.ok ? resp.json() : Promise.reject(new Error('HTTP ' + resp.status));
        });
    }

    // "{time}" in a note is noteTimeUtc, shown in the viewer's local time.
    function noteText(row) {
        return row.noteTimeUtc ? String(row.note).replace('{time}', window.larisvmsTime.format(row.noteTimeUtc)) : row.note;
    }

    function details(row) {
        var parts = [];
        if (row.note) parts.push(escapeHtml(noteText(row)));
        var badge = row.badge
            ? '<span class="badge ' + escapeHtml(row.badgeClass || 'text-bg-secondary') + ' me-1">' + escapeHtml(row.badge) + '</span>'
            : '';
        return badge + parts.join('');
    }

    function sortButton(label, key) {
        var arrow = state.sort === key ? (state.desc ? ' ▾' : ' ▴') : '';
        return '<button type="button" class="btn btn-link btn-sm p-0 text-reset fw-semibold text-decoration-none" data-report-sort="'
            + escapeHtml(key) + '">' + escapeHtml(label) + arrow + '</button>';
    }

    function cellHtml(col, value) {
        if (value == null || value === '') return '';
        if (col.type === 'time') return escapeHtml(window.larisvmsTime.format(value));
        return escapeHtml(value);
    }

    // A report with its own columns (see DashboardReportColumn): header, cells and alignment come
    // from data.columns; CSV-only columns are left out of the table.
    function renderColumns(data) {
        var cols = data.columns.filter(function (c) { return !c.csvOnly; });
        headEl.innerHTML = '<tr>' + cols.map(function (c) {
            var align = c.type === 'number' ? ' class="text-end"' : '';
            return '<th' + align + '>' + (c.sortKey ? sortButton(c.header, c.sortKey)
                : '<span class="small fw-semibold">' + escapeHtml(c.header) + '</span>') + '</th>';
        }).join('') + '</tr>';
        bodyEl.innerHTML = data.rows.length === 0
            ? '<tr><td colspan="' + cols.length + '" class="text-muted">Nothing matches.</td></tr>'
            : data.rows.map(function (r) {
                var cells = r.cells || {};
                return '<tr>' + cols.map(function (c, i) {
                    var html = cellHtml(c, cells[c.key]);
                    if (i === 0 && r.link) html = '<a href="' + escapeHtml(r.link) + '">' + html + '</a>';
                    if (c.type === 'number') return '<td class="text-end text-nowrap fw-semibold">' + html
                        + (r.badge ? ' <span class="badge ' + escapeHtml(r.badgeClass || 'text-bg-secondary') + '">' + escapeHtml(r.badge) + '</span>' : '')
                        + '</td>';
                    return '<td class="' + (c.type === 'time' ? 'text-nowrap small' : '') + '">' + html + '</td>';
                }).join('') + '</tr>';
            }).join('');
    }

    function render(data) {
        lastPage = data;
        if (data.columns && headEl) {
            renderColumns(data);
            countEl.textContent = data.total >= 0 ? data.total.toLocaleString() + ' total' : '';
            pageEl.textContent = 'Page ' + data.page + ' of ' + data.totalPages;
            prevEl.disabled = data.page <= 1;
            nextEl.disabled = data.page >= data.totalPages;
            return;
        }
        if (headEl && headEl.innerHTML !== standardHead) headEl.innerHTML = standardHead;
        valueHeaderEl = document.getElementById('dashboardReportValueHeader');
        nodeColEl = modalEl.querySelector('[data-report-node-col]');
        var offset = (data.page - 1) * data.pageSize;
        var showNode = state.def.assetKind === 'camera';
        if (nodeColEl) nodeColEl.classList.toggle('d-none', !showNode);
        if (valueHeaderEl) valueHeaderEl.textContent = data.valueHeader || 'Value';

        bodyEl.innerHTML = data.rows.length === 0
            ? '<tr><td colspan="5" class="text-muted">Nothing matches.</td></tr>'
            : data.rows.map(function (r, i) {
                var name = r.link ? '<a href="' + escapeHtml(r.link) + '">' + escapeHtml(r.assetName) + '</a>' : escapeHtml(r.assetName);
                return '<tr>' +
                    '<td class="text-muted small">' + (offset + i + 1) + '</td>' +
                    '<td>' + name + '</td>' +
                    (showNode ? '<td>' + escapeHtml(r.nodeName || '') + '</td>' : '<td class="d-none"></td>') +
                    '<td class="small">' + details(r) + '</td>' +
                    '<td class="text-end text-nowrap fw-semibold">' + escapeHtml(r.display) + '</td>' +
                    '</tr>';
            }).join('');

        countEl.textContent = data.total >= 0 ? data.total.toLocaleString() + ' total' : '';
        pageEl.textContent = 'Page ' + data.page + ' of ' + data.totalPages;
        prevEl.disabled = data.page <= 1;
        nextEl.disabled = data.page >= data.totalPages;
    }

    function load() {
        var seq = ++requestSeq;
        fetchPage(state.page)
            .then(function (data) { if (seq === requestSeq) render(data); })
            .catch(function () {
                if (seq !== requestSeq) return;
                bodyEl.innerHTML = '<tr><td colspan="5" class="text-danger">Couldn\'t load this report. Try again.</td></tr>';
            });
    }

    function open(def, options) {
        if (!def) return;
        state = { def: def, options: options || {}, page: 1, q: '', nodeId: '', sort: '', desc: false };
        titleEl.textContent = def.title;
        filterEl.value = '';
        nodeEl.value = '';
        // Filtering by node only makes sense for lists of cameras or nodes.
        nodeEl.classList.toggle('d-none', def.assetKind !== 'camera' && def.assetKind !== 'node');
        filterEl.setAttribute('placeholder', def.assetKind === 'speed' ? 'Filter by camera, trap, object, or color' : filterPlaceholder);
        bodyEl.innerHTML = '<tr><td colspan="5" class="text-muted">Loading…</td></tr>';
        countEl.textContent = '';
        pageEl.textContent = '';
        bootstrap.Modal.getOrCreateInstance(modalEl).show();
        load();
    }

    filterEl.addEventListener('input', function () {
        clearTimeout(filterTimer);
        filterTimer = setTimeout(function () {
            if (!state) return;
            state.q = filterEl.value.trim();
            state.page = 1;
            load();
        }, FILTER_DELAY_MS);
    });

    nodeEl.addEventListener('change', function () {
        if (!state) return;
        state.nodeId = nodeEl.value;
        state.page = 1;
        load();
    });

    // Delegated: a report with its own columns rebuilds the header, buttons and all.
    modalEl.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('[data-report-sort]') : null;
        if (!btn || !state) return;
        var col = btn.getAttribute('data-report-sort');
        if (state.sort === col) state.desc = !state.desc;
        // Numbers and times read best biggest/newest first.
        else { state.sort = col; state.desc = col === 'value' || col === 'speed' || col === 'time'; }
        state.page = 1;
        load();
    });

    prevEl.addEventListener('click', function () { if (state && state.page > 1) { state.page--; load(); } });
    nextEl.addEventListener('click', function () {
        if (state && lastPage && state.page < lastPage.totalPages) { state.page++; load(); }
    });

    function csvCell(v) {
        var s = String(v == null ? '' : v);
        return /[",\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
    }

    csvEl.addEventListener('click', function () {
        if (!state) return;
        var def = state.def;
        var rows = [];
        var columns = null;
        csvEl.disabled = true;

        function next(page) {
            return fetchPage(page).then(function (data) {
                rows = rows.concat(data.rows);
                if (data.columns) columns = data.columns;
                if (page < data.totalPages && page < CSV_MAX_PAGES) return next(page + 1);
            });
        }

        next(1).then(function () {
            var lines;
            if (columns) {
                // Times stay as UTC ISO in the CSV, so a spreadsheet sorts and converts them reliably.
                lines = [columns.map(function (c) { return csvCell(c.type === 'time' ? c.header + ' (UTC)' : c.header); }).join(',')];
                rows.forEach(function (r) {
                    var cells = r.cells || {};
                    lines.push(columns.map(function (c) { return csvCell(cells[c.key]); }).join(','));
                });
            } else {
                lines = [['Rank', 'Name', 'Node', 'Details', 'Status', 'Value'].join(',')];
                rows.forEach(function (r, i) {
                    lines.push([i + 1, r.assetName, r.nodeName, r.note ? noteText(r) : '', r.badge, r.display].map(csvCell).join(','));
                });
            }
            var blob = new Blob([lines.join('\r\n')], { type: 'text/csv' });
            var a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = def.key + '-' + new Date().toISOString().slice(0, 10) + '.csv';
            document.body.appendChild(a);
            a.click();
            a.remove();
            setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
        }).catch(function () {
            alert('Couldn\'t download the report. Try again.');
        }).then(function () { csvEl.disabled = false; });
    });

    return { open: open };
})();

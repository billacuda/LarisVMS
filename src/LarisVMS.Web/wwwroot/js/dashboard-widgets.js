// Dashboard widgets (Pages/Index): builds the user's saved GridStack layout, lets them add, remove,
// move and resize widgets ("Customize"), and polls GET /api/dashboard/widgets every 60s for the list
// widgets' top 10. The layout is saved per user in the dashboardLayout preference
// (user-preferences.js) as {items: [{key, x, y, w, h, window, asc}]}.
//
// The three original sections (summary tiles, node cards, camera health table) are still rendered by
// Index.razor and refreshed by dashboard.js; this file only moves each one between the hidden pool
// and its widget. Clicking a list widget's title opens its full report (dashboard-report.js).
(function () {
    'use strict';

    var LAYOUT_KEY = 'dashboardLayout';
    var POLL_INTERVAL_MS = 60000;
    var SAVE_DELAY_MS = 400;
    var GRID_COLUMNS = 12;

    var gridEl = document.getElementById('dashboardGrid');
    var configEl = document.getElementById('dashboardConfig');
    var pool = document.getElementById('dashboardWidgetPool');
    if (!gridEl || !configEl || !pool || typeof GridStack === 'undefined') return;

    var config = {};
    try { config = JSON.parse(configEl.textContent || '{}'); } catch (e) { /* fall through to an empty config */ }

    var defs = {};
    (config.available || []).forEach(function (d) { defs[d.key] = d; });

    var grid = null;
    var items = {}; // key -> { el, body, options: { window, asc } }
    var saveTimer = null;

    var customizeBtn = document.getElementById('dashboardCustomize');
    var editControls = document.getElementById('dashboardEditControls');
    var addBtn = document.getElementById('dashboardAddWidget');
    var resetBtn = document.getElementById('dashboardResetLayout');
    var doneBtn = document.getElementById('dashboardDone');
    var addModalEl = document.getElementById('dashboardAddModal');
    var addList = document.getElementById('dashboardAddList');

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    // A row's note can hold a "{time}" placeholder for noteTimeUtc, shown in the viewer's local time.
    function noteText(row) {
        return row.noteTimeUtc ? String(row.note).replace('{time}', window.larisvmsTime.format(row.noteTimeUtc)) : row.note;
    }

    function isListKind(def) { return def.kind === 'TopList' || def.kind === 'Snapshots'; }
    function isHeatmap(def) { return def.kind === 'Heatmap'; }
    function hasPoolBody(def) { return def.kind === 'Summary' || def.kind === 'NodeCards' || def.kind === 'CameraTable'; }

    // ── Layout persistence ──────────────────────────────────────────────────

    // Unknown keys (a widget retired since the layout was saved, or one this user lost permission
    // for) and duplicates are dropped. No saved layout at all means the default one; a saved empty
    // layout stays empty, since the user removed everything on purpose.
    function loadLayout() {
        var raw = window.larisvmsPreferences.get(LAYOUT_KEY, '');
        var saved = null;
        if (raw) {
            try { saved = JSON.parse(raw); } catch (e) { saved = null; }
        }
        var list = saved && Array.isArray(saved.items) ? saved.items : (config.defaultLayout || []);
        var seen = {};
        return list.filter(function (i) {
            if (!i || !defs[i.key] || seen[i.key]) return false;
            seen[i.key] = true;
            return true;
        });
    }

    // Only the 12-column layout is ever saved: below the phone breakpoint GridStack re-flows into one
    // column, and saving that would overwrite the desktop arrangement (Customize is hidden then — see
    // syncCustomizeAvailability).
    //
    // Positions come straight from each widget's live GridStack node rather than grid.save(): save()
    // drops w/h whenever they equal the widget's minimum, which reloaded as the default size instead.
    function saveLayout() {
        if (!grid || grid.getColumn() !== GRID_COLUMNS) return;
        var out = Object.keys(items).map(function (key) {
            var n = items[key].el.gridstackNode || {};
            var opts = items[key].options;
            return {
                key: key, x: n.x, y: n.y, w: n.w, h: n.h,
                window: opts.window || null, asc: !!opts.asc, kinds: opts.kinds || null, cameraId: opts.cameraId || null
            };
        }).sort(function (a, b) { return a.y - b.y || a.x - b.x; });
        window.larisvmsPreferences.set(LAYOUT_KEY, JSON.stringify({ items: out })).then(function (ok) {
            setLayoutStatus(ok ? '' : 'Couldn\'t save your dashboard layout — it will reset when the page reloads.');
        });
    }

    function setLayoutStatus(text) {
        var el = document.getElementById('dashboardLayoutStatus');
        if (!el) return;
        el.textContent = text;
        el.classList.toggle('d-none', !text);
    }

    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(saveLayout, SAVE_DELAY_MS);
    }

    // ── Widget chrome ───────────────────────────────────────────────────────

    function controlsHtml(def, options) {
        var html = '';
        if (def.hasWindow) {
            var current = options.window || '24h';
            html += '<select class="form-select form-select-sm py-0 dashboard-widget-window" aria-label="Time window">' +
                ['1h', '24h', '7d', '30d'].map(function (w) {
                    return '<option value="' + w + '"' + (w === current ? ' selected' : '') + '>' + w + '</option>';
                }).join('') + '</select>';
        }
        if (def.hasDirectionToggle) {
            html += '<button type="button" class="btn btn-sm btn-outline-secondary py-0 dashboard-widget-dir" ' +
                'title="Switch between highest and lowest first">' + (options.asc ? '↑ Lowest' : '↓ Highest') + '</button>';
        }
        if (isHeatmap(def)) {
            var cams = config.activityCameras || [];
            html += '<select class="form-select form-select-sm py-0 dashboard-widget-camera" aria-label="Camera">' +
                '<option value="all"' + (options.cameraId === 'all' ? ' selected' : '') + '>All cameras</option>' +
                cams.map(function (c) {
                    return '<option value="' + escapeHtml(c.id) + '"' + (c.id === options.cameraId ? ' selected' : '') + '>' + escapeHtml(c.name) + '</option>';
                }).join('') + '</select>';
        }
        if (def.hasKindFilter) {
            html += '<button type="button" class="btn btn-sm btn-outline-secondary py-0 dashboard-widget-config" ' +
                'title="Choose which detections to show" aria-label="Choose which detections to show">⚙</button>';
        }
        return html;
    }

    function buildWidget(item) {
        var def = defs[item.key];
        var options = { window: item.window || null, asc: !!item.asc, kinds: Array.isArray(item.kinds) ? item.kinds : null,
            cameraId: item.cameraId || null };
        if (isHeatmap(def) && !options.cameraId && config.activityCameras && config.activityCameras.length)
            options.cameraId = config.activityCameras[0].id;

        var el = document.createElement('div');
        el.className = 'grid-stack-item';
        el.setAttribute('gs-id', def.key);
        el.setAttribute('gs-min-w', '2');
        el.setAttribute('gs-min-h', '2');
        if (item.x !== undefined && item.x !== null) {
            el.setAttribute('gs-x', item.x);
            el.setAttribute('gs-y', item.y);
        } else {
            el.setAttribute('gs-auto-position', 'true');
        }
        el.setAttribute('gs-w', item.w || def.defaultW);
        el.setAttribute('gs-h', item.h || def.defaultH);

        var titleHtml = isListKind(def) || isHeatmap(def)
            // A span acting as a button rather than a <button>: GridStack never starts a drag on a real
            // button, and while customizing the title bar is the drag handle.
            ? '<span role="button" tabindex="0" class="fw-semibold text-truncate dashboard-widget-title" ' +
              'title="Open the full report">' + escapeHtml(def.title) + '</span>'
            : '<span class="fw-semibold text-truncate">' + escapeHtml(def.title) + '</span>';

        el.innerHTML =
            '<div class="grid-stack-item-content card dashboard-widget' + (def.kind === 'Summary' ? ' dashboard-widget--bare' : '') + '">' +
                '<div class="card-header d-flex align-items-center gap-2 py-1 px-2 dashboard-widget-header">' +
                    titleHtml +
                    '<span class="ms-auto d-flex align-items-center gap-1 flex-shrink-0">' + controlsHtml(def, options) + '</span>' +
                    '<button type="button" class="btn-close dashboard-widget-remove" aria-label="Remove ' + escapeHtml(def.title) + '"></button>' +
                '</div>' +
                '<div class="card-body dashboard-widget-body' + (hasPoolBody(def) ? ' p-2' : ' p-0') + '"></div>' +
            '</div>';

        var body = el.querySelector('.dashboard-widget-body');
        if (hasPoolBody(def)) {
            var source = pool.querySelector('[data-widget-body="' + def.key + '"]');
            if (source) body.appendChild(source);
        } else {
            body.innerHTML = '<div class="text-muted small m-2">Loading…</div>';
        }

        items[def.key] = { el: el, body: body, options: options };
        wireWidget(def, el, options);
        return el;
    }

    function wireWidget(def, el, options) {
        // While customizing, the title bar is the drag handle — a click there shouldn't open the report.
        var title = el.querySelector('.dashboard-widget-title');
        if (title) {
            title.addEventListener('click', function () {
                if (!gridEl.classList.contains('dashboard-editing')) openReport(def.key);
            });
            title.addEventListener('keydown', function (e) {
                if (e.key !== 'Enter' && e.key !== ' ') return;
                e.preventDefault();
                if (!gridEl.classList.contains('dashboard-editing')) openReport(def.key);
            });
        }

        var configBtn = el.querySelector('.dashboard-widget-config');
        if (configBtn) configBtn.addEventListener('click', function () { openKindsPicker(def.key); });

        var cameraSelect = el.querySelector('.dashboard-widget-camera');
        if (cameraSelect) cameraSelect.addEventListener('change', function () {
            options.cameraId = cameraSelect.value;
            items[def.key].body.innerHTML = '<div class="text-muted small m-2">Loading…</div>';
            saveLayout();
            pollHeatmaps();
        });

        var windowSelect = el.querySelector('.dashboard-widget-window');
        if (windowSelect) windowSelect.addEventListener('change', function () {
            options.window = windowSelect.value;
            saveLayout();
            poll();
        });

        var dir = el.querySelector('.dashboard-widget-dir');
        if (dir) dir.addEventListener('click', function () {
            options.asc = !options.asc;
            dir.textContent = options.asc ? '↑ Lowest' : '↓ Highest';
            saveLayout();
            poll();
        });

        el.querySelector('.dashboard-widget-remove').addEventListener('click', function () { removeWidget(def.key); });
    }

    function addWidget(item) {
        var el = buildWidget(item);
        gridEl.appendChild(el);
        grid.makeWidget(el);
    }

    function removeWidget(key) {
        var entry = items[key];
        if (!entry) return;
        // Hand an original section back to the hidden pool, so dashboard.js keeps finding its elements
        // and it can be re-added later.
        var source = entry.body.querySelector('[data-widget-body]');
        if (source) pool.appendChild(source);
        grid.removeWidget(entry.el);
        delete items[key];
        updateEmptyState();
        saveLayout();
    }

    function openReport(key) {
        var entry = items[key];
        // The activity widget's "full report" is the Activity page for its camera.
        if (isHeatmap(defs[key])) {
            if (entry && entry.options.cameraId) location.href = '/Activity?cameraId=' + encodeURIComponent(entry.options.cameraId) + '&range=7d';
            return;
        }
        if (window.larisvmsDashboardReport) window.larisvmsDashboardReport.open(defs[key], entry ? entry.options : {});
    }

    function updateEmptyState() {
        var empty = document.getElementById('dashboardEmptyLayout');
        var none = Object.keys(items).length === 0;
        if (none && !empty) {
            empty = document.createElement('p');
            empty.id = 'dashboardEmptyLayout';
            empty.className = 'text-muted';
            empty.textContent = 'No widgets on your dashboard. Use Customize → Add widget to add some.';
            gridEl.parentNode.insertBefore(empty, gridEl);
        } else if (!none && empty) {
            empty.remove();
        }
    }

    // ── List widget rendering ───────────────────────────────────────────────

    function rowHtml(row, index, def) {
        var name = row.link
            ? '<a href="' + escapeHtml(row.link) + '">' + escapeHtml(row.assetName) + '</a>'
            : escapeHtml(row.assetName);
        var badge = row.badge
            ? ' <span class="badge ' + escapeHtml(row.badgeClass || 'text-bg-secondary') + '">' + escapeHtml(row.badge) + '</span>'
            : '';
        var details = [];
        if (row.note) details.push(escapeHtml(noteText(row)));
        if (def.assetKind === 'camera' && row.nodeName) details.push(escapeHtml(row.nodeName));
        return '<tr>' +
            '<td class="text-muted small dashboard-rank">' + (index + 1) + '</td>' +
            '<td class="dashboard-name">' + name + badge +
                (details.length ? '<div class="small text-muted text-truncate">' + details.join(' · ') + '</div>' : '') + '</td>' +
            '<td class="text-end text-nowrap fw-semibold dashboard-value">' + escapeHtml(row.display) + '</td>' +
            '</tr>';
    }

    function renderList(def, entry, result) {
        if (!result.rows || result.rows.length === 0) {
            entry.body.innerHTML = '<div class="text-muted small m-2">' + escapeHtml(result.emptyMessage || 'Nothing to show') + '</div>';
            return;
        }
        var html = '<table class="table table-sm table-hover align-middle mb-0 dashboard-toplist"><tbody>' +
            result.rows.map(function (r, i) { return rowHtml(r, i, def); }).join('') +
            '</tbody></table>';
        if (result.total > result.rows.length) {
            html += '<div class="text-end px-2 py-1"><button type="button" class="btn btn-link btn-sm p-0 dashboard-view-all">View all ' +
                result.total.toLocaleString() + '</button></div>';
        }
        entry.body.innerHTML = html;
        var viewAll = entry.body.querySelector('.dashboard-view-all');
        if (viewAll) viewAll.addEventListener('click', function () { openReport(def.key); });
    }

    var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    function snapshotHtml(s) {
        var src = s.isAiDetection
            ? '/snapshot-image/' + s.cameraId + '/' + s.id
            : '/playback-thumbnail/' + s.cameraId + '?atUtc=' + encodeURIComponent(s.atUtc) + '&exact=true';
        var labels = s.badges && s.badges.length
            ? s.badges.map(function (b) { return (b.emoji ? b.emoji + ' ' : '') + b.label; }).join(', ')
            : (s.emoji ? s.emoji + ' ' : '') + s.label;
        var at = window.larisvmsTime.parse(s.atUtc) || new Date(NaN);
        return '<img src="' + src + '" loading="lazy" alt="" class="rounded" data-on-error="invisible">' +
            '<div class="small text-truncate">' + escapeHtml(labels) + '</div>' +
            '<div class="small text-muted text-truncate" title="' + escapeHtml(at.toISOString()) + '">' +
                escapeHtml(s.cameraName) + ' · ' + escapeHtml(at.toLocaleString()) + '</div>';
    }

    // Updates the strip in place rather than redrawing it: cards already shown keep their element (no
    // image reload, no flash) and slide right to their new spot, new cards fade in at the left, and
    // cards that dropped off the end are removed. The slide is a FLIP animation — measure where each
    // kept card was, reorder, then animate it from the old spot to the new one.
    function renderSnapshots(entry, result) {
        var snaps = result.snapshots || [];
        if (snaps.length === 0) {
            entry.body.innerHTML = '<div class="text-muted small m-2">' + escapeHtml(result.emptyMessage || 'Nothing to show') + '</div>';
            return;
        }

        var strip = entry.body.querySelector('.dashboard-snapshots');
        var firstRender = !strip;
        if (firstRender) {
            entry.body.innerHTML = '<div class="d-flex gap-2 p-2 dashboard-snapshots"></div>';
            strip = entry.body.querySelector('.dashboard-snapshots');
        }

        var existing = {};
        var before = {};
        Array.prototype.forEach.call(strip.children, function (el) {
            var id = el.getAttribute('data-snapshot-id');
            existing[id] = el;
            before[id] = el.getBoundingClientRect().left;
        });

        var keep = {};
        var entering = [];
        var ordered = snaps.map(function (s) {
            var id = String(s.id);
            keep[id] = true;
            var el = existing[id];
            if (!el) {
                el = document.createElement('a');
                el.className = 'dashboard-snapshot text-reset text-decoration-none';
                el.setAttribute('data-snapshot-id', id);
                el.href = '/Playback?cameraId=' + s.cameraId + '&atUtc=' + encodeURIComponent(s.playFromUtc);
                el.innerHTML = snapshotHtml(s);
                if (!firstRender && !reduceMotion) { el.classList.add('dashboard-snapshot--entering'); entering.push(el); }
            }
            return el;
        });

        Object.keys(existing).forEach(function (id) { if (!keep[id]) existing[id].remove(); });
        ordered.forEach(function (el) { strip.appendChild(el); });

        if (firstRender || reduceMotion) return;

        var moved = [];
        ordered.forEach(function (el) {
            var id = el.getAttribute('data-snapshot-id');
            if (!(id in before)) return;
            var dx = before[id] - el.getBoundingClientRect().left;
            if (!dx) return;
            el.style.transition = 'none';
            el.style.transform = 'translateX(' + dx + 'px)';
            moved.push(el);
        });
        void strip.offsetWidth; // commit the starting positions before animating away from them
        requestAnimationFrame(function () {
            moved.forEach(function (el) { el.style.transition = ''; el.style.transform = ''; });
            entering.forEach(function (el) { el.classList.remove('dashboard-snapshot--entering'); });
        });
    }

    // ── Polling ─────────────────────────────────────────────────────────────

    function widgetSpec(key) {
        var o = items[key].options;
        return key + (o.window ? '|' + o.window : '') + (o.asc ? '|asc' : '') +
            (o.kinds ? '|k=' + o.kinds.join(',') : '');
    }

    // Camera activity widgets: each fetches its camera's 7-day heatmap from the Activity page's own
    // endpoint and draws a compact copy of that chart (same cells and colors — see activity.js).
    function renderHeatmap(def, entry, data) {
        if (!data || data.total === 0) {
            entry.body.innerHTML = '<div class="text-muted small m-2">' + escapeHtml(def.emptyMessage || 'Nothing to show') + '</div>';
            return;
        }
        var steps = 6;
        var html = '<div class="p-2"><div class="activity-grid activity-grid--compact" role="img" aria-label="' + escapeHtml(data.peak || 'Activity heatmap') + '">' +
            '<div></div>' + data.columnLabels.map(function (c, i) {
                return '<div class="activity-col-label' + (i % 3 === 0 ? '' : ' activity-col-label--minor') + '">' + (i % 3 === 0 ? escapeHtml(c) : '') + '</div>';
            }).join('');
        data.rowLabels.forEach(function (label, r) {
            html += '<div class="activity-row-label">' + escapeHtml(label) + '</div>';
            data.cells[r].forEach(function (v, c) {
                var stepN = v && data.max ? Math.max(1, Math.ceil(v / data.max * steps)) : 0;
                html += '<div class="activity-cell activity-step-' + stepN + '" title="' + escapeHtml(label + ' ' + data.columnLabels[c] + ': ' + v.toLocaleString()) + '"></div>';
            });
        });
        html += '</div>' + (data.peak ? '<div class="small text-muted mt-2">' + escapeHtml(data.peak) + '</div>' : '') + '</div>';
        entry.body.innerHTML = html;
    }

    function pollHeatmaps() {
        Object.keys(items).filter(function (k) { return isHeatmap(defs[k]); }).forEach(function (k) {
            var entry = items[k];
            if (!entry.options.cameraId) {
                entry.body.innerHTML = '<div class="text-muted small m-2">No cameras you can review.</div>';
                return;
            }
            var url = entry.options.cameraId === 'all' ? '/api/activity?range=7d'
                : '/api/cameras/' + encodeURIComponent(entry.options.cameraId) + '/activity?range=7d';
            fetch(url)
                .then(function (r) { return r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)); })
                .then(function (data) { if (items[k] === entry) renderHeatmap(defs[k], entry, data); })
                .catch(function () { /* keep what's shown; the next poll retries */ });
        });
    }

    function poll() {
        pollHeatmaps();
        var keys = Object.keys(items).filter(function (k) { return isListKind(defs[k]); });
        if (keys.length === 0) return;
        var query = keys.map(function (k) { return 'w=' + encodeURIComponent(widgetSpec(k)); }).join('&');
        fetch('/api/dashboard/widgets?' + query)
            .then(function (resp) { return resp.ok ? resp.json() : Promise.reject(new Error('HTTP ' + resp.status)); })
            .then(function (data) {
                (data.results || []).forEach(function (result) {
                    var entry = items[result.key];
                    var def = defs[result.key];
                    if (!entry || !def) return;
                    if (def.kind === 'Snapshots') renderSnapshots(entry, result);
                    else renderList(def, entry, result);
                });
            })
            .catch(function () {
                // Keep whatever's on screen; dashboard.js's status line already reports a failed
                // refresh, and the next interval tries again.
            });
    }

    // ── Latest detections' type picker ──────────────────────────────────────

    var kindsModalEl = document.getElementById('dashboardKindsModal');
    var kindsList = document.getElementById('dashboardKindsList');
    var kindsKey = null;

    function defaultKinds() {
        return (config.detectionKinds || []).filter(function (k) { return k.byDefault; }).map(function (k) { return k.token; });
    }

    function renderKindChecks(selected) {
        kindsList.innerHTML = (config.detectionKinds || []).map(function (k, i) {
            var id = 'dashboardKind' + i;
            return '<div class="form-check">' +
                '<input class="form-check-input" type="checkbox" id="' + id + '" value="' + escapeHtml(k.token) + '"' +
                    (selected.indexOf(k.token) >= 0 ? ' checked' : '') + '>' +
                '<label class="form-check-label" for="' + id + '">' + (k.emoji ? escapeHtml(k.emoji) + ' ' : '') + escapeHtml(k.label) + '</label>' +
                '</div>';
        }).join('');
    }

    function openKindsPicker(key) {
        if (!kindsModalEl || !kindsList || !items[key]) return;
        kindsKey = key;
        renderKindChecks(items[key].options.kinds || defaultKinds());
        bootstrap.Modal.getOrCreateInstance(kindsModalEl).show();
    }

    function wireKindsPicker() {
        if (!kindsModalEl) return;
        document.getElementById('dashboardKindsDefault').addEventListener('click', function () { renderKindChecks(defaultKinds()); });
        document.getElementById('dashboardKindsSave').addEventListener('click', function () {
            var entry = items[kindsKey];
            if (!entry) return;
            var chosen = Array.prototype.filter.call(kindsList.querySelectorAll('input[type=checkbox]'), function (c) { return c.checked; })
                .map(function (c) { return c.value; });
            var defaults = defaultKinds();
            var isDefault = chosen.length === defaults.length && defaults.every(function (t) { return chosen.indexOf(t) >= 0; });
            // Stored only when it differs from the default, so detection types added later show up
            // for anyone who never changed this.
            entry.options.kinds = isDefault ? null : chosen;
            bootstrap.Modal.getOrCreateInstance(kindsModalEl).hide();
            entry.body.innerHTML = '<div class="text-muted small m-2">Loading…</div>';
            saveLayout();
            poll();
        });
    }

    // ── Customize mode ──────────────────────────────────────────────────────

    // Customize only makes sense on the 12-column grid (see saveLayout). Checked against the grid's real
    // column count, not the window width: with the sidebar open, a mid-size window can be wide enough
    // for desktop chrome but narrow enough to collapse the grid.
    function syncCustomizeAvailability() {
        var bar = document.getElementById('dashboardCustomizeBar');
        var full = grid.getColumn() === GRID_COLUMNS;
        if (bar) bar.classList.toggle('d-none', !full);
        if (!full && gridEl.classList.contains('dashboard-editing')) setEditing(false);
    }

    function setEditing(on) {
        grid.setStatic(!on);
        gridEl.classList.toggle('dashboard-editing', on);
        if (customizeBtn) customizeBtn.classList.toggle('d-none', on);
        if (editControls) {
            editControls.classList.toggle('d-none', !on);
            editControls.classList.toggle('d-flex', on);
        }
    }

    function renderAddList() {
        var groups = {};
        var order = [];
        (config.available || []).forEach(function (d) {
            if (!groups[d.group]) { groups[d.group] = []; order.push(d.group); }
            groups[d.group].push(d);
        });
        addList.innerHTML = order.map(function (g) {
            return '<h6 class="mt-2">' + escapeHtml(g) + '</h6><div class="list-group mb-3">' +
                groups[g].map(function (d) {
                    var onBoard = !!items[d.key];
                    return '<div class="list-group-item d-flex align-items-center gap-2">' +
                        '<span class="flex-grow-1">' + escapeHtml(d.title) + '</span>' +
                        (onBoard
                            ? '<span class="small text-muted">On dashboard</span>'
                            : '<button type="button" class="btn btn-sm btn-outline-primary" data-add-widget="' + escapeHtml(d.key) + '">Add</button>') +
                        '</div>';
                }).join('') + '</div>';
        }).join('');
    }

    function wireCustomize() {
        if (customizeBtn) customizeBtn.addEventListener('click', function () { setEditing(true); });
        if (doneBtn) doneBtn.addEventListener('click', function () { setEditing(false); saveLayout(); });

        if (addBtn && addModalEl && addList) {
            addBtn.addEventListener('click', function () {
                renderAddList();
                bootstrap.Modal.getOrCreateInstance(addModalEl).show();
            });
            addList.addEventListener('click', function (e) {
                var btn = e.target.closest('[data-add-widget]');
                if (!btn) return;
                var key = btn.getAttribute('data-add-widget');
                if (!defs[key] || items[key]) return;
                addWidget({ key: key });
                updateEmptyState();
                saveLayout();
                renderAddList();
                poll();
            });
        }

        if (resetBtn) resetBtn.addEventListener('click', function () {
            if (!confirm('Reset your dashboard to the default widgets and layout?')) return;
            Object.keys(items).forEach(function (k) { removeWidget(k); });
            window.larisvmsPreferences.set(LAYOUT_KEY, '');
            buildLayout(loadLayout());
            poll();
        });
    }

    function buildLayout(layout) {
        grid.batchUpdate();
        layout.forEach(addWidget);
        grid.batchUpdate(false);
        updateEmptyState();
    }

    function init() {
        grid = GridStack.init({
            column: GRID_COLUMNS,
            cellHeight: 60,
            margin: 6,
            staticGrid: true,
            handle: '.dashboard-widget-header',
            // One column only at phone widths.
            columnOpts: { breakpoints: [{ w: 576, c: 1 }] }
        }, gridEl);

        buildLayout(loadLayout());
        // Attached after the initial build so loading a layout never re-saves it.
        grid.on('change', scheduleSave);
        wireCustomize();
        wireKindsPicker();
        syncCustomizeAvailability();
        var resizeTimer = null;
        window.addEventListener('resize', function () {
            clearTimeout(resizeTimer);
            resizeTimer = setTimeout(syncCustomizeAvailability, 200);
        });

        poll();
        setInterval(poll, POLL_INTERVAL_MS);
    }

    window.larisvmsPreferences.whenReady().then(init);
})();

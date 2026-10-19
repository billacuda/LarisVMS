// Activity page (Components/Pages/Activity): heatmaps of when events happen, for one camera
// (GET /api/cameras/{id}/activity) or all of them (GET /api/activity).
//
// Views: "Typical week" — day of week × hour, totalled over the range (the default); "By day" — every
// date left to right, hours top to bottom, for 30 days and up; and for the last 24 hours, event type ×
// hour. The filter tree narrows to event types (Animal) or labels (Animal → cat).
//
// The event-type choice and the view are remembered per user (larisvmsPreferences) and carried across
// cameras; camera, range, view and selection are also in the URL, so a view can be bookmarked.
//
// Color is one blue ramp, light (few) → dark (many), quantized into six steps so neighbouring cells
// are easy to compare; empty cells use the surface tint. Exact counts are in the hover tooltip and the
// table view, so nothing depends on reading the color alone.
//
// "Speeds" (GET /api/cameras/{id}/speed-activity, /api/speed-activity) shows speed-trap measurements
// on the same grids: average or top speed (the ramp spans slowest → fastest cell, and cells over the
// trap's speed limit are outlined red), or how many were measured / over the limit. The trap list
// replaces the event-type tree; the metric and trap choice are remembered like the rest.
(function () {
    'use strict';

    var STEPS = 6;
    var SELECTION_PREF = 'activitySelection';
    var VIEW_PREF = 'activityView';
    var KIND_PREF = 'activityKind';
    var METRIC_PREF = 'activityMetric';
    var SCENE_SOURCE_PREF = 'activitySceneSource';

    var configEl = document.getElementById('activityConfig');
    var chartEl = document.getElementById('activityChart');
    if (!configEl || !chartEl) return;

    var config = JSON.parse(configEl.textContent || '{}');
    var cameraSelect = document.getElementById('activityCamera');
    var rangeGroup = document.getElementById('activityRange');
    var viewGroup = document.getElementById('activityView');
    var tableToggle = document.getElementById('activityTableView');
    var filterEl = document.getElementById('activityFilter');
    var resetBtn = document.getElementById('activityResetFilter');
    var peakEl = document.getElementById('activityPeak');
    var summaryEl = document.getElementById('activitySummary');
    var legendEl = document.getElementById('activityLegend');
    var tooltip = document.getElementById('activityTooltip');
    var kindGroup = document.getElementById('activityKind');
    var eventCard = document.getElementById('activityEventCard');
    var speedCard = document.getElementById('activitySpeedCard');
    var metricSelect = document.getElementById('activityMetric');
    var trapsEl = document.getElementById('activitySpeedTraps');
    var sceneCard = document.getElementById('activitySceneCard');
    var sceneCatsEl = document.getElementById('activitySceneCategories');
    var sceneFrom = document.getElementById('activitySceneFrom');
    var sceneTo = document.getElementById('activitySceneTo');
    var tableSwitch = document.getElementById('activityTableSwitch');

    var params = new URLSearchParams(location.search);
    var state = {
        cameraId: config.cameraId,
        range: config.range || '7d',
        view: config.view || null,           // null until preferences load
        selection: params.getAll('sel'),     // the user's choice; empty = remembered/default
        kind: config.kind || null,           // 'events' | 'speeds'; null until preferences load
        metric: config.metric || null,
        traps: params.getAll('trap'),        // speed traps to include; empty = all
        source: params.get('source') === 'motion' ? 'motion' : null, // scene: 'objects' | 'motion'
        cats: params.getAll('cat'),          // scene object categories; empty = all
        fromHour: params.get('from') !== null ? +params.get('from') : 0,
        toHour: params.get('to') !== null ? +params.get('to') : 0
    };
    function speeds() { return state.kind === 'speeds'; }
    function scene() { return state.kind === 'scene'; }
    // Scene only: a whole view at once ("view:<id>" in the camera list), one heatmap per camera.
    function sceneView() { return typeof state.cameraId === 'string' && state.cameraId.indexOf('view:') === 0; }
    function viewOf(id) { return (config.views || []).filter(function (v) { return 'view:' + v.id === id; })[0]; }
    var last = null;
    var requestSeq = 0;

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function longRange() { return state.range === '30d' || state.range === '90d' || state.range === '1y'; }

    function syncUrl() {
        var p = new URLSearchParams();
        p.set('cameraId', state.cameraId);
        p.set('range', state.range);
        if (scene()) {
            p.set('kind', 'scene');
            p.set('source', state.source);
            state.cats.forEach(function (c) { p.append('cat', c); });
            if (state.fromHour !== state.toHour) { p.set('from', state.fromHour); p.set('to', state.toHour); }
            try { history.replaceState(null, '', '/Activity?' + p.toString()); } catch (e) { /* bookmarking is a nicety */ }
            return;
        }
        if (longRange()) p.set('view', state.view);
        if (speeds()) {
            p.set('kind', 'speeds');
            p.set('metric', state.metric);
            state.traps.forEach(function (t) { p.append('trap', t); });
        } else {
            state.selection.forEach(function (s) { p.append('sel', s); });
        }
        try { history.replaceState(null, '', '/Activity?' + p.toString()); } catch (e) { /* bookmarking is a nicety */ }
    }

    function sceneUrl(cameraId) {
        var p = new URLSearchParams();
        p.set('range', state.range);
        p.set('source', state.source);
        if (state.source === 'objects') state.cats.forEach(function (c) { p.append('cat', c); });
        if (state.fromHour !== state.toHour) { p.set('fromHour', state.fromHour); p.set('toHour', state.toHour); }
        return '/api/cameras/' + cameraId + '/scene-heat?' + p.toString();
    }

    // The picture under a camera's heat: its uploaded one, or a sharp frame from its recording. The
    // upload's version is in the address, so a new upload isn't hidden by the browser's cache.
    function backgroundUrl(data) {
        return '/api/cameras/' + encodeURIComponent(data.cameraId) + '/scene-background?v=' + (data.backgroundVersion || 0);
    }

    function apiUrl() {
        var p = new URLSearchParams();
        p.set('range', state.range);
        if (scene()) return sceneUrl(state.cameraId);
        if (longRange()) p.set('view', state.view);
        if (speeds()) {
            p.set('metric', state.metric);
            state.traps.forEach(function (t) { p.append('trap', t); });
            return (state.cameraId === 'all' ? '/api/speed-activity?' : '/api/cameras/' + state.cameraId + '/speed-activity?') + p.toString();
        }
        state.selection.forEach(function (s) { p.append('sel', s); });
        return (state.cameraId === 'all' ? '/api/activity?' : '/api/cameras/' + state.cameraId + '/activity?') + p.toString();
    }

    function load() {
        var seq = ++requestSeq;
        // Views are a Scene choice; Events and Speeds go back to a camera.
        if (!scene() && sceneView()) {
            state.cameraId = config.firstCameraId || 'all';
            cameraSelect.value = state.cameraId;
        }
        if (scene() && sceneView()) { loadSceneView(seq); return; }
        if (scene() && state.cameraId === 'all') {
            // A scene is one camera's picture; there's nothing to lay "all cameras" over.
            last = null;
            renderControls();
            peakEl.textContent = '';
            summaryEl.textContent = '';
            legendEl.innerHTML = '';
            chartEl.innerHTML = '<p class="text-muted mb-0">Choose a camera to see where things move in its picture.</p>';
            syncUrl();
            return;
        }
        chartEl.classList.add('opacity-50');
        fetch(apiUrl())
            .then(function (r) { return r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)); })
            .then(function (data) {
                if (seq !== requestSeq) return;
                last = data;
                render();
            })
            .catch(function () {
                if (seq !== requestSeq) return;
                chartEl.innerHTML = '<p class="text-danger mb-0">Couldn\'t load activity. Try again.</p>';
            })
            .then(function () { if (seq === requestSeq) chartEl.classList.remove('opacity-50'); });
        syncUrl();
    }

    // Scene for a view: each of its cameras' heat, side by side, each on its own scale.
    function loadSceneView(seq) {
        var view = viewOf(state.cameraId);
        last = null;
        renderControls();
        legendEl.innerHTML = '';
        if (!view || view.cameraIds.length === 0) {
            peakEl.textContent = '';
            summaryEl.textContent = '';
            chartEl.innerHTML = '<p class="text-muted mb-0">This view has no cameras you can review.</p>';
            syncUrl();
            return;
        }
        chartEl.classList.add('opacity-50');
        Promise.all(view.cameraIds.map(function (id) {
            return fetch(sceneUrl(id)).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; });
        })).then(function (results) {
            if (seq !== requestSeq) return;
            chartEl.classList.remove('opacity-50');
            renderSceneView(view, results.filter(Boolean));
        });
        syncUrl();
    }

    function renderSceneView(view, heats) {
        var motion = state.source === 'motion';
        peakEl.textContent = (motion ? 'Where motion happened' : 'Where objects moved') + ' · ' + view.name;
        var hours = state.fromHour !== state.toHour
            ? ', ' + sceneFrom.options[state.fromHour].text + ' to ' + sceneTo.options[state.toHour].text : '';
        summaryEl.textContent = 'Over ' + sceneRangeNames[state.range] + hours + '. Each camera is on its own scale.';
        window.larisvmsSceneHeatmap.reset();
        chartEl.innerHTML = '<div class="scene-heatmap-grid">' + heats.map(function (h, i) {
            var busiest = h.maxCount === 0 ? 'nothing recorded'
                : motion ? 'busiest spot ' + h.maxCount.toLocaleString() + ' frames'
                : 'busiest spot ' + h.maxCount.toLocaleString() + (h.maxCount === 1 ? ' object' : ' objects');
            return '<div class="scene-heatmap-panel"><div class="small mb-1"><span class="fw-semibold">' + escapeHtml(h.cameraName) +
                '</span> <span class="text-muted">· ' + busiest + '</span></div><div data-scene-panel="' + i + '"></div></div>';
        }).join('') + '</div>';
        window.larisvmsSceneHeatmap.layoutGrid(chartEl.querySelector('.scene-heatmap-grid'));
        heats.forEach(function (h, i) {
            var panel = chartEl.querySelector('[data-scene-panel="' + i + '"]');
            if (h.maxCount === 0) {
                panel.innerHTML = '<p class="text-muted small mb-0">' + (motion && !h.motionAvailable
                    ? 'No grid motion detection on this camera.' : 'Nothing in this range.') + '</p>';
            } else {
                window.larisvmsSceneHeatmap.render(panel, h, backgroundUrl(h), { framed: true });
            }
        });
        legendEl.innerHTML = window.larisvmsSceneHeatmap.legend('');
        renderBackgroundControls(null);
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    function step(value, max) {
        if (!value || !max) return 0;
        return Math.max(1, Math.ceil(value / max * STEPS));
    }

    // The color step for one cell. Speeds (average/top) spread the ramp from the slowest to the
    // fastest cell — speeds bunch together, and a ramp from zero would paint them all dark.
    function cellStep(data, v) {
        if (v === null || v === undefined) return 0;
        if (data.metric === 'avg' || data.metric === 'max') {
            var span = data.max - data.min;
            return span <= 0 ? STEPS : 1 + Math.floor((v - data.min) / span * (STEPS - 1));
        }
        return step(v, data.max);
    }

    function overClass(data, v) {
        return (data.metric === 'avg' || data.metric === 'max') && data.limit != null && v != null && v > data.limit
            ? ' activity-cell--over' : '';
    }

    function speedValue(data, v) {
        if (v === null || v === undefined) return 'nothing measured';
        if (data.metric === 'avg') return 'average ' + Math.round(v) + ' ' + data.unit;
        if (data.metric === 'max') return 'top ' + Math.round(v) + ' ' + data.unit;
        if (data.metric === 'over') return v.toLocaleString() + ' over the limit';
        return v.toLocaleString() + ' measured';
    }

    function speedCellText(v) { return v === null || v === undefined ? '' : (last.metric === 'avg' || last.metric === 'max') ? Math.round(v) : v.toLocaleString(); }

    var dayNames = { Sun: 'Sunday', Mon: 'Monday', Tue: 'Tuesday', Wed: 'Wednesday', Thu: 'Thursday', Fri: 'Friday', Sat: 'Saturday' };
    var hourLabels = ['12a','1a','2a','3a','4a','5a','6a','7a','8a','9a','10a','11a','12p','1p','2p','3p','4p','5p','6p','7p','8p','9p','10p','11p'];

    function hourText(label) { return label.replace(/a$/, ' AM').replace(/p$/, ' PM'); } // "3p" -> "3 PM"
    function hourRange(i) { return hourText(hourLabels[i]) + '–' + hourText(hourLabels[(i + 1) % 24]); }
    function events(n) { return n.toLocaleString() + (n === 1 ? ' event' : ' events'); }

    // ISO date ("2026-10-03") as a local calendar date, without time-zone shifting.
    function dateOf(iso) { var p = iso.split('-'); return new Date(+p[0], +p[1] - 1, +p[2]); }
    function dayText(iso) { return dateOf(iso).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' }); }

    function cellText(data, row, col) {
        var count = data.cells[row][col];
        var what = speeds() ? speedValue(data, count) : events(count);
        if (data.mode === 'calendar') return dayText(data.columnLabels[col]) + ' · ' + hourRange(row) + ': ' + what;
        if (data.mode === 'day') return data.rowLabels[row] + ', ' + hourText(data.columnLabels[col]) + '–' + hourText(data.columnLabels[(col + 1) % 24]) + ': ' + what;
        var day = dayNames[data.rowLabels[row]] || data.rowLabels[row];
        if (speeds()) return day + 's ' + hourRange(col) + ': ' + what;
        var text = day + 's ' + hourRange(col) + ': ' + events(count);
        // Over more than one week, the total is spread across several of that weekday — give the average.
        var n = data.weekdayOccurrences ? data.weekdayOccurrences[row] : 0;
        if (n > 1) text += ' total · ' + (count / n).toLocaleString(undefined, { maximumFractionDigits: 1 }) + ' per ' + day + ' on average';
        return text;
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    function renderGrid(data) {
        var html = '<div class="activity-grid" role="img" aria-label="' + escapeHtml(data.peak || 'Activity heatmap') + '">';
        html += '<div></div>' + data.columnLabels.map(function (c, i) {
            return '<div class="activity-col-label' + (i % 3 === 0 ? '' : ' activity-col-label--minor') + '">' + escapeHtml(c) + '</div>';
        }).join('');
        data.rowLabels.forEach(function (label, r) {
            html += '<div class="activity-row-label text-truncate" title="' + escapeHtml(label) + '">' + escapeHtml(label) + '</div>';
            data.cells[r].forEach(function (v, c) {
                html += '<div class="activity-cell activity-step-' + cellStep(data, v) + overClass(data, v) + '" data-row="' + r + '" data-col="' + c + '"></div>';
            });
        });
        return html + '</div>';
    }

    // "By day": 24 hour rows × one column per date, month names over each month's first day, and a
    // strip of daily totals underneath (its own scale, so a quiet hour pattern doesn't hide a busy day).
    function renderCalendar(data) {
        var days = data.columnLabels.length;
        // Past ~4 months the 1px gaps between day columns would eat a third of the width; rows keep theirs.
        var cols = 'grid-template-columns: 2.5rem repeat(' + days + ', minmax(0, 1fr));' + (days > 120 ? ' column-gap: 0;' : '');
        var html = '<div class="activity-calendar" style="' + cols + '" role="img" aria-label="' + escapeHtml((data.busiestDay || '') + ' ' + (data.peak || '')) + '">';
        html += '<div></div>' + data.columnLabels.map(function (iso, i) {
            var d = dateOf(iso);
            var label = d.getDate() === 1 || i === 0 ? d.toLocaleDateString(undefined, { month: 'short' }) : '';
            return '<div class="activity-month-label">' + escapeHtml(label) + '</div>';
        }).join('');
        for (var h = 0; h < 24; h++) {
            html += '<div class="activity-row-label">' + (h % 3 === 0 ? hourLabels[h] : '') + '</div>';
            for (var c = 0; c < days; c++) {
                html += '<div class="activity-cell activity-cell--calendar activity-step-' + cellStep(data, data.cells[h][c]) + overClass(data, data.cells[h][c]) + '" data-row="' + h + '" data-col="' + c + '"></div>';
            }
        }
        if (data.dayValues) {
            // Speeds: each day's own value, on its own scale.
            var vals = data.dayValues.filter(function (v) { return v !== null; });
            var dayScale = { metric: data.metric, min: vals.length ? Math.min.apply(null, vals) : 0, max: vals.length ? Math.max.apply(null, vals) : 0 };
            if (data.metric !== 'avg' && data.metric !== 'max') dayScale.min = 0;
            html += '<div class="activity-row-label">Day</div>' + data.dayValues.map(function (v, c) {
                return '<div class="activity-day-total activity-step-' + cellStep(dayScale, v) + overClass(data, v) + '" data-day="' + c + '"></div>';
            }).join('');
        } else {
            var maxDay = Math.max.apply(null, data.dayTotals.concat([0]));
            html += '<div class="activity-row-label">Day</div>' + data.dayTotals.map(function (t, c) {
                return '<div class="activity-day-total activity-step-' + step(t, maxDay) + '" data-day="' + c + '"></div>';
            }).join('');
        }
        return html + '</div>';
    }

    function renderTable(data) {
        var rows = data.rowLabels, cols = data.columnLabels, cell = function (r, c) { return data.cells[r][c]; };
        var colHead = function (c) { return escapeHtml(c); };
        if (data.mode === 'calendar') {
            // One row per day reads better than 365 columns.
            rows = data.columnLabels.map(dayText);
            cols = hourLabels;
            cell = function (r, c) { return data.cells[c][r]; };
        }
        var html = '<div class="table-responsive" style="max-height: 70vh;"><table class="table table-sm table-bordered small text-end mb-0"><thead><tr><th></th>' +
            cols.map(function (c) { return '<th scope="col">' + colHead(c) + '</th>'; }).join('') + '<th scope="col">Total</th></tr></thead><tbody>';
        if (speeds()) {
            // No Total column: an average or a top speed doesn't add up across hours.
            html = html.replace('<th scope="col">Total</th>', '');
            rows.forEach(function (label, r) {
                var tds = '';
                for (var c = 0; c < cols.length; c++) {
                    var v = cell(r, c);
                    tds += '<td' + (overClass(data, v) ? ' class="text-danger fw-semibold"' : '') + '>' + speedCellText(v) + '</td>';
                }
                html += '<tr><th scope="row" class="text-start text-nowrap">' + escapeHtml(label) + '</th>' + tds + '</tr>';
            });
            return html + '</tbody></table></div>';
        }
        rows.forEach(function (label, r) {
            var total = 0, tds = '';
            for (var c = 0; c < cols.length; c++) { var v = cell(r, c); total += v; tds += '<td>' + (v ? v.toLocaleString() : '') + '</td>'; }
            html += '<tr><th scope="row" class="text-start text-nowrap">' + escapeHtml(label) + '</th>' + tds +
                '<td class="fw-semibold">' + total.toLocaleString() + '</td></tr>';
        });
        return html + '</tbody></table></div>';
    }

    function renderChart(data) {
        if (speeds() && data.measured === 0) {
            chartEl.innerHTML = '<p class="text-muted mb-0">No speeds measured in this range. Speeds come from speed traps — set one up on a camera\'s Speed traps page (Cameras → Edit).</p>';
            return;
        }
        if (!speeds() && data.total === 0) {
            chartEl.innerHTML = '<p class="text-muted mb-0">Nothing matching these event types in this range.</p>';
            return;
        }
        chartEl.innerHTML = tableToggle.checked ? renderTable(data)
            : data.mode === 'calendar' ? renderCalendar(data) : renderGrid(data);
    }

    function renderLegend(data) {
        if ((speeds() ? data.measured === 0 : data.total === 0) || tableToggle.checked) { legendEl.innerHTML = ''; return; }
        var swatches = '';
        for (var i = 1; i <= STEPS; i++) swatches += '<span class="activity-swatch activity-step-' + i + '"></span>';
        if (speeds()) {
            var speedScale = data.metric === 'avg' || data.metric === 'max';
            legendEl.innerHTML = speedScale
                ? '<span class="small text-muted">Slower (' + Math.round(data.min) + ' ' + escapeHtml(data.unit) + ')</span>' + swatches +
                  '<span class="small text-muted">Faster (' + Math.round(data.max) + ' ' + escapeHtml(data.unit) + ')</span>' +
                  (data.limit != null ? '<span class="activity-swatch activity-cell--over ms-3"></span><span class="small text-muted">Over the ' + Math.round(data.limit) + ' ' + escapeHtml(data.unit) + ' limit</span>' : '')
                : '<span class="small text-muted">Fewer</span>' + swatches +
                  '<span class="small text-muted">More (up to ' + data.max.toLocaleString() + ' per hour cell)</span>';
            return;
        }
        legendEl.innerHTML = '<span class="small text-muted">Fewer</span>' + swatches +
            '<span class="small text-muted">More (up to ' + data.max.toLocaleString() + ' per hour cell)</span>';
    }

    var rangeNames = { '24h': 'the last 24 hours', '7d': 'the last 7 days', '30d': 'the last 30 days', '90d': 'the last 90 days', '1y': 'the last year' };

    var sceneRangeNames = { '24h': 'the last 24 hours', '7d': 'the last 7 days', '30d': 'the last 30 days', '90d': 'the last 90 days', '1y': 'the last year' };

    function renderScene(data) {
        var motion = data.source === 'motion';
        peakEl.textContent = motion ? 'Where motion happened' : 'Where objects moved';
        var hours = state.fromHour !== state.toHour
            ? ', ' + sceneFrom.options[state.fromHour].text + ' to ' + sceneTo.options[state.toHour].text : '';
        summaryEl.textContent = 'Over ' + sceneRangeNames[data.range] + hours + '. ' + (data.maxCount === 0 ? '' : motion
            ? 'The busiest spot had motion in ' + data.maxCount.toLocaleString() + ' analysed frames.'
            : 'Up to ' + data.maxCount.toLocaleString() + (data.maxCount === 1 ? ' object' : ' objects') + ' passed through the busiest spot.');
        if (data.maxCount === 0) {
            chartEl.innerHTML = '<p class="text-muted mb-0">' + (motion
                ? 'No grid motion recorded in this range. Motion heat needs server-side motion detection in Grid mode on this camera.'
                : 'No moving objects recorded in this range. Paths are recorded from now on, as objects move through the picture.') + '</p>';
            legendEl.innerHTML = '';
        } else {
            window.larisvmsSceneHeatmap.reset();
            window.larisvmsSceneHeatmap.render(chartEl, data, backgroundUrl(data));
            legendEl.innerHTML = window.larisvmsSceneHeatmap.legend(motion
                ? data.maxCount.toLocaleString() + ' frames'
                : data.maxCount.toLocaleString() + (data.maxCount === 1 ? ' object' : ' objects'));
        }
        var motionInput = document.getElementById('activitySceneMotion');
        motionInput.disabled = !data.motionAvailable;
        motionInput.parentElement.title = data.motionAvailable ? '' : 'Needs server-side motion detection in Grid mode on this camera.';
        renderBackgroundControls(data);
    }

    // ── Scene background: the camera's picture, or one uploaded for it ──────
    var backgroundEl = document.getElementById('activitySceneBackground');
    var backgroundFile = document.getElementById('activitySceneBackgroundFile');

    function renderBackgroundControls(data) {
        if (!backgroundEl) return;
        if (!data || !data.canChangeBackground) { backgroundEl.classList.add('d-none'); return; }
        backgroundEl.classList.remove('d-none');
        backgroundEl.querySelector('[data-bg-status]').textContent = data.hasCustomBackground ? 'Your picture' : 'Camera picture';
        backgroundEl.querySelector('[data-bg-upload]').textContent = data.hasCustomBackground ? 'Replace…' : 'Use my own picture…';
        backgroundEl.querySelector('[data-bg-remove]').classList.toggle('d-none', !data.hasCustomBackground);
        backgroundEl.querySelector('[data-bg-error]').textContent = '';
    }

    function backgroundError(text) {
        backgroundEl.querySelector('[data-bg-error]').textContent = text;
    }

    function problemText(r) {
        return r.json().then(function (p) { return (p && (p.detail || p.title)) || 'Couldn\'t save the picture.'; },
            function () { return 'Couldn\'t save the picture.'; });
    }

    if (backgroundEl) {
        backgroundEl.addEventListener('click', function (e) {
            if (e.target.closest('[data-bg-upload]')) { backgroundFile.value = ''; backgroundFile.click(); }
            else if (e.target.closest('[data-bg-remove]') && last) {
                fetch('/api/cameras/' + encodeURIComponent(last.cameraId) + '/scene-background', { method: 'DELETE' })
                    .then(function (r) { if (r.ok) load(); else problemText(r).then(backgroundError); });
            }
        });
        backgroundFile.addEventListener('change', function () {
            var file = backgroundFile.files && backgroundFile.files[0];
            if (!file || !last) return;
            if (file.size > 10 * 1024 * 1024) { backgroundError('The picture must be under 10 MB.'); return; }
            var cameraId = last.cameraId;
            var reader = new FileReader();
            reader.onload = function () {
                var dataUrl = String(reader.result || '');
                var img = new Image();
                img.onload = function () {
                    fetch('/api/cameras/' + encodeURIComponent(cameraId) + '/scene-background', {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ imageBase64: dataUrl.substring(dataUrl.indexOf(',') + 1), width: img.naturalWidth, height: img.naturalHeight })
                    }).then(function (r) { if (r.ok) load(); else problemText(r).then(backgroundError); });
                };
                img.onerror = function () { backgroundError('That file isn\'t a picture this browser can read.'); };
                img.src = dataUrl;
            };
            reader.readAsDataURL(file);
        });
    }

    // The parts of the page that follow the chosen kind, range and options.
    function renderControls() {
        eventCard.classList.toggle('d-none', speeds() || scene());
        speedCard.classList.toggle('d-none', !speeds());
        sceneCard.classList.toggle('d-none', !scene());
        tableSwitch.classList.toggle('d-none', scene());
        Array.prototype.forEach.call(kindGroup.querySelectorAll('[data-kind]'), function (b) {
            var on = b.getAttribute('data-kind') === state.kind;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
        Array.prototype.forEach.call(rangeGroup.querySelectorAll('[data-range]'), function (b) {
            var on = b.getAttribute('data-range') === state.range;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
        viewGroup.classList.toggle('d-none', !longRange() || scene());
        // Views in the camera list only make sense for Scene.
        Array.prototype.forEach.call(cameraSelect.querySelectorAll('[data-view-option]'), function (o) { o.hidden = !scene(); });
        if (!scene() || sceneView()) renderBackgroundControls(null);
        if (scene()) {
            document.getElementById('activitySceneObjects').checked = state.source === 'objects';
            document.getElementById('activitySceneMotion').checked = state.source === 'motion';
            sceneCatsEl.classList.toggle('d-none', state.source !== 'objects');
            Array.prototype.forEach.call(sceneCatsEl.querySelectorAll('[data-scene-cat]'), function (cb) {
                cb.checked = state.cats.length === 0 || state.cats.indexOf(cb.getAttribute('data-scene-cat')) >= 0;
            });
            sceneFrom.value = state.fromHour;
            sceneTo.value = state.toHour;
        }
    }

    function render() {
        var data = last;
        renderControls();
        if (scene()) {
            renderScene(data);
            return;
        }
        peakEl.textContent = [data.busiestDay, data.peak].filter(Boolean).join(' ');
        var where = state.cameraId === 'all'
            ? 'Times in the server\'s time zone (' + data.timeZoneName + ').'
            : 'Times in the camera\'s time zone (' + data.timeZoneName + ').';
        if (speeds()) {
            summaryEl.textContent = data.measured.toLocaleString() + (data.measured === 1 ? ' speed' : ' speeds') + ' measured in ' + rangeNames[data.range] +
                (state.cameraId === 'all' ? ' across all cameras' : '') +
                (data.measured > 0 ? ' · average ' + Math.round(data.average) + ' ' + data.unit + ', top ' + Math.round(data.top) + ' ' + data.unit : '') +
                '. ' + where;
        } else {
            summaryEl.textContent = events(data.total) + ' in ' + rangeNames[data.range] +
                (state.cameraId === 'all' ? ' across all cameras' : '') + '. ' + where;
        }
        renderChart(data);
        renderLegend(data);
        eventCard.classList.toggle('d-none', speeds());
        speedCard.classList.toggle('d-none', !speeds());
        if (speeds()) renderTraps(data); else renderFilter(data);
        Array.prototype.forEach.call(kindGroup.querySelectorAll('[data-kind]'), function (b) {
            var on = b.getAttribute('data-kind') === state.kind;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
        metricSelect.value = state.metric;
        Array.prototype.forEach.call(rangeGroup.querySelectorAll('[data-range]'), function (b) {
            var on = b.getAttribute('data-range') === state.range;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
        viewGroup.classList.toggle('d-none', !longRange());
        Array.prototype.forEach.call(viewGroup.querySelectorAll('[data-view]'), function (b) {
            var on = b.getAttribute('data-view') === state.view;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
    }

    // ── Filter tree ─────────────────────────────────────────────────────────
    // A type's key ("Animal") selects all of it; label keys ("Animal:cat") select just those labels.
    // The tree shows what the server actually applied (last.selection) — a remembered choice this
    // camera has none of falls back to the default there.

    function isSelected(key) { return last && last.selection.indexOf(key) >= 0; }
    function g(key) { return last.groups.filter(function (x) { return x.key === key; })[0]; }

    function renderFilter(data) {
        if (data.groups.length === 0) {
            filterEl.innerHTML = '<span class="text-muted">No events in this range.</span>';
            return;
        }
        filterEl.innerHTML = data.groups.map(function (grp, gi) {
            var whole = isSelected(grp.key);
            var some = !whole && grp.labels.some(function (l) { return isSelected(l.key); });
            var id = 'activityGroup' + gi;
            var labels = grp.labels.length === 0 ? '' :
                '<details class="ms-4"' + (some ? ' open' : '') + '><summary class="text-muted">' + grp.labels.length + ' label' + (grp.labels.length === 1 ? '' : 's') + '</summary>' +
                grp.labels.map(function (l, li) {
                    var lid = id + '_' + li;
                    return '<div class="form-check"><input class="form-check-input" type="checkbox" id="' + lid + '" data-label="' + escapeHtml(l.key) + '" data-group="' + escapeHtml(grp.key) + '"' +
                        (whole || isSelected(l.key) ? ' checked' : '') + '><label class="form-check-label" for="' + lid + '">' + escapeHtml(l.name) +
                        ' <span class="text-muted">' + l.count.toLocaleString() + '</span></label></div>';
                }).join('') + '</details>';
            return '<div class="mb-1"><div class="form-check"><input class="form-check-input" type="checkbox" id="' + id + '" data-group-key="' + escapeHtml(grp.key) + '"' +
                (whole ? ' checked' : '') + '><label class="form-check-label" for="' + id + '">' + (grp.emoji ? escapeHtml(grp.emoji) + ' ' : '') + escapeHtml(grp.name) +
                ' <span class="text-muted">' + grp.count.toLocaleString() + '</span></label></div>' + labels + '</div>';
        }).join('');
        Array.prototype.forEach.call(filterEl.querySelectorAll('[data-group-key]'), function (cb) {
            if (!cb.checked && g(cb.getAttribute('data-group-key')).labels.some(function (l) { return isSelected(l.key); })) cb.indeterminate = true;
        });
    }

    function setSelection(sel) {
        state.selection = sel;
        window.larisvmsPreferences.set(SELECTION_PREF, JSON.stringify(sel));
        load();
    }

    filterEl.addEventListener('change', function (e) {
        var cb = e.target;
        var sel = last.selection.filter(function (k) { return k !== '__none__'; });
        var groupKey = cb.getAttribute('data-group-key');
        if (groupKey) {
            var group = g(groupKey);
            sel = sel.filter(function (k) { return k !== groupKey && group.labels.every(function (l) { return l.key !== k; }); });
            if (cb.checked) sel.push(groupKey);
        } else if (cb.getAttribute('data-label')) {
            var labelKey = cb.getAttribute('data-label');
            var parent = g(cb.getAttribute('data-group'));
            if (sel.indexOf(parent.key) >= 0) {
                // Unticking one label of a fully-selected type: keep the rest of its labels.
                sel = sel.filter(function (k) { return k !== parent.key; });
                parent.labels.forEach(function (l) { if (l.key !== labelKey) sel.push(l.key); });
            } else if (cb.checked) {
                sel.push(labelKey);
                // Every label ticked: collapse back to the type itself.
                if (parent.labels.every(function (l) { return sel.indexOf(l.key) >= 0; })) {
                    sel = sel.filter(function (k) { return parent.labels.every(function (l) { return l.key !== k; }); });
                    sel.push(parent.key);
                }
            } else {
                sel = sel.filter(function (k) { return k !== labelKey; });
            }
        }
        // Nothing ticked would mean "default" to the server — show an explicit empty chart instead.
        setSelection(sel.length > 0 ? sel : ['__none__']);
    });

    resetBtn.addEventListener('click', function () { setSelection([]); });

    // ── Speed traps list (Speeds view) ──────────────────────────────────────
    // Every trap ticked = no narrowing (an empty selection), so a newly added trap is included.

    function renderTraps(data) {
        if (data.traps.length === 0) {
            trapsEl.innerHTML = '<span class="text-muted">No speed traps on ' + (state.cameraId === 'all' ? 'any camera' : 'this camera') + ' yet.</span>';
            return;
        }
        var all = data.selection.length === 0;
        trapsEl.innerHTML = data.traps.map(function (t, i) {
            var id = 'activityTrap' + i;
            return '<div class="form-check"><input class="form-check-input" type="checkbox" id="' + id + '" data-trap="' + escapeHtml(t.id) + '"' +
                (all || data.selection.indexOf(t.id) >= 0 ? ' checked' : '') + '><label class="form-check-label" for="' + id + '">' + escapeHtml(t.name) +
                ' <span class="text-muted">' + t.count.toLocaleString() + '</span></label></div>';
        }).join('');
    }

    trapsEl.addEventListener('change', function () {
        var boxes = Array.prototype.slice.call(trapsEl.querySelectorAll('[data-trap]'));
        var ticked = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b.getAttribute('data-trap'); });
        // All ticked, or none (nothing to show), both mean "every trap".
        state.traps = ticked.length === boxes.length || ticked.length === 0 ? [] : ticked;
        load();
    });

    metricSelect.addEventListener('change', function () {
        state.metric = metricSelect.value;
        window.larisvmsPreferences.set(METRIC_PREF, state.metric);
        load();
    });

    sceneCard.addEventListener('change', function (e) {
        var t = e.target;
        if (t.name === 'activitySceneSource') {
            state.source = t.value;
            window.larisvmsPreferences.set(SCENE_SOURCE_PREF, state.source);
        } else if (t.hasAttribute('data-scene-cat')) {
            var boxes = Array.prototype.slice.call(sceneCatsEl.querySelectorAll('[data-scene-cat]'));
            var ticked = boxes.filter(function (b) { return b.checked; }).map(function (b) { return b.getAttribute('data-scene-cat'); });
            // All ticked means every kind, including any added later; none ticked shows nothing.
            state.cats = ticked.length === boxes.length ? [] : ticked.length === 0 ? ['__none__'] : ticked;
        } else if (t === sceneFrom || t === sceneTo) {
            state.fromHour = +sceneFrom.value;
            state.toHour = +sceneTo.value;
        } else return;
        load();
    });

    kindGroup.addEventListener('click', function (e) {
        var b = e.target.closest('[data-kind]');
        if (!b || b.getAttribute('data-kind') === state.kind) return;
        state.kind = b.getAttribute('data-kind');
        window.larisvmsPreferences.set(KIND_PREF, state.kind);
        load();
    });

    // ── Tooltip ─────────────────────────────────────────────────────────────

    chartEl.addEventListener('mousemove', function (e) {
        if (!last) return;
        var cell = e.target.closest('.activity-cell');
        var dayTotal = e.target.closest('.activity-day-total');
        if (cell) tooltip.textContent = cellText(last, +cell.getAttribute('data-row'), +cell.getAttribute('data-col'));
        else if (dayTotal) {
            var c = +dayTotal.getAttribute('data-day');
            tooltip.textContent = last.dayValues
                ? dayText(last.columnLabels[c]) + ': ' + speedValue(last, last.dayValues[c]) + ' that day'
                : dayText(last.columnLabels[c]) + ': ' + events(last.dayTotals[c]) + ' that day';
        } else { tooltip.classList.add('d-none'); return; }
        tooltip.classList.remove('d-none');
        var x = Math.min(e.clientX + 12, window.innerWidth - tooltip.offsetWidth - 8);
        tooltip.style.left = x + 'px';
        tooltip.style.top = (e.clientY + 16) + 'px';
    });
    chartEl.addEventListener('mouseleave', function () { tooltip.classList.add('d-none'); });

    // ── Controls ────────────────────────────────────────────────────────────

    cameraSelect.addEventListener('change', function () {
        // The event-type choice carries over: type and label names are the same across cameras.
        state.cameraId = cameraSelect.value;
        load();
    });
    rangeGroup.addEventListener('click', function (e) {
        var b = e.target.closest('[data-range]');
        if (!b) return;
        state.range = b.getAttribute('data-range');
        load();
    });
    viewGroup.addEventListener('click', function (e) {
        var b = e.target.closest('[data-view]');
        if (!b) return;
        state.view = b.getAttribute('data-view');
        window.larisvmsPreferences.set(VIEW_PREF, state.view);
        load();
    });
    tableToggle.addEventListener('change', function () { if (last) render(); });

    // Remembered choices load first, so the first chart already uses them.
    window.larisvmsPreferences.whenReady().then(function () {
        if (state.selection.length === 0) {
            try { state.selection = JSON.parse(window.larisvmsPreferences.get(SELECTION_PREF, '[]')) || []; }
            catch (e) { state.selection = []; }
        }
        if (!state.view) state.view = window.larisvmsPreferences.get(VIEW_PREF, 'pattern') === 'calendar' ? 'calendar' : 'pattern';
        if (!state.kind) {
            var k = window.larisvmsPreferences.get(KIND_PREF, 'events');
            state.kind = k === 'speeds' || k === 'scene' ? k : 'events';
        }
        if (!state.source) state.source = window.larisvmsPreferences.get(SCENE_SOURCE_PREF, 'objects') === 'motion' ? 'motion' : 'objects';
        if (!state.metric) {
            var m = window.larisvmsPreferences.get(METRIC_PREF, 'avg');
            state.metric = ['avg', 'max', 'over', 'count'].indexOf(m) >= 0 ? m : 'avg';
        }
        if (state.cameraId) load();
    });
})();

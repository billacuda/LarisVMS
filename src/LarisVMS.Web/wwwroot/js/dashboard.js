// Dashboard (Pages/Index): polls GET /api/dashboard every 60s and redraws the summary tiles + camera
// table from that JSON — IDashboardService.GetHealthAsync backs both this and the page's own
// server-rendered initial load, so the two can never independently drift out of sync. This file is
// the single render path for both the immediate on-load fetch and every subsequent 60s refresh (the
// server-rendered markup is only ever the pre-JS fallback), which is what lets the thumbnail toggle,
// sort, and pagination all compose correctly across a refresh — see composeAfterRender().
(function () {
    'use strict';

    var POLL_INTERVAL_MS = 60000;
    var THUMB_TOGGLE_KEY = 'dashboardShowThumbnails';

    var table = document.getElementById('dashboardTable');
    var tbody = document.getElementById('dashboardTableBody');
    if (!table || !tbody) return;

    var tableWrap = document.getElementById('dashboardTableWrap');
    var emptyMessage = document.getElementById('dashboardEmptyMessage');
    var refreshStatus = document.getElementById('dashboardRefreshStatus');
    var thumbToggle = document.getElementById('dashboardThumbToggle');

    var lastData = null; // most recent /api/dashboard response — re-rendered from on a toggle flip, no refetch needed

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    // Server-backed (see user-preferences.js) — start() below waits for the preferences fetch to
    // resolve before the very first render, specifically so the table's first paint already reflects
    // the right thumbnail-column state instead of flashing it on once the value loads.
    function loadShowThumbnails() {
        return window.larisvmsPreferences.get(THUMB_TOGGLE_KEY, 'false') === 'true';
    }
    function saveShowThumbnails(show) {
        window.larisvmsPreferences.set(THUMB_TOGGLE_KEY, show);
    }

    function renderSummary(data) {
        document.getElementById('dashRecordingCount').textContent = data.recordingCount;
        var notReporting = document.getElementById('dashNotReportingCount');
        notReporting.textContent = data.notReportingCount;
        notReporting.classList.toggle('text-danger', data.notReportingCount > 0);
        document.getElementById('dashDisabledCount').textContent = data.disabledCount;
        var nodesOnline = document.getElementById('dashNodesOnline');
        nodesOnline.textContent = data.nodesOnlineCount + ' / ' + data.nodesTotalCount;
        nodesOnline.classList.toggle('text-danger', data.nodesOnlineCount < data.nodesTotalCount);
    }

    function statusBadge(r) {
        if (!r.cameraEnabled) return '<span class="badge text-bg-secondary">Disabled</span>';
        if (!r.nodeAssigned) return '<span class="badge text-bg-warning">No node</span>';
        if (r.healthFresh) return '<span class="badge text-bg-success">Recording</span>';
        return '<span class="badge text-bg-danger">Not reporting</span>';
    }

    // Orthogonal to statusBadge above — a camera can be recording fine while its AI-detection engine
    // is still cold-building a TensorRT engine (minutes on first start), or has failed to build at all.
    // Rendered as a second, separate badge rather than folded into statusBadge's own logic, since
    // recording health and detection-engine health are two different things a camera can independently
    // be fine or not-fine at. Empty string (nothing rendered) when neither flag is set — the common
    // case once a camera's engine has finished its one-time build.
    function detectionBadge(r) {
        if (r.engineBuildFailed) return ' <span class="badge text-bg-danger" title="AI detection failed to start — check the vision log">AI detection failed</span>';
        if (r.isEngineBuilding) return ' <span class="badge text-bg-info"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Starting AI detection…</span>';
        return '';
    }

    function nodeCell(r) {
        if (!r.nodeAssigned) return '<span class="text-muted">unassigned</span>';
        var badgeClass = r.nodeOnline ? 'text-bg-success' : 'text-bg-secondary';
        return escapeHtml(r.nodeName) + ' <span class="badge ' + badgeClass + ' ms-1">' + (r.nodeOnline ? 'online' : 'offline') + '</span>';
    }

    // Must produce the identical string Pages/Index.cshtml's own AudioSummary does for the same row
    // — the server-rendered table is this table's pre-JS fallback, and the first poll replaces it
    // in place, so a formatting difference would show up as the row visibly changing on load.
    // Unlike fps/bitrate this isn't gated on healthFresh: the codec a camera sends is a property of
    // the stream, not a live measurement, so the last known value stays true while a node is down.
    function audioCell(r) {
        if (!r.audioCodec) return '—';
        if (r.audioSampleRateHz === null || r.audioSampleRateHz === undefined) return escapeHtml(r.audioCodec);
        var khz = (r.audioSampleRateHz / 1000).toFixed(1).replace(/\.0$/, '');
        return escapeHtml(r.audioCodec) + ' ' + khz + ' kHz';
    }

    function renderRow(r, showThumbnails) {
        var thumbHtml = showThumbnails
            ? '<img src="/playback-thumbnail/' + r.cameraId + '/latest" loading="lazy" ' +
              'style="width:64px;height:36px;object-fit:cover;" class="rounded" alt="" ' +
              'onerror="this.style.display=\'none\'">'
            : '';

        var fps = r.healthFresh && r.fps !== null && r.fps !== undefined ? r.fps : null;
        var bitrate = r.healthFresh && r.bitrateKbps !== null && r.bitrateKbps !== undefined ? r.bitrateKbps : null;
        var reported = r.healthReportedAt ? new Date(r.healthReportedAt) : null;

        return '<tr data-camera-id="' + r.cameraId + '">' +
            '<td class="thumb-col">' + thumbHtml + '</td>' +
            '<td><a href="/Cameras/Edit/' + r.cameraId + '">' + escapeHtml(r.cameraName) + '</a></td>' +
            '<td>' + nodeCell(r) + '</td>' +
            '<td>' + statusBadge(r) + detectionBadge(r) + '</td>' +
            '<td class="text-end" data-sort-value="' + (fps !== null ? fps : '') + '">' + (fps !== null ? fps : '—') + '</td>' +
            '<td class="text-end" data-sort-value="' + (bitrate !== null ? bitrate : '') + '">' + (bitrate !== null ? bitrate.toLocaleString() + ' kbps' : '—') + '</td>' +
            '<td data-sort-value="' + (r.audioSampleRateHz !== null && r.audioSampleRateHz !== undefined ? r.audioSampleRateHz : '') + '">' + audioCell(r) + '</td>' +
            '<td class="text-end" data-sort-value="' + (r.reconnectCount !== null && r.reconnectCount !== undefined ? r.reconnectCount : '') + '">' +
                (r.reconnectCount !== null && r.reconnectCount !== undefined ? r.reconnectCount : '—') + '</td>' +
            '<td class="small text-muted" data-sort-value="' + (reported ? reported.getTime() : 0) + '">' +
                (reported ? '<span title="' + reported.toISOString() + '">' + reported.toLocaleString() + '</span>' : '<span>never</span>') +
            '</td>' +
            '</tr>';
    }

    // Re-applies whatever sort the user currently has active and re-slices to their current page —
    // deliberately not the 'larisvms:table-changed' event (which both utilities also listen for),
    // since that event's own contract is "a real user action happened, reset to page 1," and a 60s
    // background refresh redrawing the exact same data is not that.
    function composeAfterRender() {
        if (window.larisvmsSortableTable) window.larisvmsSortableTable.reapply('dashboardTable');
        if (window.larisvmsTablePagination) window.larisvmsTablePagination.refresh('dashboardTable');
    }

    function render(data) {
        lastData = data;
        renderSummary(data);

        var showThumbnails = !!(thumbToggle && thumbToggle.checked);
        table.classList.toggle('hide-thumb-col', !showThumbnails);

        var rows = data.rows || [];
        if (emptyMessage) emptyMessage.style.display = rows.length === 0 ? '' : 'none';
        if (tableWrap) tableWrap.style.display = rows.length === 0 ? 'none' : '';

        tbody.innerHTML = rows.map(function (r) { return renderRow(r, showThumbnails); }).join('');
        composeAfterRender();
    }

    function setRefreshStatus(text, isError) {
        if (!refreshStatus) return;
        refreshStatus.textContent = text;
        refreshStatus.className = 'small ' + (isError ? 'text-danger' : 'text-muted');
    }

    function poll() {
        fetch('/api/dashboard')
            .then(function (resp) { return resp.ok ? resp.json() : Promise.reject(new Error('HTTP ' + resp.status)); })
            .then(function (data) {
                render(data);
                setRefreshStatus('Updated ' + new Date().toLocaleTimeString(), false);
            })
            .catch(function () {
                // Transient network/server hiccup — keep whatever's already on screen (stale data
                // beats a blank page) and just flag that the last refresh attempt failed; the next
                // interval tick tries again on its own, no special retry/backoff needed for a 60s poll.
                setRefreshStatus('Refresh failed — retrying at the next interval', true);
            });
    }

    function initThumbToggle() {
        if (!thumbToggle) return;
        thumbToggle.checked = loadShowThumbnails();
        table.classList.toggle('hide-thumb-col', !thumbToggle.checked);
        thumbToggle.addEventListener('change', function () {
            saveShowThumbnails(thumbToggle.checked);
            // Re-render from cached data rather than refetching — the toggle only changes how known
            // rows are displayed, not what data they show.
            if (lastData) render(lastData);
            else table.classList.toggle('hide-thumb-col', !thumbToggle.checked);
        });
    }

    // Waits for the preferences fetch before the very first poll/render, so the table's first paint
    // already reflects the right thumbnail-column state — user-preferences.js's own GET request
    // already started as soon as its script tag ran (earlier in _Layout.cshtml, before this file),
    // so by the time this resolves it has usually added little to no visible delay.
    window.larisvmsPreferences.whenReady().then(function () {
        initThumbToggle();
        poll();
        setInterval(poll, POLL_INTERVAL_MS);
    });
})();

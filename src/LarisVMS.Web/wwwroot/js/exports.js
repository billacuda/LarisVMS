// Exports page: polls GET /api/exports on an interval while any job is still Queued/Running and
// redraws the table from that JSON, so a running export's status/download link appears without a
// manual refresh. Stops polling once every job on the page is terminal (Done/Failed) — exports are
// occasional and this page is often left open in a background tab, so there's no reason to keep
// hitting the server once there's nothing left that could change.
(function () {
    'use strict';

    var POLL_INTERVAL_MS = 4000;
    var tbody = document.getElementById('exportsTableBody');
    var tableWrap = document.getElementById('exportsTableWrap');
    var emptyMessage = document.getElementById('exportsEmptyMessage');
    if (!tbody) return;

    var pollTimer = null;

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function formatBytes(bytes) {
        if (bytes === null || bytes === undefined || bytes < 0) return '—';
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var value = bytes, unit = 0;
        while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
        return (unit === 0 ? Math.round(value) : value.toFixed(1)) + ' ' + units[unit];
    }

    var JOB_BADGE = { Queued: 'text-bg-secondary', Running: 'text-bg-primary', Done: 'text-bg-success', Failed: 'text-bg-danger' };
    var ITEM_BADGE = JOB_BADGE;

    function renderJob(job) {
        var badge = JOB_BADGE[job.status] || 'text-bg-secondary';
        var inProgress = job.status === 'Queued' || job.status === 'Running';
        var created = new Date(job.createdUtc);
        var from = new Date(job.fromUtc);
        var to = new Date(job.toUtc);

        var itemsHtml = job.items.map(function (item) {
            var itemBadge = ITEM_BADGE[item.status] || 'text-bg-secondary';
            var extra = '';
            if (item.status === 'Done') {
                var sizeSuffix = (item.outputSizeBytes !== null && item.outputSizeBytes !== undefined)
                    ? ' (' + formatBytes(item.outputSizeBytes) + ')' : '';
                extra = '<a class="btn btn-sm btn-outline-primary ms-1" href="/export-download/' + item.id + '">Download' + sizeSuffix + '</a>';
            } else if (item.status === 'Failed' && item.errorMessage) {
                extra = '<span class="text-danger ms-1" title="' + escapeHtml(item.errorMessage) + '">&#9888; ' + escapeHtml(item.errorMessage) + '</span>' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary ms-1 js-retry-export" data-item-id="' + item.id + '">Retry</button>';
            }
            var nodeSuffix = item.nodeName ? ' <span class="text-muted">(' + escapeHtml(item.nodeName) + ')</span>' : '';
            return '<li class="mb-1">' + escapeHtml(item.cameraName) + nodeSuffix + ' <span class="badge ' + itemBadge + '">' + item.status + '</span>' + extra + '</li>';
        }).join('');

        var deleteBtn = inProgress ? '' :
            '<button type="button" class="btn btn-sm btn-outline-danger js-delete-export" data-job-id="' + job.id + '" title="Delete this export">&#128465;</button>';

        return '<tr data-job-id="' + job.id + '">' +
            '<td><span title="' + created.toISOString() + '">' + created.toLocaleString() + '</span>' +
            '<div class="text-muted small">by ' + escapeHtml(job.requestedByUserName || 'unknown') + '</div></td>' +
            '<td class="small">' + from.toLocaleString() + ' &ndash; ' + to.toLocaleString() + '</td>' +
            '<td><span class="badge ' + badge + '">' + job.status + '</span></td>' +
            '<td><ul class="list-unstyled mb-0 small">' + itemsHtml + '</ul></td>' +
            '<td>' + deleteBtn + '</td>' +
            '</tr>';
    }

    function render(jobs) {
        if (emptyMessage) emptyMessage.style.display = jobs.length === 0 ? '' : 'none';
        if (tableWrap) tableWrap.style.display = jobs.length === 0 ? 'none' : '';
        tbody.innerHTML = jobs.map(renderJob).join('');
    }

    function anyInProgress(jobs) {
        return jobs.some(function (j) { return j.status === 'Queued' || j.status === 'Running'; });
    }

    function poll() {
        fetch('/api/exports')
            .then(function (resp) { return resp.ok ? resp.json() : Promise.reject(new Error('HTTP ' + resp.status)); })
            .then(function (jobs) {
                render(jobs);
                if (anyInProgress(jobs)) {
                    pollTimer = setTimeout(poll, POLL_INTERVAL_MS);
                }
            })
            .catch(function () {
                // Transient network/server hiccup — back off and try again rather than giving up
                // polling for the rest of the page's lifetime.
                pollTimer = setTimeout(poll, POLL_INTERVAL_MS);
            });
    }

    // Kick off polling immediately if the server-rendered page already shows something in progress
    // (covers the common case of leaving this page open while an export runs); otherwise the table
    // is already fully rendered and static, so there's nothing to do until a delete/retry happens.
    if (tbody.querySelector('.badge.text-bg-primary, .badge.text-bg-secondary')) {
        pollTimer = setTimeout(poll, POLL_INTERVAL_MS);
    }

    tbody.addEventListener('click', function (ev) {
        var deleteBtn = ev.target.closest('.js-delete-export');
        if (deleteBtn) {
            if (!confirm('Delete this export? Finished output files will be removed.')) return;
            deleteBtn.disabled = true;
            fetch('/api/exports/' + deleteBtn.dataset.jobId, { method: 'DELETE' })
                .then(function (resp) {
                    if (!resp.ok) return resp.text().then(function (t) { throw new Error(t || ('HTTP ' + resp.status)); });
                    if (!pollTimer) poll(); else fetch('/api/exports').then(function (r) { return r.json(); }).then(render);
                })
                .catch(function (err) {
                    alert('Could not delete export: ' + (err && err.message ? err.message : err));
                    deleteBtn.disabled = false;
                });
            return;
        }

        var retryBtn = ev.target.closest('.js-retry-export');
        if (retryBtn) {
            retryBtn.disabled = true;
            fetch('/api/exports/' + retryBtn.dataset.itemId + '/retry', { method: 'POST' })
                .then(function (resp) {
                    if (!resp.ok) return resp.text().then(function (t) { throw new Error(t || ('HTTP ' + resp.status)); });
                    if (!pollTimer) poll(); else fetch('/api/exports').then(function (r) { return r.json(); }).then(render);
                })
                .catch(function (err) {
                    alert('Could not retry export: ' + (err && err.message ? err.message : err));
                    retryBtn.disabled = false;
                });
        }
    });
})();

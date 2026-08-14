// M8 Schedule mode: window editor — list + form panel against /api/cameras/{id}/schedule-windows
// and /api/schedule-windows/{id}. Framework-free, same convention as zones-editor.js/
// event-tag-rules-editor.js. No canvas, no observed-topics panel — a window is just days + a time
// range.
window.larisvmsScheduleEditor = (function () {
    'use strict';

    // Mirrors LarisVMS.Core.Enums.DayOfWeekFlags exactly — Sunday=1 (1<<0) through Saturday=64
    // (1<<6). The list/create/update endpoints return the ScheduleWindow entity's Days as a raw
    // System.Text.Json-serialized enum, i.e. this int, not a name — no string parsing needed to
    // read it back.
    var DAYS = [
        { name: 'Sunday', abbr: 'Sun', bit: 1 },
        { name: 'Monday', abbr: 'Mon', bit: 2 },
        { name: 'Tuesday', abbr: 'Tue', bit: 4 },
        { name: 'Wednesday', abbr: 'Wed', bit: 8 },
        { name: 'Thursday', abbr: 'Thu', bit: 16 },
        { name: 'Friday', abbr: 'Fri', bit: 32 },
        { name: 'Saturday', abbr: 'Sat', bit: 64 }
    ];

    function daysSummary(bitmask) {
        var included = DAYS.filter(function (d) { return (bitmask & d.bit) !== 0; });
        if (included.length === 7) return 'Every day';
        if (included.length === 0) return '(no days selected)';
        return included.map(function (d) { return d.abbr; }).join(', ');
    }

    // HH:mm from the API's TimeOnly (serialized "HH:mm:ss") to a <input type="time"> value, and back.
    function toInputTime(timeOnly) { return (timeOnly || '00:00:00').slice(0, 5); }

    function init(o) {
        var opts = o;
        var windows = [];
        var selectedId = null;

        function renderList() {
            var panel = document.getElementById(opts.listPanelId);
            if (!windows.length) {
                panel.innerHTML = '<p class="text-muted small">No schedule windows yet — with none configured, a Schedule-mode camera keeps everything (fails open).</p>';
                return;
            }
            var sorted = windows.slice().sort(function (a, b) { return a.startTime.localeCompare(b.startTime); });
            var html = '<div class="list-group">';
            sorted.forEach(function (w) {
                html += '<button type="button" class="list-group-item list-group-item-action py-2' +
                    (w.id === selectedId ? ' active' : '') + '" data-window-id="' + w.id + '">' +
                    toInputTime(w.startTime) + '&ndash;' + toInputTime(w.endTime) +
                    (w.endTime < w.startTime ? ' <span class="text-muted small">(crosses midnight)</span>' : '') +
                    (w.isEnabled ? '' : ' <span class="text-muted small">(disabled)</span>') +
                    '<div class="text-muted small">' + daysSummary(w.days) + '</div>' +
                    '</button>';
            });
            html += '</div>';
            panel.innerHTML = html;

            Array.prototype.forEach.call(panel.querySelectorAll('[data-window-id]'), function (el) {
                el.addEventListener('click', function () { selectWindow(el.getAttribute('data-window-id')); });
            });
        }

        async function loadWindows() {
            var resp = await fetch('/api/cameras/' + opts.cameraId + '/schedule-windows');
            windows = resp.ok ? await resp.json() : [];
            renderList();
        }

        function dayCheckboxes() {
            return document.querySelectorAll('.' + opts.dayCheckboxClass);
        }

        function showForm(win) {
            selectedId = win ? win.id : null;
            document.getElementById(opts.formPanelId).style.display = '';
            document.getElementById(opts.formTitleId).textContent = win ? 'Edit window' : 'New window';
            document.getElementById(opts.formIdInputId).value = win ? win.id : '';
            document.getElementById(opts.formStartTimeId).value = win ? toInputTime(win.startTime) : '09:00';
            document.getElementById(opts.formEndTimeId).value = win ? toInputTime(win.endTime) : '17:00';
            document.getElementById(opts.formEnabledId).checked = win ? win.isEnabled : true;

            var mask = win ? win.days : 127; // default to every day for a new window
            Array.prototype.forEach.call(dayCheckboxes(), function (cb) {
                var day = DAYS.find(function (d) { return d.name === cb.value; });
                cb.checked = !!(day && (mask & day.bit));
            });

            document.getElementById(opts.formDeleteBtnId).style.display = win ? '' : 'none';
            renderList();
        }

        function hideForm() {
            selectedId = null;
            document.getElementById(opts.formPanelId).style.display = 'none';
            renderList();
        }

        function selectWindow(id) {
            var win = windows.find(function (w) { return w.id === id; });
            if (win) showForm(win);
        }

        document.getElementById(opts.newBtnId).addEventListener('click', function () { showForm(null); });
        document.getElementById(opts.formCancelBtnId).addEventListener('click', hideForm);

        document.getElementById(opts.formSaveBtnId).addEventListener('click', async function () {
            var id = document.getElementById(opts.formIdInputId).value;
            var checkedNames = Array.prototype.filter.call(dayCheckboxes(), function (cb) { return cb.checked; })
                .map(function (cb) { return cb.value; });
            if (!checkedNames.length) { alert('Select at least one day.'); return; }

            var body = {
                days: checkedNames.join(','),
                startTime: document.getElementById(opts.formStartTimeId).value + ':00',
                endTime: document.getElementById(opts.formEndTimeId).value + ':00',
                isEnabled: document.getElementById(opts.formEnabledId).checked
            };

            var url = id ? '/api/schedule-windows/' + id : '/api/cameras/' + opts.cameraId + '/schedule-windows';
            var method = id ? 'PUT' : 'POST';
            var resp = await fetch(url, { method: method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            if (!resp.ok) { alert('Could not save this window.'); return; }

            hideForm();
            await loadWindows();
        });

        document.getElementById(opts.formDeleteBtnId).addEventListener('click', async function () {
            var id = document.getElementById(opts.formIdInputId).value;
            if (!id || !confirm('Delete this schedule window?')) return;
            var resp = await fetch('/api/schedule-windows/' + id, { method: 'DELETE' });
            if (!resp.ok) { alert('Could not delete this window.'); return; }
            hideForm();
            await loadWindows();
        });

        loadWindows();
    }

    return { init: init };
})();

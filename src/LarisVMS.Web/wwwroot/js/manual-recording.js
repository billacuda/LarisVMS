// Manual recording on live tiles: the ⏺ button, the "this camera or the whole view" prompt, and the
// lower-left indicators. 🔴 means footage is being kept right now, for any reason. Ⓜ️ means a manual
// recording is on. State comes from GET /api/v1/recording, polled by live-view.js's indicator loop, so
// every viewer sees the same thing. The recording itself lives on the server, not in this page.
window.larisvmsManualRecording = (function () {
    'use strict';

    var states = {};
    var viewCameras = function () { return []; };
    var loaded = false;

    function buttonHtml(canRecord) {
        if (!canRecord) return '';
        var label = larisvmsI18n.html('js.manualRecording.start');
        return '<button type="button" class="btn btn-outline-light view-cell-record" style="padding:.1rem .35rem;"' +
            ' title="' + label + '" aria-label="' + label + '">⏺</button>';
    }

    function badgesHtml() {
        var keeping = larisvmsI18n.html('js.manualRecording.recordingInProgress');
        var manual = larisvmsI18n.html('js.manualRecording.manualRecordingActive');
        return '<div class="position-absolute bottom-0 start-0 m-1 live-record-badges">' +
            '<span class="live-record-keeping d-none" role="img" title="' + keeping + '" aria-label="' + keeping + '">🔴</span>' +
            '<span class="live-record-manual d-none" role="img" title="' + manual + '" aria-label="' + manual + '">Ⓜ️</span>' +
            '</div>';
    }

    // Says when a recording on a tile in this view starts or stops, including one someone else changed.
    var announcer = null;
    function announce(key) {
        if (!announcer) {
            announcer = document.createElement('div');
            announcer.className = 'visually-hidden';
            announcer.setAttribute('role', 'status');
            document.body.appendChild(announcer);
        }
        announcer.textContent = larisvmsI18n.t(key);
    }

    function announceChanges(before) {
        document.querySelectorAll('[data-camera-tile]').forEach(function (tile) {
            var id = tile.dataset.cameraTile;
            var was = !!(before[id] && before[id].manual);
            var now = isManual(id);
            if (was !== now) announce(now ? 'js.manualRecording.announceStarted' : 'js.manualRecording.announceStopped');
        });
    }

    function isManual(cameraId) {
        return !!(states[cameraId] && states[cameraId].manual);
    }

    function applyTile(tile) {
        var state = states[tile.dataset.cameraTile] || {};
        var keeping = tile.querySelector('.live-record-keeping');
        var manual = tile.querySelector('.live-record-manual');
        if (keeping) keeping.classList.toggle('d-none', !state.keeping);
        if (manual) manual.classList.toggle('d-none', !state.manual);

        var button = tile.querySelector('.view-cell-record');
        if (button) {
            var label = larisvmsI18n.t(state.manual ? 'js.manualRecording.stop' : 'js.manualRecording.start');
            button.textContent = state.manual ? '⏹' : '⏺';
            button.title = label;
            button.setAttribute('aria-label', label);
        }
    }

    function applyAll() {
        document.querySelectorAll('[data-camera-tile]').forEach(applyTile);
    }

    async function refresh() {
        var list;
        try {
            var response = await fetch('/api/v1/recording');
            if (!response.ok) return; // keep showing the last known state
            list = await response.json();
        } catch (e) {
            return; // transient failure, the next poll tries again
        }
        var before = states;
        var firstLoad = !loaded;
        loaded = true;
        states = {};
        list.forEach(function (state) { states[state.cameraId] = state; });
        applyAll();
        if (!firstLoad) announceChanges(before);
    }

    // Everything here is appended to the fullscreen element when there is one: a dialog anywhere else
    // doesn't display while a tile is fullscreen.
    function openDialog(message, buttons) {
        var dialog = document.createElement('dialog');
        dialog.className = 'lv-record-dialog';

        var text = document.createElement('p');
        text.id = 'lvRecordDialogText';
        text.textContent = message;
        dialog.appendChild(text);
        dialog.setAttribute('aria-labelledby', text.id);

        var row = document.createElement('div');
        row.className = 'd-flex flex-wrap gap-2 justify-content-end';
        buttons.forEach(function (b) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn btn-sm ' + (b.primary ? 'btn-danger' : 'btn-outline-secondary');
            button.textContent = b.label;
            button.addEventListener('click', function () {
                dialog.close();
                if (b.action) b.action();
            });
            row.appendChild(button);
        });
        dialog.appendChild(row);

        dialog.addEventListener('close', function () { dialog.remove(); });
        (document.fullscreenElement || document.body).appendChild(dialog);
        dialog.showModal();
    }

    function notify(message) {
        openDialog(message, [{ label: larisvmsI18n.t('js.manualRecording.ok'), primary: true }]);
    }

    async function send(cameraIds, start) {
        var result;
        try {
            var response = await fetch('/api/v1/recording/' + (start ? 'start' : 'stop'), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ cameraIds: cameraIds })
            });
            if (!response.ok) {
                notify(larisvmsI18n.t('js.manualRecording.couldNotChange'));
                return;
            }
            result = await response.json();
        } catch (e) {
            notify(larisvmsI18n.t('js.manualRecording.couldNotChange'));
            return;
        }

        var before = Object.assign({}, states);
        // Shown straight away rather than on the next poll.
        result.changed.concat(result.unchanged).forEach(function (cameraId) {
            var state = Object.assign({ cameraId: cameraId }, states[cameraId]);
            state.manual = start;
            if (start) state.keeping = true;
            states[cameraId] = state;
        });
        applyAll();
        announceChanges(before);
        refresh();

        if (result.skipped.length > 0) {
            notify(larisvmsI18n.t('js.manualRecording.someSkipped', { count: result.skipped.length }));
        }
    }

    function onClick(cameraId) {
        var start = !isManual(cameraId);
        // Only offer "all" when it would change more than this one camera.
        var eligible = viewCameras().filter(function (id, index, all) {
            return all.indexOf(id) === index && isManual(id) !== start;
        });
        if (eligible.length < 2) {
            send([cameraId], start);
            return;
        }

        openDialog(larisvmsI18n.t(start ? 'js.manualRecording.startWhich' : 'js.manualRecording.stopWhich'), [
            { label: larisvmsI18n.t('js.manualRecording.cancel') },
            { label: larisvmsI18n.t('js.manualRecording.thisCamera'), action: function () { send([cameraId], start); } },
            {
                label: larisvmsI18n.t('js.manualRecording.allCameras', { count: eligible.length }),
                primary: true,
                action: function () { send(eligible, start); }
            }
        ]);
    }

    function wire(tile, cameraId) {
        var button = tile.querySelector('.view-cell-record');
        if (button) {
            button.addEventListener('click', function (e) {
                e.stopPropagation();
                onClick(cameraId);
            });
        }
        applyTile(tile);
    }

    // getViewCameras returns the ids in the current view that this user may record.
    function init(getViewCameras) {
        viewCameras = getViewCameras;
        refresh();
    }

    return { buttonHtml: buttonHtml, badgesHtml: badgesHtml, wire: wire, init: init, refresh: refresh };
})();

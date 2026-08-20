// PTZ controls (M18 "basic PTZ") — a compact directional pad + zoom buttons, shown only for a camera
// with CameraCapabilities.HasPtz (server-decided; a camera that wasn't probed as PTZ-capable gets no
// markup at all, same "nothing to render" shape as audio-controls.js's own html() for a silent
// stream). Continuous move while a direction is held (mouse or touch), re-issued on a short interval
// so the server-side dead-man's-switch (PtzService's own device auto-stop timeout) never fires while
// a control is genuinely still held, with an explicit Stop call on release/leave/cancel so a camera
// never keeps panning after the control was let go — the same two-sided safety net
// OnvifPtzClient.ContinuousMoveAsync's own doc comment describes.
window.larisvmsPtzControls = (function () {
    'use strict';

    var MOVE_REISSUE_MS = 2000; // comfortably inside PtzService's own 5s device-side timeout

    function dirBtn(panX, tiltY, label) {
        return '<button type="button" class="btn btn-outline-light btn-sm p-0 ptz-dir" ' +
            'style="width:1.6rem; height:1.6rem; line-height:1;" ' +
            'data-ptz-pan="' + panX + '" data-ptz-tilt="' + tiltY + '" ' +
            'title="Pan/tilt" aria-label="Pan/tilt">' + label + '</button>';
    }

    function zoomBtn(zoomX, label) {
        return '<button type="button" class="btn btn-outline-light btn-sm p-0 ptz-zoom" ' +
            'style="width:1.6rem; height:1.6rem; line-height:1;" ' +
            'data-ptz-zoom="' + zoomX + '" title="Zoom" aria-label="Zoom">' + label + '</button>';
    }

    /// Markup for one tile's PTZ pad, hidden (d-none) until toggled on — call sites drop this inside
    /// their own controls container and wire up a toggle button that removes d-none, the same shape
    /// as a View cell's mini-timeline area. Returns an empty string for a non-PTZ camera.
    function html(hasPtz) {
        if (!hasPtz) return '';
        return (
            '<div class="ptz-pad d-none bg-dark bg-opacity-75 rounded p-1" style="pointer-events:auto;">' +
                '<div style="display:grid; grid-template-columns:repeat(3,1.6rem); grid-auto-rows:1.6rem; gap:2px;">' +
                    dirBtn(-1, 1, '↖') + dirBtn(0, 1, '↑') + dirBtn(1, 1, '↗') +
                    dirBtn(-1, 0, '←') + '<span></span>' + dirBtn(1, 0, '→') +
                    dirBtn(-1, -1, '↙') + dirBtn(0, -1, '↓') + dirBtn(1, -1, '↘') +
                '</div>' +
                '<div class="d-flex gap-1 mt-1">' +
                    zoomBtn(-1, '−') + zoomBtn(1, '+') +
                '</div>' +
            '</div>'
        );
    }

    /// containerEl is whatever html() rendered into (the .ptz-pad element itself, or an ancestor —
    /// resolved via querySelector either way). cameraId drives the /api/cameras/{id}/ptz/* routes.
    function wire(containerEl, cameraId) {
        if (!containerEl || !cameraId) return;
        var pad = containerEl.classList && containerEl.classList.contains('ptz-pad')
            ? containerEl : containerEl.querySelector('.ptz-pad');
        if (!pad) return; // non-PTZ camera — html() rendered nothing

        var reissueTimer = null;
        var activePointerId = null;

        function post(path, body) {
            return fetch('/api/cameras/' + cameraId + '/ptz/' + path, {
                method: 'POST',
                headers: body ? { 'Content-Type': 'application/json' } : undefined,
                body: body ? JSON.stringify(body) : undefined
            }).catch(function () { /* best-effort — a dropped PTZ command isn't worth surfacing an error for */ });
        }

        function startMove(panX, tiltY, zoomX) {
            var body = { panX: panX, tiltY: tiltY, zoomX: zoomX };
            post('move', body);
            if (reissueTimer) clearInterval(reissueTimer);
            reissueTimer = setInterval(function () { post('move', body); }, MOVE_REISSUE_MS);
        }

        function stopMove() {
            if (reissueTimer) { clearInterval(reissueTimer); reissueTimer = null; }
            post('stop');
        }

        function wireHoldButton(btn, panX, tiltY, zoomX) {
            btn.addEventListener('pointerdown', function (e) {
                if (e.button !== 0) return;
                e.preventDefault();
                e.stopPropagation();
                activePointerId = e.pointerId;
                btn.setPointerCapture(e.pointerId);
                startMove(panX, tiltY, zoomX);
            });
            function end(e) {
                if (e.pointerId !== activePointerId) return;
                activePointerId = null;
                if (btn.hasPointerCapture(e.pointerId)) btn.releasePointerCapture(e.pointerId);
                stopMove();
            }
            btn.addEventListener('pointerup', end);
            btn.addEventListener('pointercancel', end);
            // A press that drags off the button before releasing (common on touch) must still stop —
            // pointer capture keeps the up/cancel events coming to this same element regardless of
            // where the pointer physically is by then, so 'leave' itself needs no separate handler.
        }

        pad.querySelectorAll('.ptz-dir').forEach(function (btn) {
            var panX = Number(btn.getAttribute('data-ptz-pan'));
            var tiltY = Number(btn.getAttribute('data-ptz-tilt'));
            wireHoldButton(btn, panX, tiltY, 0);
        });
        pad.querySelectorAll('.ptz-zoom').forEach(function (btn) {
            var zoomX = Number(btn.getAttribute('data-ptz-zoom'));
            wireHoldButton(btn, 0, 0, zoomX);
        });
    }

    return { html: html, wire: wire };
})();

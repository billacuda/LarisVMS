// M8 zone editor — polygon drawing on a canvas over the camera's live snapshot, framework-free like
// the rest of this app's JS (timeline.js's own header comment states that convention explicitly;
// this follows it). One editor covers all four ZoneKinds (see Zone.cs's doc comment for what each
// kind actually does as of pass 1) — only the Kind dropdown changes, the drawing/CRUD flow is
// identical for all of them.
//
// Pass 1 deliberately doesn't support reshaping a saved zone's polygon — editing an existing zone
// only touches name/kind/sensitivity/enabled. To change the shape, delete and redraw. A full
// vertex-drag editor is real scope on its own and wasn't worth blocking the rest of M8 on.
window.larisvmsZonesEditor = (function () {
    'use strict';

    var KIND_COLORS = {
        ServerMotion: 'rgba(255,193,7,0.85)',   // amber — matches nothing else on the timeline, so
                                                 // a zone's own boundary is never confused with the
                                                 // green "motion detected" bands it produces
        Ignore: 'rgba(220,53,69,0.85)',
        CameraMotion: 'rgba(13,110,253,0.85)',
        Privacy: 'rgba(108,117,125,0.85)'
    };
    var KIND_LABELS = {
        ServerMotion: 'Motion (server)',
        Ignore: 'Ignore',
        CameraMotion: 'Motion (camera) — inactive',
        Privacy: 'Privacy — inactive'
    };

    // Closing a polygon by clicking back near its first point needs a forgiving radius in *screen*
    // pixels, not fractional canvas coords — otherwise it's effectively unhittable on a small tile
    // or a high-DPI display where 1 fractional unit covers many device pixels.
    var CLOSE_RADIUS_PX = 12;

    function init(o) {
        var opts = o;
        var canvas = document.getElementById(opts.canvasId);
        var ctx = canvas.getContext('2d');

        var zones = [];
        var snapshotImg = new Image();
        var snapshotLoaded = false;
        var snapshotFailed = false;

        var drawing = false;
        var drawPoints = []; // [{x,y}] fractional 0-1
        var selectedZoneId = null;

        function resizeCanvas() {
            var rect = canvas.getBoundingClientRect();
            var dpr = window.devicePixelRatio || 1;
            canvas.width = Math.max(1, Math.round(rect.width * dpr));
            canvas.height = Math.max(1, Math.round(rect.height * dpr));
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        }

        function fracToPixel(p) {
            return { x: p.x * canvas.clientWidth, y: p.y * canvas.clientHeight };
        }

        function pixelToFrac(clientX, clientY) {
            var rect = canvas.getBoundingClientRect();
            return {
                x: Math.min(1, Math.max(0, (clientX - rect.left) / rect.width)),
                y: Math.min(1, Math.max(0, (clientY - rect.top) / rect.height))
            };
        }

        function drawPolygonPath(points) {
            ctx.beginPath();
            points.forEach(function (p, i) {
                var px = fracToPixel(p);
                if (i === 0) ctx.moveTo(px.x, px.y); else ctx.lineTo(px.x, px.y);
            });
        }

        function redraw() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            if (!w || !h) return;
            ctx.clearRect(0, 0, w, h);

            if (snapshotLoaded) {
                ctx.drawImage(snapshotImg, 0, 0, w, h);
            } else {
                ctx.fillStyle = '#2b2b2b';
                ctx.fillRect(0, 0, w, h);
                ctx.fillStyle = '#aaa';
                ctx.font = '13px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText(snapshotFailed
                    ? 'Could not load a snapshot from this camera — zones can still be drawn without one.'
                    : 'Loading snapshot…', w / 2, h / 2);
                ctx.textAlign = 'left';
            }

            zones.forEach(function (z) {
                var points = parsePolygon(z.polygonJson);
                if (points.length < 3) return;
                var color = KIND_COLORS[z.kind] || KIND_COLORS.ServerMotion;
                var isSelected = z.id === selectedZoneId;

                drawPolygonPath(points);
                ctx.closePath();
                ctx.fillStyle = color.replace('0.85', z.isEnabled ? '0.20' : '0.08');
                ctx.fill();
                ctx.strokeStyle = color;
                ctx.lineWidth = isSelected ? 3 : 1.5;
                if (!z.isEnabled) ctx.setLineDash([6, 4]); else ctx.setLineDash([]);
                ctx.stroke();
                ctx.setLineDash([]);

                if (points.length) {
                    var labelPos = fracToPixel(points[0]);
                    ctx.fillStyle = '#fff';
                    ctx.font = 'bold 11px sans-serif';
                    ctx.fillText(z.name, labelPos.x + 4, labelPos.y - 4);
                }
            });

            if (drawing && drawPoints.length > 0) {
                drawPolygonPath(drawPoints);
                ctx.strokeStyle = '#ffc107';
                ctx.lineWidth = 2;
                ctx.setLineDash([4, 4]);
                ctx.stroke();
                ctx.setLineDash([]);

                drawPoints.forEach(function (p, i) {
                    var px = fracToPixel(p);
                    ctx.beginPath();
                    ctx.arc(px.x, px.y, i === 0 ? 6 : 4, 0, Math.PI * 2);
                    ctx.fillStyle = i === 0 ? '#fff' : '#ffc107';
                    ctx.fill();
                    ctx.strokeStyle = '#000';
                    ctx.lineWidth = 1;
                    ctx.stroke();
                });
            }
        }

        function parsePolygon(polygonJson) {
            try {
                var parsed = JSON.parse(polygonJson);
                if (parsed && Array.isArray(parsed.points)) {
                    return parsed.points.map(function (p) { return { x: p[0], y: p[1] }; });
                }
            } catch (e) { /* corrupted row — treat as empty rather than fail the whole editor */ }
            return [];
        }

        function polygonJsonFromPoints(points) {
            return JSON.stringify({ points: points.map(function (p) { return [p.x, p.y]; }) });
        }

        // ── Snapshot loading ────────────────────────────────────────────────
        snapshotImg.onload = function () { snapshotLoaded = true; redraw(); };
        snapshotImg.onerror = function () { snapshotFailed = true; redraw(); };
        snapshotImg.src = opts.snapshotUrl;

        // ── Zone list / load ────────────────────────────────────────────────
        function escHtml(s) {
            return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
        }

        function renderList() {
            var panel = document.getElementById(opts.listPanelId);
            if (!zones.length) {
                panel.innerHTML = '<p class="text-muted small">No zones yet — draw one to get started.</p>';
                return;
            }
            var html = '<div class="list-group">';
            zones.forEach(function (z) {
                var color = KIND_COLORS[z.kind] || KIND_COLORS.ServerMotion;
                html += '<button type="button" class="list-group-item list-group-item-action py-2' +
                    (z.id === selectedZoneId ? ' active' : '') + '" data-zone-id="' + z.id + '">' +
                    '<span class="d-inline-block rounded-circle me-2" style="width:10px;height:10px;background:' + color + ';"></span>' +
                    escHtml(z.name) +
                    (z.isEnabled ? '' : ' <span class="text-muted small">(disabled)</span>') +
                    '<div class="text-muted small">' + (KIND_LABELS[z.kind] || z.kind) + '</div>' +
                    '</button>';
            });
            html += '</div>';
            panel.innerHTML = html;

            Array.prototype.forEach.call(panel.querySelectorAll('[data-zone-id]'), function (el) {
                el.addEventListener('click', function () { selectZone(el.getAttribute('data-zone-id')); });
            });
        }

        async function loadZones() {
            var resp = await fetch('/api/cameras/' + opts.cameraId + '/zones');
            zones = resp.ok ? await resp.json() : [];
            renderList();
            redraw();
        }

        // ── Form panel ──────────────────────────────────────────────────────
        function showForm(zone, polygonJsonForNew) {
            selectedZoneId = zone ? zone.id : null;
            document.getElementById(opts.formPanelId).style.display = '';
            document.getElementById(opts.formTitleId).textContent = zone ? ('Edit zone: ' + zone.name) : 'New zone';
            document.getElementById(opts.formIdInputId).value = zone ? zone.id : '';
            document.getElementById(opts.formPolygonInputId).value = zone ? zone.polygonJson : polygonJsonForNew;
            document.getElementById(opts.formNameId).value = zone ? zone.name : '';
            document.getElementById(opts.formKindId).value = zone ? zone.kind : 'ServerMotion';
            // 0.03 (3%), not the 0.15 this originally shipped with — see Zone.Sensitivity's doc
            // comment for the real-camera-frame measurement behind that change.
            var sensitivityPercent = Math.round((zone ? zone.sensitivity : 0.03) * 100);
            document.getElementById(opts.formSensitivityId).value = sensitivityPercent;
            document.getElementById(opts.formSensitivityValueId).textContent = sensitivityPercent + '%';
            document.getElementById(opts.formEnabledId).checked = zone ? zone.isEnabled : true;
            document.getElementById(opts.formDeleteBtnId).style.display = zone ? '' : 'none';
            updateSensitivityVisibility();
            renderList();
            redraw();
        }

        function hideForm() {
            selectedZoneId = null;
            document.getElementById(opts.formPanelId).style.display = 'none';
            renderList();
            redraw();
        }

        function updateSensitivityVisibility() {
            var kind = document.getElementById(opts.formKindId).value;
            document.getElementById(opts.formSensitivityGroupId).style.display =
                kind === 'ServerMotion' ? '' : 'none';
        }

        function selectZone(id) {
            var zone = zones.find(function (z) { return z.id === id; });
            if (zone) showForm(zone, zone.polygonJson);
        }

        document.getElementById(opts.formKindId).addEventListener('change', updateSensitivityVisibility);
        document.getElementById(opts.formSensitivityId).addEventListener('input', function () {
            document.getElementById(opts.formSensitivityValueId).textContent = this.value + '%';
        });
        document.getElementById(opts.formCancelBtnId).addEventListener('click', hideForm);

        document.getElementById(opts.formSaveBtnId).addEventListener('click', async function () {
            var id = document.getElementById(opts.formIdInputId).value;
            var body = {
                name: document.getElementById(opts.formNameId).value.trim(),
                kind: document.getElementById(opts.formKindId).value,
                polygonJson: document.getElementById(opts.formPolygonInputId).value,
                sensitivity: Number(document.getElementById(opts.formSensitivityId).value) / 100,
                isEnabled: document.getElementById(opts.formEnabledId).checked
            };
            if (!body.name) { alert('Name is required.'); return; }

            var url = id ? '/api/zones/' + id : '/api/cameras/' + opts.cameraId + '/zones';
            var method = id ? 'PUT' : 'POST';
            var resp = await fetch(url, { method: method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            if (!resp.ok) { alert('Could not save this zone.'); return; }

            hideForm();
            await loadZones();
        });

        document.getElementById(opts.formDeleteBtnId).addEventListener('click', async function () {
            var id = document.getElementById(opts.formIdInputId).value;
            if (!id || !confirm('Delete this zone?')) return;
            var resp = await fetch('/api/zones/' + id, { method: 'DELETE' });
            if (!resp.ok) { alert('Could not delete this zone.'); return; }
            hideForm();
            await loadZones();
        });

        // ── Drawing ─────────────────────────────────────────────────────────
        function setDrawingUi(on) {
            document.getElementById(opts.drawBtnId).style.display = on ? 'none' : '';
            document.getElementById(opts.cancelDrawBtnId).style.display = on ? '' : 'none';
            document.getElementById(opts.undoPointBtnId).style.display = on ? '' : 'none';
            document.getElementById(opts.statusId).textContent = on
                ? 'Click to place points; click the first point again (or double-click) to finish.'
                : '';
        }

        function startDrawing() {
            hideForm();
            drawing = true;
            drawPoints = [];
            setDrawingUi(true);
            redraw();
        }

        function cancelDrawing() {
            drawing = false;
            drawPoints = [];
            setDrawingUi(false);
            redraw();
        }

        function finishDrawing() {
            drawing = false;
            setDrawingUi(false);
            var points = drawPoints.slice();
            drawPoints = [];
            redraw();
            showForm(null, polygonJsonFromPoints(points));
        }

        document.getElementById(opts.drawBtnId).addEventListener('click', startDrawing);
        document.getElementById(opts.cancelDrawBtnId).addEventListener('click', cancelDrawing);
        document.getElementById(opts.undoPointBtnId).addEventListener('click', function () {
            drawPoints.pop();
            redraw();
        });

        canvas.addEventListener('click', function (e) {
            if (!drawing) return;
            var p = pixelToFrac(e.clientX, e.clientY);

            if (drawPoints.length >= 3) {
                var first = fracToPixel(drawPoints[0]);
                var rect = canvas.getBoundingClientRect();
                var dx = (e.clientX - rect.left) - first.x;
                var dy = (e.clientY - rect.top) - first.y;
                if (Math.sqrt(dx * dx + dy * dy) <= CLOSE_RADIUS_PX) {
                    finishDrawing();
                    return;
                }
            }

            drawPoints.push(p);
            redraw();
        });

        canvas.addEventListener('dblclick', function (e) {
            if (!drawing || drawPoints.length < 3) return;
            e.preventDefault();
            finishDrawing();
        });

        window.addEventListener('resize', function () { resizeCanvas(); redraw(); });

        resizeCanvas();
        loadZones();
    }

    return { init: init };
})();

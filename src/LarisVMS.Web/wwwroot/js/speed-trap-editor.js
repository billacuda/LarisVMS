// Speed trap editor (Cameras → Speed traps). Each trap is two gate lines, A and B, drawn across the
// road, plus the real distance between them. Coordinates are fractions (0-1) of the camera frame,
// the same space as zones and detection boxes: {"a":[[x,y],[x,y]],"b":[[x,y],[x,y]]}.
//
// Drawing: click the two ends of gate A, then the two ends of gate B. Afterwards any end can be
// dragged. The live video (with the AI overlay showing each moving object's speed) sits under the
// canvas; until it plays, a still snapshot is drawn instead.
(function () {
    'use strict';

    var HANDLE_RADIUS = 7;
    var HIT_RADIUS = 12;
    var FEET_PER_METRE = 3.28084;
    var KM_PER_MILE = 1.609344;
    var GATE_COLORS = { a: '#f59e0b', b: '#22d3ee' };

    function init(opts) {
        var canvas = document.getElementById('trapCanvas');
        var videoEl = document.getElementById('trapLiveVideo');
        var listPanel = document.getElementById('trapListPanel');
        var formPanel = document.getElementById('trapFormPanel');
        var formTitle = document.getElementById('trapFormTitle');
        var formName = document.getElementById('trapFormName');
        var formDistance = document.getElementById('trapFormDistance');
        var formUnit = document.getElementById('trapFormDistanceUnit');
        var formEnabled = document.getElementById('trapFormEnabled');
        var formLimit = document.getElementById('trapFormLimit');
        var mph = opts.speedUnit === 'mph';
        function limitToDisplay(kph) { return Math.round((mph ? kph / KM_PER_MILE : kph) * 10) / 10; }
        function limitToKph(v) { return mph ? v * KM_PER_MILE : v; }
        var formError = document.getElementById('trapFormError');
        var saveBtn = document.getElementById('trapFormSaveBtn');
        var cancelBtn = document.getElementById('trapFormCancelBtn');
        var deleteBtn = document.getElementById('trapFormDeleteBtn');
        var newBtn = document.getElementById('trapNewBtn');
        var redrawBtn = document.getElementById('trapRedrawBtn');
        var showBoxes = document.getElementById('trapShowBoxes');
        var statusEl = document.getElementById('trapStatus');
        var hintEl = document.getElementById('trapHint');
        var ctx = canvas.getContext('2d');

        var traps = [];
        // The trap being created or edited: {id|null, a:[[x,y],[x,y]]|[], b:[...]|[]}. Null = nothing open.
        var draft = null;
        var dragging = null; // {gate: 'a'|'b', index: 0|1}
        var snapshot = new Image();
        var snapshotLoaded = false;
        var videoHasFrame = false;

        // ── Geometry ────────────────────────────────────────────────────────
        // Where the frame actually sits inside the canvas (object-fit: contain).
        function frameRect() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            var nw = 16, nh = 9;
            if (videoHasFrame && videoEl.videoWidth > 0) { nw = videoEl.videoWidth; nh = videoEl.videoHeight; }
            else if (snapshotLoaded && snapshot.naturalWidth > 0) { nw = snapshot.naturalWidth; nh = snapshot.naturalHeight; }
            var scale = Math.min(w / nw, h / nh);
            var dw = nw * scale, dh = nh * scale;
            return { x: (w - dw) / 2, y: (h - dh) / 2, w: dw, h: dh };
        }

        function toCanvas(p) {
            var r = frameRect();
            return { x: r.x + p[0] * r.w, y: r.y + p[1] * r.h };
        }

        function toFrame(evt) {
            var b = canvas.getBoundingClientRect();
            var r = frameRect();
            var x = (evt.clientX - b.left - r.x) / r.w;
            var y = (evt.clientY - b.top - r.y) / r.h;
            return [Math.min(1, Math.max(0, x)), Math.min(1, Math.max(0, y))];
        }

        function parseGates(json) {
            try {
                var g = JSON.parse(json);
                if (g && Array.isArray(g.a) && Array.isArray(g.b)) return { a: g.a, b: g.b };
            } catch (e) { /* fall through */ }
            return { a: [], b: [] };
        }

        // ── Drawing ─────────────────────────────────────────────────────────
        function redraw() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
            ctx.clearRect(0, 0, w, h);

            if (!videoHasFrame) {
                var r = frameRect();
                if (snapshotLoaded) {
                    ctx.drawImage(snapshot, r.x, r.y, r.w, r.h);
                } else {
                    ctx.fillStyle = '#6c757d';
                    ctx.font = '14px sans-serif';
                    ctx.fillText('Loading picture…', 12, 24);
                }
            }

            traps.forEach(function (t) {
                if (draft && draft.id === t.id) return;
                drawTrap(parseGates(t.gatesJson), t.name, false);
            });
            if (draft) drawTrap(draft, formName.value || 'New speed trap', true);
        }

        function drawTrap(g, name, active) {
            ctx.save();
            ctx.globalAlpha = active ? 1 : 0.5;
            if (g.a.length === 2 && g.b.length === 2) {
                // The measured stretch between the gates, lightly shaded.
                var a0 = toCanvas(g.a[0]), a1 = toCanvas(g.a[1]), b0 = toCanvas(g.b[0]), b1 = toCanvas(g.b[1]);
                ctx.fillStyle = 'rgba(255,255,255,0.12)';
                ctx.beginPath();
                ctx.moveTo(a0.x, a0.y); ctx.lineTo(a1.x, a1.y);
                // Join the nearer ends so the shape doesn't cross itself when B was drawn the other way.
                var straight = dist(a1, b1) + dist(a0, b0) <= dist(a1, b0) + dist(a0, b1);
                if (straight) { ctx.lineTo(b1.x, b1.y); ctx.lineTo(b0.x, b0.y); }
                else { ctx.lineTo(b0.x, b0.y); ctx.lineTo(b1.x, b1.y); }
                ctx.closePath();
                ctx.fill();
            }
            drawGate(g.a, 'A', GATE_COLORS.a, active);
            drawGate(g.b, 'B', GATE_COLORS.b, active);
            if (g.a.length >= 1) {
                var p = toCanvas(g.a[0]);
                ctx.font = '12px sans-serif';
                ctx.fillStyle = '#fff';
                ctx.strokeStyle = 'rgba(0,0,0,0.7)';
                ctx.lineWidth = 3;
                ctx.strokeText(name, p.x + 8, p.y - 8);
                ctx.fillText(name, p.x + 8, p.y - 8);
            }
            ctx.restore();
        }

        function drawGate(points, letter, color, active) {
            if (points.length === 0) return;
            var p0 = toCanvas(points[0]);
            ctx.strokeStyle = color;
            ctx.fillStyle = color;
            ctx.lineWidth = 3;
            if (points.length === 2) {
                var p1 = toCanvas(points[1]);
                ctx.beginPath(); ctx.moveTo(p0.x, p0.y); ctx.lineTo(p1.x, p1.y); ctx.stroke();
                var mx = (p0.x + p1.x) / 2, my = (p0.y + p1.y) / 2;
                ctx.beginPath(); ctx.arc(mx, my, 10, 0, Math.PI * 2); ctx.fill();
                ctx.fillStyle = '#111';
                ctx.font = 'bold 12px sans-serif';
                ctx.fillText(letter, mx - 4, my + 4);
                ctx.fillStyle = color;
            }
            if (active) {
                points.forEach(function (pt) {
                    var c = toCanvas(pt);
                    ctx.beginPath(); ctx.arc(c.x, c.y, HANDLE_RADIUS, 0, Math.PI * 2); ctx.fill();
                });
            }
        }

        function dist(p, q) { return Math.hypot(p.x - q.x, p.y - q.y); }

        // ── Canvas input ────────────────────────────────────────────────────
        function drawingStage() {
            if (!draft) return null;
            if (draft.a.length < 2) return 'a';
            if (draft.b.length < 2) return 'b';
            return null;
        }

        function updateHint() {
            var stage = drawingStage();
            if (!draft) hintEl.textContent = 'Select a speed trap to adjust it, or start a new one.';
            else if (stage === 'a') hintEl.textContent = draft.a.length === 0 ? 'Click one end of line A (where vehicles cross first).' : 'Click the other end of line A.';
            else if (stage === 'b') hintEl.textContent = draft.b.length === 0 ? 'Click one end of line B.' : 'Click the other end of line B.';
            else hintEl.textContent = 'Drag an end to adjust a line. Enter the distance between the lines, then Save.';
            redrawBtn.style.display = draft && !stage ? '' : 'none';
        }

        function handleAt(evt) {
            if (!draft) return null;
            var b = canvas.getBoundingClientRect();
            var mx = evt.clientX - b.left, my = evt.clientY - b.top;
            var found = null;
            ['a', 'b'].forEach(function (gate) {
                draft[gate].forEach(function (pt, i) {
                    var c = toCanvas(pt);
                    if (Math.hypot(c.x - mx, c.y - my) <= HIT_RADIUS) found = { gate: gate, index: i };
                });
            });
            return found;
        }

        canvas.addEventListener('mousedown', function (evt) {
            if (evt.button !== 0 || !draft) return;
            var stage = drawingStage();
            if (stage) {
                draft[stage].push(toFrame(evt));
                updateHint();
                redraw();
                return;
            }
            dragging = handleAt(evt);
        });
        canvas.addEventListener('mousemove', function (evt) {
            if (dragging) {
                draft[dragging.gate][dragging.index] = toFrame(evt);
                redraw();
                return;
            }
            canvas.style.cursor = draft && (drawingStage() || handleAt(evt)) ? (drawingStage() ? 'crosshair' : 'grab') : 'default';
        });
        window.addEventListener('mouseup', function () { dragging = null; });
        // Right-click removes the last point while drawing.
        canvas.addEventListener('contextmenu', function (evt) {
            if (!draft || !drawingStage()) return;
            evt.preventDefault();
            if (draft.b.length > 0) draft.b.pop(); else if (draft.a.length > 0) draft.a.pop();
            updateHint();
            redraw();
        });

        // ── List and form ───────────────────────────────────────────────────
        function distanceText(m) {
            return formUnit.value === 'ft' ? Math.round(m * FEET_PER_METRE * 10) / 10 + ' ft' : Math.round(m * 10) / 10 + ' m';
        }

        function renderList() {
            listPanel.innerHTML = '';
            if (traps.length === 0) {
                var empty = document.createElement('p');
                empty.className = 'text-muted small';
                empty.textContent = 'No speed traps yet.';
                listPanel.appendChild(empty);
                return;
            }
            var list = document.createElement('div');
            list.className = 'list-group';
            traps.forEach(function (t) {
                var item = document.createElement('button');
                item.type = 'button';
                item.className = 'list-group-item list-group-item-action d-flex justify-content-between align-items-center'
                    + (draft && draft.id === t.id ? ' active' : '');
                var name = document.createElement('span');
                name.textContent = t.name + (t.isEnabled ? '' : ' (off)');
                var meta = document.createElement('span');
                meta.className = 'small' + (draft && draft.id === t.id ? '' : ' text-muted');
                meta.textContent = distanceText(t.distanceMeters)
                    + (t.speedLimitKph ? ' · limit ' + limitToDisplay(t.speedLimitKph) + (mph ? ' mph' : ' km/h') : '');
                item.appendChild(name);
                item.appendChild(meta);
                item.addEventListener('click', function () { openTrap(t); });
                list.appendChild(item);
            });
            listPanel.appendChild(list);
        }

        function openTrap(t) {
            var g = t ? parseGates(t.gatesJson) : { a: [], b: [] };
            draft = { id: t ? t.id : null, a: g.a, b: g.b };
            formTitle.textContent = t ? 'Edit speed trap' : 'New speed trap';
            formName.value = t ? t.name : '';
            formDistance.value = t
                ? (formUnit.value === 'ft' ? Math.round(t.distanceMeters * FEET_PER_METRE * 100) / 100 : Math.round(t.distanceMeters * 100) / 100)
                : '';
            formEnabled.checked = t ? t.isEnabled : true;
            if (formLimit) formLimit.value = t && t.speedLimitKph ? limitToDisplay(t.speedLimitKph) : '';
            formError.textContent = '';
            deleteBtn.style.display = t ? '' : 'none';
            formPanel.style.display = '';
            renderList();
            updateHint();
            redraw();
        }

        function closeForm() {
            draft = null;
            formPanel.style.display = 'none';
            renderList();
            updateHint();
            redraw();
        }

        newBtn.addEventListener('click', function () { openTrap(null); });
        redrawBtn.addEventListener('click', function () {
            if (!draft) return;
            draft.a = []; draft.b = [];
            updateHint();
            redraw();
        });
        cancelBtn.addEventListener('click', closeForm);
        formName.addEventListener('input', redraw);

        saveBtn.addEventListener('click', async function () {
            if (!draft) return;
            formError.textContent = '';
            if (drawingStage()) { formError.textContent = 'Draw both lines first.'; return; }
            var value = parseFloat(formDistance.value);
            if (!(value > 0)) { formError.textContent = 'Enter the distance between the lines.'; return; }
            var metres = formUnit.value === 'ft' ? value / FEET_PER_METRE : value;
            var limitValue = formLimit && formLimit.value !== '' ? parseFloat(formLimit.value) : null;
            if (limitValue !== null && !(limitValue > 0)) { formError.textContent = 'Enter a speed limit above 0, or leave it blank.'; return; }
            var body = {
                name: formName.value.trim() || 'Speed trap',
                gatesJson: JSON.stringify({ a: draft.a, b: draft.b }),
                distanceMeters: metres,
                isEnabled: formEnabled.checked,
                speedLimitKph: limitValue === null ? null : limitToKph(limitValue)
            };
            var url = draft.id ? '/api/speed-traps/' + draft.id : '/api/cameras/' + opts.cameraId + '/speed-traps';
            saveBtn.disabled = true;
            try {
                var resp = await fetch(url, {
                    method: draft.id ? 'PUT' : 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body)
                });
                if (!resp.ok) {
                    formError.textContent = (await resp.text()).replace(/^"|"$/g, '') || 'Could not save.';
                    return;
                }
                statusEl.textContent = 'Saved. It takes effect within about 30 seconds.';
                await loadTraps();
                closeForm();
            } catch (e) {
                formError.textContent = 'Could not save: ' + e.message;
            } finally {
                saveBtn.disabled = false;
            }
        });

        deleteBtn.addEventListener('click', async function () {
            if (!draft || !draft.id) return;
            if (!confirm('Delete this speed trap?')) return;
            var resp = await fetch('/api/speed-traps/' + draft.id, { method: 'DELETE' });
            if (!resp.ok) { formError.textContent = 'Could not delete.'; return; }
            statusEl.textContent = 'Deleted.';
            await loadTraps();
            closeForm();
        });

        async function loadTraps() {
            try {
                var resp = await fetch('/api/cameras/' + opts.cameraId + '/speed-traps');
                traps = resp.ok ? await resp.json() : [];
            } catch (e) {
                traps = [];
            }
            renderList();
            redraw();
        }

        // ── Background picture ──────────────────────────────────────────────
        snapshot.onload = function () { snapshotLoaded = true; redraw(); };
        snapshot.src = opts.snapshotUrl;

        if (window.larisvmsLiveView) {
            videoEl.addEventListener('playing', function () {
                if (!videoHasFrame) { videoHasFrame = true; redraw(); }
            });
            var statusStub = { set textContent(v) {}, get textContent() { return ''; } };
            window.larisvmsLiveView.start(opts.cameraId, videoEl, statusStub, opts.codecHint, opts.hasAudio, 'main');
            // Live AI boxes, labelled with each object's speed once it has crossed a trap — the
            // quickest way to check a trap against a vehicle of known speed.
            var overlay = window.larisvmsLiveView.startDetectionOverlay(opts.cameraId, videoEl, null);
            overlay.setShowMoving(showBoxes.checked);
            showBoxes.addEventListener('change', function () { overlay.setShowMoving(showBoxes.checked); });
        }

        window.addEventListener('resize', redraw);
        updateHint();
        loadTraps();
    }

    window.larisvmsSpeedTrapEditor = { init: init };
})();

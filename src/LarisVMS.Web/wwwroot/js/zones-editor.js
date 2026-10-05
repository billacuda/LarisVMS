// M8 zone editor — polygon drawing on a canvas over the camera's live snapshot, framework-free like
// the rest of this app's JS (timeline.js's own header comment states that convention explicitly;
// this follows it). One editor covers all four ZoneKinds (see Zone.cs's doc comment for what each
// kind actually does as of pass 1) — only the Kind dropdown changes, the drawing/CRUD flow is
// identical for all of them.
//
// Pass 1 deliberately doesn't support reshaping a saved zone's polygon — editing an existing zone
// only touches name/kind/sensitivity/enabled. To change the shape, delete and redraw. A full
// vertex-drag editor is real scope on its own and wasn't worth blocking the rest of M8 on.
//
// Pass 3c-2 folded the Grid-mode editor into this same module and page rather than a separate one —
// both share the same canvas, the same live video underneath, and the same "only one method drives
// motion detection at a time" toggle, so keeping them as two independent modules fighting over the
// same DOM elements would have been more awkward than one module with an activeMode switch.
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
        // M18 built the burn-in (NodeWorker/RecordingSession) but a confirmed-live bug on real
        // Intel/NVIDIA hardware (masked camera stuck cycling Connecting/Backoff forever, see
        // CHANGELOG 0.111.0-0.112.0) made it a kill-switched no-op again (NodeWorker.PrivacyMaskEnabled
        // = false) until the root cause is found — still "not yet active" from an admin's perspective.
        Privacy: 'Privacy — not yet active'
    };

    // Closing a polygon by clicking back near its first point needs a forgiving radius in *screen*
    // pixels, not fractional canvas coords — otherwise it's effectively unhittable on a small tile
    // or a high-DPI display where 1 fractional unit covers many device pixels.
    var CLOSE_RADIUS_PX = 12;

    // ── Grid mask bit layout — MUST match LarisVMS.Media.MotionGrid.cs exactly (row-major, cell
    // (row,col) at bit row*gridSize+col, LSB-first within each byte) since masks toggled here are
    // decoded by that exact class server-side. Any change here needs the same change there. ────────
    function base64ToBytes(b64) {
        if (!b64) return null;
        var binary = atob(b64);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }
    function bytesToBase64(bytes) {
        var binary = '';
        for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
        return btoa(binary);
    }
    function byteCount(gridSize) { return Math.ceil((gridSize * gridSize) / 8); }
    function isCellMasked(maskBase64, cellIndex) {
        var bytes = base64ToBytes(maskBase64);
        if (!bytes) return false;
        var byteIndex = Math.floor(cellIndex / 8);
        if (byteIndex >= bytes.length) return false;
        return (bytes[byteIndex] & (1 << (cellIndex % 8))) !== 0;
    }
    function setCellMasked(maskBase64, gridSize, cellIndex, masked) {
        var needed = byteCount(gridSize);
        var existing = base64ToBytes(maskBase64);
        var bytes = new Uint8Array(needed);
        if (existing) bytes.set(existing.subarray(0, Math.min(existing.length, needed)));
        var byteIndex = Math.floor(cellIndex / 8);
        var bit = 1 << (cellIndex % 8);
        if (masked) bytes[byteIndex] |= bit; else bytes[byteIndex] &= ~bit;
        return bytesToBase64(bytes);
    }

    function init(o) {
        var opts = o;
        var canvas = document.getElementById(opts.canvasId);
        var ctx = canvas.getContext('2d');
        var videoEl = opts.videoId ? document.getElementById(opts.videoId) : null;

        // 'Polygon' or 'Grid' — which pane/editor is currently shown *and* (per the mode toggle's own
        // behavior) the method actually driving motion detection right now. Set once loadRegion()
        // resolves the camera's real Camera.MotionRegionMode; defaults to Grid only as a pre-load
        // placeholder (new cameras default Grid server-side — see Camera.MotionRegionMode's own doc
        // comment — but an existing camera's real mode always wins once loadRegion() returns).
        var activeMode = 'Grid';

        var zones = [];
        var snapshotImg = new Image();
        var snapshotLoaded = false;
        var snapshotFailed = false;
        // Pass 3c-1: once live video has ever shown a real frame, the canvas stops painting a
        // background of its own (snapshot image or placeholder text) and leaves that area
        // transparent so the <video> element underneath shows through — see redraw()'s own guard.
        // Sticky rather than reset on a reconnect: live-view.js's own freeze-frame/backoff already
        // keeps the last decoded frame visible through a brief reconnect, so flipping this back to
        // false would just replace a perfectly good frozen frame with "Loading snapshot…" text.
        var videoHasFrame = false;
        // zoneId -> current motion score (pass 3c-1), from /live/{cameraId}/motion-zones. Only ever
        // populated for ServerMotion zones — see MotionZoneMask's own doc comment for why Ignore/
        // CameraMotion/Privacy zones have no live score of their own to report.
        var zoneScores = {};
        // Pass 3c-2: Grid mode's own per-cell scores from the same message (row-major, matching the
        // mask's own layout) — null until the first tick arrives while a Grid-mode session is running.
        var cellScores = null;

        var drawing = false;
        var drawPoints = []; // [{x,y}] fractional 0-1
        var selectedZoneId = null;

        // Grid mode's own editable state — server values loaded once by loadRegion(), then mutated
        // locally (cell clicks, size/sensitivity changes) and only pushed back on an explicit Save,
        // per the same "make your edits, then Save" shape the polygon zone form already uses.
        var gridSize = 32;
        var gridMask = null;
        var gridSensitivity = 0.03;
        var gridDirty = false;

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

        // Pass 3c-1: the only place a zone's live score changes what's drawn. Only ServerMotion zones
        // ever have one (Ignore/CameraMotion/Privacy keep today's fixed enabled/disabled opacity —
        // see MotionZoneMask's own doc comment for why those kinds have no live motion score at all).
        // Binary threshold-crossing, matching the original plan's own spec exactly ("muted transparent
        // yellow when its score is over threshold... no fill otherwise") — a zone with no live score
        // yet (socket not connected, this camera isn't running Polygon mode) falls back to the static
        // enabled/disabled look, never a regression.
        var TRIGGERED_WASH_OPACITY = 0.45;
        function fillOpacityForZone(z) {
            if (z.kind === 'ServerMotion' && z.isEnabled && zoneScores.hasOwnProperty(z.id)) {
                return zoneScores[z.id] >= z.sensitivity ? TRIGGERED_WASH_OPACITY : 0;
            }
            return z.isEnabled ? 0.20 : 0.08;
        }

        function drawBackground() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            if (videoHasFrame) {
                // Live video (pass 3c-1) renders through this transparent area on its own — nothing
                // for the canvas to paint here.
            } else if (snapshotLoaded) {
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
        }

        function drawPolygons() {
            zones.forEach(function (z) {
                var points = parsePolygon(z.polygonJson);
                if (points.length < 3) return;
                var color = KIND_COLORS[z.kind] || KIND_COLORS.ServerMotion;
                var isSelected = z.id === selectedZoneId;

                drawPolygonPath(points);
                ctx.closePath();
                ctx.fillStyle = color.replace('0.85', fillOpacityForZone(z).toFixed(2));
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

        // Colors per the plan's own spec: muted transparent yellow for motion, muted transparent red
        // for masked, no fill at all for a quiet cell (only its grid line) — masked wins over a live
        // score, since a masked cell isn't contributing to detection regardless of what its raw score
        // happens to read (MotionGrid.ScoreCells computes every cell's score regardless of masking,
        // purely for tuning feedback — that's not a reason to let it compete with the mask visually).
        function drawGrid() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            var cellW = w / gridSize, cellH = h / gridSize;
            for (var row = 0; row < gridSize; row++) {
                for (var col = 0; col < gridSize; col++) {
                    var idx = row * gridSize + col;
                    var x = col * cellW, y = row * cellH;
                    var masked = isCellMasked(gridMask, idx);
                    var score = (cellScores && idx < cellScores.length) ? cellScores[idx] : null;

                    if (masked) {
                        ctx.fillStyle = 'rgba(220,53,69,0.35)';
                        ctx.fillRect(x, y, cellW, cellH);
                    } else if (score !== null && score >= gridSensitivity) {
                        ctx.fillStyle = 'rgba(255,193,7,0.45)';
                        ctx.fillRect(x, y, cellW, cellH);
                    }

                    ctx.strokeStyle = 'rgba(255,255,255,0.25)';
                    ctx.lineWidth = 1;
                    ctx.strokeRect(x, y, cellW, cellH);
                }
            }
        }

        function redraw() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            if (!w || !h) return;
            ctx.clearRect(0, 0, w, h);
            drawBackground();
            if (activeMode === 'Grid') drawGrid(); else drawPolygons();
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
        // Still fetched even though live video (below) is the normal case now — it's what's shown
        // before the first live frame arrives, and the permanent fallback if this camera can't
        // stream live at all (no supported codec, not currently recording), matching this page's
        // pre-3c-1 behavior exactly in that case.
        snapshotImg.onload = function () { snapshotLoaded = true; redraw(); };
        snapshotImg.onerror = function () { snapshotFailed = true; redraw(); };
        snapshotImg.src = opts.snapshotUrl;

        // ── Live video (pass 3c-1) ──────────────────────────────────────────
        if (videoEl && window.larisvmsLiveView) {
            videoEl.addEventListener('playing', function () {
                if (!videoHasFrame) { videoHasFrame = true; redraw(); }
            });
            // No visible status text of its own — the canvas's own "Loading snapshot…"/failure text
            // already covers this camera while videoHasFrame is still false, so there's nothing
            // useful to add a second status line for.
            var liveStatusStub = { set textContent(v) {}, get textContent() { return ''; } };
            window.larisvmsLiveView.start(opts.cameraId, videoEl, liveStatusStub, opts.codecHint, opts.hasAudio, 'main');
        }

        // ── Live per-zone / per-cell motion scores (pass 3c-1 + 3c-2) ────────
        // One socket, one message shape ({zones, cellScores}) carrying whichever the camera's actual
        // MotionSession is currently producing — both are tracked regardless of activeMode so
        // switching panes never has to wait for a fresh tick to have live data ready.
        //
        // Held back to line up with the video, the same way live view's AI boxes are: the live video
        // plays a couple of seconds behind real time, so showing each tick the moment it arrived
        // washed zones before the moving object had reached them on screen. Each tick carries how old
        // its frame was (ageMs, node clock); it is queued and applied once the video has caught up to
        // that moment (live-view.js's measured video latency). Shown immediately when there's no live
        // video to line up with (still on the snapshot) or the node didn't send an age.
        (function connectMotionScores() {
            var socket = null;
            var pending = []; // [{ captureAtMs, payload }] oldest first
            var MAX_PENDING = 100;

            function applyPayload(payload) {
                var scores = (payload && payload.zones) || [];
                zoneScores = {};
                scores.forEach(function (s) { zoneScores[s.zoneId] = s.score; });
                cellScores = (payload && payload.cellScores) || null;
                redraw();
            }

            function canSync() {
                return videoHasFrame && videoEl && window.larisvmsLiveView && window.larisvmsLiveView.videoLatencyMs;
            }

            function release() {
                if (pending.length > 0) {
                    var presentationNowMs = canSync()
                        ? Date.now() - window.larisvmsLiveView.videoLatencyMs(videoEl)
                        : Infinity;
                    // The newest tick the video has reached wins; everything older is superseded.
                    var due = null;
                    while (pending.length > 0 && pending[0].captureAtMs <= presentationNowMs) due = pending.shift();
                    if (due) applyPayload(due.payload);
                }
                requestAnimationFrame(release);
            }
            requestAnimationFrame(release);

            function connect() {
                var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
                socket = new WebSocket(proto + '//' + location.host + '/live/' + opts.cameraId + '/motion-zones');
                socket.onmessage = function (evt) {
                    var payload;
                    try { payload = JSON.parse(evt.data); } catch (e) { return; } // one bad tick — next supersedes it
                    var ageMs = payload && typeof payload.ageMs === 'number' && isFinite(payload.ageMs) ? payload.ageMs : null;
                    if (ageMs === null || !canSync()) {
                        pending = [];
                        applyPayload(payload);
                        return;
                    }
                    pending.push({ captureAtMs: Date.now() - ageMs, payload: payload });
                    if (pending.length > MAX_PENDING) pending.splice(0, pending.length - MAX_PENDING);
                };
                socket.onclose = function () {
                    socket = null;
                    // Same lower-stakes fixed-delay reconnect live-view.js's own detection overlay
                    // uses (startDetectionOverlay) — a missed tick or two of wash is not worth
                    // exponential backoff, and this page has no explicit "stop watching" action to
                    // guard against (unlike that overlay's on/off toggle) since it's tied to the
                    // page's own lifetime.
                    setTimeout(connect, 2000);
                };
                socket.onerror = function () { /* onclose fires next and handles reconnect */ };
            }
            connect();
        })();

        // ── Mode toggle (pass 3c-2) ──────────────────────────────────────────
        // Polygon and Grid are mutually exclusive per camera (Camera.MotionRegionMode). Per the
        // user's own explicit call: the toggle both switches which pane is shown *and* immediately
        // activates that method — viewing an editor and making it the active one are the same action
        // here, not two separate steps.
        function setPaneVisibility(mode) {
            document.getElementById(opts.gridPaneId).style.display = mode === 'Grid' ? '' : 'none';
            document.getElementById(opts.polygonPaneId).style.display = mode === 'Grid' ? 'none' : '';
            document.getElementById(opts.gridControlsId).style.display = mode === 'Grid' ? '' : 'none';
            document.getElementById(opts.polygonControlsId).style.display = mode === 'Grid' ? 'none' : '';
            document.getElementById(opts.polygonHintId).style.display = mode === 'Grid' ? 'none' : (drawing ? '' : 'none');
            document.getElementById(opts.polygonSidebarId).style.display = mode === 'Grid' ? 'none' : '';
            document.getElementById(opts.modeToggleGridId).classList.toggle('active', mode === 'Grid');
            document.getElementById(opts.modeTogglePolygonId).classList.toggle('active', mode !== 'Grid');
        }

        function switchMode(mode) {
            if (mode === activeMode) return;
            fetch('/api/cameras/' + opts.cameraId + '/motion-region/mode', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ mode: mode })
            }).then(function (r) {
                if (!r.ok) { alert('Could not switch modes.'); return; }
                if (mode !== 'Grid' && drawing) cancelDrawing();
                activeMode = mode;
                setPaneVisibility(mode);
                redraw();
            }).catch(function () { alert('Could not switch modes.'); });
        }

        document.getElementById(opts.modeToggleGridId).addEventListener('click', function () { switchMode('Grid'); });
        document.getElementById(opts.modeTogglePolygonId).addEventListener('click', function () { switchMode('Polygon'); });

        function loadRegion() {
            fetch('/api/cameras/' + opts.cameraId + '/motion-region').then(function (r) {
                return r.ok ? r.json() : null;
            }).then(function (region) {
                if (!region) { setPaneVisibility(activeMode); return; }
                gridSize = region.gridSize;
                gridMask = region.gridMask;
                gridSensitivity = region.gridSensitivity;
                document.getElementById(opts.gridSizeSelectId).value = String(gridSize);
                var pct = Math.round(gridSensitivity * 100);
                document.getElementById(opts.gridSensitivityId).value = pct;
                document.getElementById(opts.gridSensitivityValueId).textContent = pct + '%';
                activeMode = region.mode === 'Grid' ? 'Grid' : 'Polygon';
                setPaneVisibility(activeMode);
                redraw();
            }).catch(function () { setPaneVisibility(activeMode); /* best-effort — editor still usable against in-memory defaults */ });
        }

        // ── Grid editing (pass 3c-2) ──────────────────────────────────────────
        function setGridDirty(dirty) {
            gridDirty = dirty;
            document.getElementById(opts.gridSaveBtnId).disabled = !dirty;
        }

        function setGridStatus(text) {
            var el = document.getElementById(opts.gridStatusId);
            if (el) el.textContent = text;
        }

        document.getElementById(opts.gridSaveBtnId).addEventListener('click', function () {
            setGridStatus('Saving…');
            fetch('/api/cameras/' + opts.cameraId + '/motion-region/grid', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ gridSize: gridSize, mask: gridMask, sensitivity: gridSensitivity })
            }).then(function (r) {
                if (r.ok) { setGridDirty(false); setGridStatus('Saved.'); }
                else { setGridStatus('Could not save.'); }
            }).catch(function () { setGridStatus('Could not save.'); });
        });

        document.getElementById(opts.gridSizeSelectId).addEventListener('change', function () {
            var select = this;
            var newSize = Number(select.value);
            if (newSize === gridSize) return;
            if (!confirm('Changing the grid size clears the current mask. Continue?')) {
                select.value = String(gridSize);
                return;
            }
            gridSize = newSize;
            gridMask = null;
            cellScores = null; // stale — belonged to the old size's cell layout
            setGridDirty(true);
            setGridStatus('Unsaved changes.');
            redraw();
        });

        var gridSensitivityInput = document.getElementById(opts.gridSensitivityId);
        gridSensitivityInput.addEventListener('input', function () {
            document.getElementById(opts.gridSensitivityValueId).textContent = this.value + '%';
        });
        gridSensitivityInput.addEventListener('change', function () {
            gridSensitivity = Number(this.value) / 100;
            setGridDirty(true);
            setGridStatus('Unsaved changes.');
            redraw();
        });

        // Drag-select, not one cell per click — masking a real area one cell at a time doesn't scale
        // (a 64x64 grid is 4096 cells). Mousedown on a cell decides the *direction* for this whole
        // drag from that cell's own current state (unmasked -> painting masked, masked -> painting
        // unmasked, per the user's own "activated or deactivated depending on the first cell
        // clicked" spec) — every other cell the pointer passes over while the button stays down gets
        // set to that same target state, not toggled individually (so re-crossing a cell mid-drag
        // can't flip it back). A plain click with no movement still works as a single-cell toggle:
        // it falls out of this naturally, since mousedown alone already paints the first cell.
        var gridDragActive = false;
        var gridDragTargetMasked = false;
        var gridDragLastIdx = -1;

        function cellIndexFromEvent(e) {
            var rect = canvas.getBoundingClientRect();
            // Clamped, not just measured — dragging past the canvas edge (easy to do with a fast
            // mouse gesture) should keep selecting up to the boundary row/column, not stop dead the
            // instant the pointer leaves the element.
            var fracX = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            var fracY = Math.min(1, Math.max(0, (e.clientY - rect.top) / rect.height));
            var col = Math.min(gridSize - 1, Math.floor(fracX * gridSize));
            var row = Math.min(gridSize - 1, Math.floor(fracY * gridSize));
            return row * gridSize + col;
        }

        function paintGridCell(idx) {
            if (idx === gridDragLastIdx) return; // already handled this exact cell during this drag
            gridDragLastIdx = idx;
            if (isCellMasked(gridMask, idx) === gridDragTargetMasked) return; // already in the target state
            gridMask = setCellMasked(gridMask, gridSize, idx, gridDragTargetMasked);
            setGridDirty(true);
            setGridStatus('Unsaved changes.');
            redraw();
        }

        canvas.addEventListener('mousedown', function (e) {
            if (activeMode !== 'Grid') return;
            e.preventDefault(); // avoid the page's own text-selection drag while painting cells
            var idx = cellIndexFromEvent(e);
            gridDragActive = true;
            gridDragTargetMasked = !isCellMasked(gridMask, idx);
            gridDragLastIdx = -1;
            paintGridCell(idx);
        });

        // Bound on window, not canvas — a fast drag routinely outruns the canvas's own bounds mid-
        // gesture, and mouseup releasing outside the element must still end the drag (otherwise it
        // stays "active" and the next unrelated click anywhere on the page starts painting again).
        window.addEventListener('mousemove', function (e) {
            if (!gridDragActive) return;
            paintGridCell(cellIndexFromEvent(e));
        });
        window.addEventListener('mouseup', function () {
            gridDragActive = false;
            gridDragLastIdx = -1;
        });

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
            document.getElementById(opts.polygonHintId).style.display = on ? '' : 'none';
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

        function handlePolygonClick(e) {
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
        }

        canvas.addEventListener('click', function (e) {
            // Grid mode's own interaction is entirely mousedown/mousemove/mouseup (see paintGridCell's
            // own doc comment) — dispatching here too would double-toggle the first cell of every
            // drag, since mousedown already painted it before this click event even fires.
            if (activeMode !== 'Grid') handlePolygonClick(e);
        });

        canvas.addEventListener('dblclick', function (e) {
            if (activeMode === 'Grid' || !drawing || drawPoints.length < 3) return;
            e.preventDefault();
            finishDrawing();
        });

        window.addEventListener('resize', function () { resizeCanvas(); redraw(); });

        resizeCanvas();
        setPaneVisibility(activeMode);
        loadRegion();
        loadZones();
    }

    return { init: init };
})();

// Export redaction editor (Playback → Export…): draw areas to black out in one camera's export, over a
// still frame from the export's start. Rectangle tool: drag. Polygon tool: click each corner, then
// click the first point, double-click or press Enter to close; right-click removes the last point,
// Esc abandons the shape being drawn. Areas are listed beside the picture (hover to highlight, × to
// remove). Coordinates are 0..1 of the frame — the same format as camera zones, and the node masks
// them with the same exact-shape mask as Privacy zones.
//
// window.larisvmsRedactionEditor.open({ title, imageUrl, polygons }) → Promise of the new polygon list
// (each an array of [x, y] points), or null if cancelled.
window.larisvmsRedactionEditor = (function () {
    'use strict';

    var modalEl = null;
    var els = {};
    var state = null;

    function build() {
        modalEl = document.createElement('div');
        modalEl.className = 'modal fade';
        modalEl.tabIndex = -1;
        modalEl.setAttribute('aria-labelledby', 'redactionTitle');
        modalEl.innerHTML =
            '<div class="modal-dialog modal-xl modal-dialog-scrollable"><div class="modal-content">' +
                '<div class="modal-header"><h5 class="modal-title" id="redactionTitle">Redact areas</h5>' +
                    '<button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button></div>' +
                '<div class="modal-body">' +
                    '<div class="d-flex flex-wrap align-items-center gap-2 mb-2">' +
                        '<div class="btn-group btn-group-sm" role="group" aria-label="Drawing tool">' +
                            '<button type="button" class="btn btn-outline-secondary active" data-tool="rect">▭ Rectangle</button>' +
                            '<button type="button" class="btn btn-outline-secondary" data-tool="poly">⬠ Polygon</button>' +
                        '</div>' +
                        '<span class="small text-muted" data-hint></span>' +
                    '</div>' +
                    '<div class="row g-3">' +
                        '<div class="col-lg-9"><div class="redaction-stage position-relative">' +
                            '<img alt="Frame from the start of the export" class="w-100 d-block rounded" draggable="false">' +
                            '<canvas class="position-absolute top-0 start-0 w-100 h-100" style="cursor: crosshair; touch-action: none;"></canvas>' +
                            '<div class="redaction-missing d-none text-muted small p-4">No recording at the export\'s start time to draw on. Pick a start time with footage.</div>' +
                        '</div></div>' +
                        '<div class="col-lg-3"><div class="small fw-semibold mb-1">Areas</div><ol class="list-group list-group-numbered small" data-list></ol>' +
                            '<button type="button" class="btn btn-link btn-sm px-0" data-clear>Clear all</button></div>' +
                    '</div>' +
                '</div>' +
                '<div class="modal-footer">' +
                    '<span class="small text-muted me-auto">Areas are blacked out in this camera\'s exported video. The export is re-encoded, so it takes longer.</span>' +
                    '<button type="button" class="btn btn-sm btn-secondary" data-bs-dismiss="modal">Cancel</button>' +
                    '<button type="button" class="btn btn-sm btn-primary" data-save>Done</button>' +
                '</div>' +
            '</div></div>';
        document.body.appendChild(modalEl);
        els.title = modalEl.querySelector('#redactionTitle');
        els.img = modalEl.querySelector('img');
        els.canvas = modalEl.querySelector('canvas');
        els.missing = modalEl.querySelector('.redaction-missing');
        els.list = modalEl.querySelector('[data-list]');
        els.hint = modalEl.querySelector('[data-hint]');

        Array.prototype.forEach.call(modalEl.querySelectorAll('[data-tool]'), function (b) {
            b.addEventListener('click', function () { setTool(b.getAttribute('data-tool')); });
        });
        modalEl.querySelector('[data-clear]').addEventListener('click', function () {
            state.polygons = []; state.draft = null; redraw();
        });
        modalEl.querySelector('[data-save]').addEventListener('click', function () {
            state.saved = true;
            bootstrap.Modal.getOrCreateInstance(modalEl).hide();
        });
        modalEl.addEventListener('hidden.bs.modal', function () {
            var s = state;
            state = null;
            if (s) s.resolve(s.saved ? s.polygons : null);
        });
        els.img.addEventListener('load', function () { els.missing.classList.add('d-none'); redraw(); });
        els.img.addEventListener('error', function () { els.missing.classList.remove('d-none'); els.img.classList.add('d-none'); });
        window.addEventListener('resize', function () { if (state) redraw(); });
        wireCanvas();
    }

    function setTool(tool) {
        state.tool = tool;
        state.draft = null;
        Array.prototype.forEach.call(modalEl.querySelectorAll('[data-tool]'), function (b) {
            b.classList.toggle('active', b.getAttribute('data-tool') === tool);
        });
        els.hint.textContent = tool === 'rect'
            ? 'Drag over the area to hide.'
            : 'Click each corner; click the first point, double-click, or press Enter to finish. Right-click undoes a point.';
        redraw();
    }

    // ── Geometry ────────────────────────────────────────────────────────────

    function toFrame(e) {
        var r = els.canvas.getBoundingClientRect();
        return [Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)), Math.min(1, Math.max(0, (e.clientY - r.top) / r.height))];
    }

    function nearFirstPoint(pt) {
        if (!state.draft || state.draft.length < 3) return false;
        var r = els.canvas.getBoundingClientRect();
        var f = state.draft[0];
        return Math.hypot((pt[0] - f[0]) * r.width, (pt[1] - f[1]) * r.height) < 10;
    }

    function finishPolygon() {
        // A double-click to finish also lands as two clicks: drop points on top of the previous one.
        var pts = (state.draft || []).filter(function (p, i, all) {
            return i === 0 || Math.hypot(p[0] - all[i - 1][0], p[1] - all[i - 1][1]) > 0.002;
        });
        if (pts.length >= 3) state.polygons.push(pts);
        state.draft = null;
        redraw();
    }

    function wireCanvas() {
        var drag = null;
        els.canvas.addEventListener('pointerdown', function (e) {
            if (!state || e.button !== 0) return;
            var pt = toFrame(e);
            if (state.tool === 'rect') {
                drag = { start: pt, end: pt };
                els.canvas.setPointerCapture(e.pointerId);
                state.draft = rectPoints(drag.start, drag.end);
                redraw();
                return;
            }
            if (nearFirstPoint(pt)) { finishPolygon(); return; }
            state.draft = (state.draft || []).concat([pt]);
            redraw();
        });
        els.canvas.addEventListener('pointermove', function (e) {
            if (!state) return;
            state.cursor = toFrame(e);
            if (drag) { drag.end = state.cursor; state.draft = rectPoints(drag.start, drag.end); }
            redraw();
        });
        els.canvas.addEventListener('pointerup', function () {
            if (!state || !drag) return;
            var a = drag.start, b = drag.end;
            drag = null;
            // A click without a drag isn't an area.
            if (Math.abs(a[0] - b[0]) > 0.005 && Math.abs(a[1] - b[1]) > 0.005) state.polygons.push(rectPoints(a, b));
            state.draft = null;
            redraw();
        });
        els.canvas.addEventListener('dblclick', function () { if (state && state.tool === 'poly') finishPolygon(); });
        els.canvas.addEventListener('contextmenu', function (e) {
            e.preventDefault();
            if (state && state.tool === 'poly' && state.draft) {
                state.draft.pop();
                if (state.draft.length === 0) state.draft = null;
                redraw();
            }
        });
        document.addEventListener('keydown', function (e) {
            if (!state || !modalEl.classList.contains('show')) return;
            if (e.key === 'Enter' && state.tool === 'poly') { e.preventDefault(); finishPolygon(); }
            else if (e.key === 'Escape' && state.draft) { e.preventDefault(); e.stopPropagation(); state.draft = null; redraw(); }
        }, true);
    }

    function rectPoints(a, b) {
        var x0 = Math.min(a[0], b[0]), x1 = Math.max(a[0], b[0]), y0 = Math.min(a[1], b[1]), y1 = Math.max(a[1], b[1]);
        return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]];
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    function redraw() {
        if (!state) return;
        var c = els.canvas;
        var w = c.clientWidth, h = c.clientHeight;
        if (!w || !h) return;
        var dpr = window.devicePixelRatio || 1;
        c.width = Math.round(w * dpr);
        c.height = Math.round(h * dpr);
        var ctx = c.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, h);

        function path(points) {
            ctx.beginPath();
            points.forEach(function (p, i) { if (i === 0) ctx.moveTo(p[0] * w, p[1] * h); else ctx.lineTo(p[0] * w, p[1] * h); });
        }

        state.polygons.forEach(function (poly, i) {
            path(poly);
            ctx.closePath();
            ctx.fillStyle = i === state.hover ? 'rgba(13,110,253,0.75)' : 'rgba(0,0,0,0.8)';
            ctx.fill();
            ctx.lineWidth = 2;
            ctx.strokeStyle = i === state.hover ? '#0d6efd' : '#ffc107';
            ctx.stroke();
            var cx = poly.reduce(function (a, p) { return a + p[0]; }, 0) / poly.length * w;
            var cy = poly.reduce(function (a, p) { return a + p[1]; }, 0) / poly.length * h;
            ctx.fillStyle = '#fff';
            ctx.font = 'bold 14px system-ui, sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText(String(i + 1), cx, cy);
        });

        if (state.draft) {
            var pts = state.tool === 'poly' && state.cursor ? state.draft.concat([state.cursor]) : state.draft;
            path(pts);
            if (state.tool === 'rect') ctx.closePath();
            ctx.lineWidth = 2;
            ctx.setLineDash([6, 4]);
            ctx.strokeStyle = '#ffc107';
            ctx.stroke();
            ctx.setLineDash([]);
            state.draft.forEach(function (p, i) {
                ctx.beginPath();
                ctx.arc(p[0] * w, p[1] * h, i === 0 ? 6 : 4, 0, Math.PI * 2);
                ctx.fillStyle = i === 0 ? '#ffc107' : '#fff';
                ctx.fill();
            });
        }
        renderList();
    }

    function isRect(poly) {
        return poly.length === 4 && poly[0][1] === poly[1][1] && poly[1][0] === poly[2][0] && poly[2][1] === poly[3][1] && poly[3][0] === poly[0][0];
    }

    function renderList() {
        els.list.innerHTML = state.polygons.length === 0
            ? '<li class="list-group-item text-muted">None yet</li>'
            : state.polygons.map(function (poly, i) {
                return '<li class="list-group-item d-flex align-items-center" data-index="' + i + '">' +
                    '<span class="ms-1">' + (isRect(poly) ? 'Rectangle' : 'Polygon, ' + poly.length + ' points') + '</span>' +
                    '<button type="button" class="btn-close ms-auto" aria-label="Remove area ' + (i + 1) + '" data-remove="' + i + '"></button></li>';
            }).join('');
    }

    function wireList() {
        els.list.addEventListener('click', function (e) {
            var b = e.target.closest('[data-remove]');
            if (!b) return;
            state.polygons.splice(+b.getAttribute('data-remove'), 1);
            state.hover = -1;
            redraw();
        });
        els.list.addEventListener('mouseover', function (e) {
            var li = e.target.closest('[data-index]');
            var i = li ? +li.getAttribute('data-index') : -1;
            if (state && i !== state.hover) { state.hover = i; redraw(); }
        });
        els.list.addEventListener('mouseleave', function () { if (state) { state.hover = -1; redraw(); } });
    }

    function open(options) {
        if (!modalEl) { build(); wireList(); }
        return new Promise(function (resolve) {
            state = {
                polygons: (options.polygons || []).map(function (p) { return p.map(function (pt) { return pt.slice(); }); }),
                draft: null, cursor: null, hover: -1, tool: 'rect', saved: false, resolve: resolve
            };
            els.title.textContent = 'Redact areas — ' + (options.title || 'camera');
            els.img.classList.remove('d-none');
            els.missing.classList.add('d-none');
            els.img.src = options.imageUrl;
            setTool('rect');
            bootstrap.Modal.getOrCreateInstance(modalEl).show();
            modalEl.addEventListener('shown.bs.modal', function once() { modalEl.removeEventListener('shown.bs.modal', once); redraw(); });
        });
    }

    return { open: open };
})();

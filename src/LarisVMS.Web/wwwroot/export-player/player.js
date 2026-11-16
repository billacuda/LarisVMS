// LarisVMS standalone export player (Player.html in an export package). Runs from disk with no
// server: the export's data comes from data/export.js (window.LARIS_EXPORT, built by
// ExportPackageBuilder), its text from player/strings.js through i18n.js, and verification from
// export-verify-core.js.
//
//   Player   cameras on the left, dragged (or clicked) into a grid; one clock drives every tile,
//            with the timeline, speed, frame step, and jumps between events.
//   Events   every motion and detection in the export, filterable; Play jumps to one.
//   Verify   checks the package folder against its signed list of files, and each camera's own
//            signed manifest and footage chain.
//   About    the export's details, its log, bookmarks, evidence locks, and files.
//
// An exported file is its camera's recorded segments back to back, so its own timeline skips any gap
// between them; segments[] (clock times) maps clock time to a position in the file.
(function () {
    'use strict';

    var app = document.getElementById('app');
    var X = window.LARIS_EXPORT;
    var I = window.larisvmsI18n;
    if (!app) return;
    if (!X || !I) {
        app.textContent = 'This folder is missing part of the LarisVMS export (data/export.js or player/strings.js). Copy the whole folder again.';
        app.className = 'ep-fatal';
        return;
    }
    function t(key, args) { return I.t(key, args); }
    function plural(key, n, args) { return I.plural(key, n, args); }
    document.documentElement.lang = I.lang || 'en';
    document.title = t('js.exportPlayer.title');

    // ── Small DOM helpers ───────────────────────────────────────────────────

    function el(tag, attrs, children) {
        var node = document.createElement(tag);
        if (attrs) {
            for (var k in attrs) {
                if (!Object.prototype.hasOwnProperty.call(attrs, k) || attrs[k] == null || attrs[k] === false) continue;
                if (k === 'text') node.textContent = attrs[k];
                else if (k === 'class') node.className = attrs[k];
                else if (k.indexOf('on') === 0 && typeof attrs[k] === 'function') node.addEventListener(k.slice(2), attrs[k]);
                else if (attrs[k] === true) node.setAttribute(k, '');
                else node.setAttribute(k, attrs[k]);
            }
        }
        (children || []).forEach(function (c) { if (c != null) node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
        return node;
    }

    function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

    function ms(iso) { return Date.parse(iso); }

    // ── Time display ────────────────────────────────────────────────────────

    var zones = [{ value: 'local', label: t('js.exportPlayer.zoneThisComputer') }];
    if (X.siteTimeZone) zones.push({ value: 'site', label: t('js.exportPlayer.zoneSite', { zone: X.siteTimeZone }) });
    zones.push({ value: 'utc', label: 'UTC' });
    var zoneMode = 'local';
    var formatters = {};

    function zoneOption() {
        if (zoneMode === 'utc') return 'UTC';
        if (zoneMode === 'site') return X.siteTimeZone;
        return undefined;
    }

    function fmt(kind, msValue) {
        var key = kind + '|' + zoneMode;
        if (!formatters[key]) {
            var opts = { timeZone: zoneOption() };
            if (kind === 'full') { opts.dateStyle = 'medium'; opts.timeStyle = 'medium'; }
            else if (kind === 'date') { opts.dateStyle = 'medium'; }
            else if (kind === 'time') { opts.hour = '2-digit'; opts.minute = '2-digit'; opts.second = '2-digit'; opts.hourCycle = undefined; }
            else if (kind === 'hm') { opts.hour = '2-digit'; opts.minute = '2-digit'; }
            try { formatters[key] = new Intl.DateTimeFormat(I.locale, opts); }
            catch (e) { formatters[key] = new Intl.DateTimeFormat(I.locale, kind === 'full' ? { dateStyle: 'medium', timeStyle: 'medium' } : {}); }
        }
        return formatters[key].format(new Date(msValue));
    }

    function clockText(msValue) {
        var tenths = Math.floor((((msValue % 1000) + 1000) % 1000) / 100);
        return fmt('date', msValue) + ' ' + fmt('time', msValue) + '.' + tenths;
    }

    function duration(msValue) {
        var s = Math.max(0, Math.round(msValue / 1000));
        var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
        return (h ? h + ':' + String(m).padStart(2, '0') : String(m)) + ':' + String(sec).padStart(2, '0');
    }

    // ── The export's data ───────────────────────────────────────────────────

    var rangeStart = ms(X.fromUtc), rangeEnd = ms(X.toUtc);
    var probe = document.createElement('video');
    function canPlay(codec) {
        var c = (codec || 'h264').toLowerCase();
        var type = (c.indexOf('hevc') >= 0 || c.indexOf('265') >= 0 || c.indexOf('hvc') === 0 || c.indexOf('hev') === 0)
            ? 'video/mp4; codecs="hvc1.1.6.L93.B0"'
            : 'video/mp4; codecs="avc1.42E01E"';
        return probe.canPlayType(type) !== '';
    }

    var cameras = (X.cameras || []).map(function (c) {
        var files = (c.files || []).map(function (f) {
            // Where each segment sits in the file: back to back, gaps left out.
            var offset = 0;
            var pieces = (f.segments || []).map(function (s) {
                var start = ms(s.startUtc), end = ms(s.endUtc);
                var piece = { start: start, end: end, offset: offset };
                offset += (end - start) / 1000;
                return piece;
            });
            var playable = canPlay(f.codec);
            var src = playable || !f.h264Path ? f.path : f.h264Path;
            return { path: f.path, h264Path: f.h264Path, codec: f.codec, src: src, playable: playable || !!f.h264Path, pieces: pieces, raw: f };
        });
        return { id: c.id, name: c.name, files: files };
    });
    var camerasById = {};
    cameras.forEach(function (c) { camerasById[c.id] = c; });

    var events = (X.events || []).map(function (e) {
        return {
            cameraId: e.cameraId, start: ms(e.startUtc), end: ms(e.endUtc), kind: e.kind, label: e.label || '', color: e.color || X.colors.motion,
            best: e.bestFrameUtc ? ms(e.bestFrameUtc) : null, box: e.box, count: e.count
        };
    }).sort(function (a, b) { return a.start - b.start; });
    // Detection boxes by camera, ordered by their best frame, for a quick lookup while playing.
    var BOX_WINDOW = 750;
    var boxesByCamera = {};
    events.forEach(function (e) {
        if (!e.box || e.best == null) return;
        (boxesByCamera[e.cameraId] = boxesByCamera[e.cameraId] || []).push(e);
    });
    Object.keys(boxesByCamera).forEach(function (id) { boxesByCamera[id].sort(function (a, b) { return a.best - b.best; }); });

    /// The boxes whose best frame is within BOX_WINDOW of a clock time (binary search, then a short walk).
    function boxesNear(cameraId, at) {
        var list = boxesByCamera[cameraId];
        if (!list || list.length === 0) return [];
        var lo = 0, hi = list.length;
        while (lo < hi) { var mid = (lo + hi) >> 1; if (list[mid].best < at - BOX_WINDOW) lo = mid + 1; else hi = mid; }
        var out = [];
        for (var i = lo; i < list.length && list[i].best <= at + BOX_WINDOW; i++) out.push(list[i]);
        return out;
    }

    var bookmarks = (X.bookmarks || []).map(function (b) { return { cameraId: b.cameraId, at: ms(b.atUtc), note: b.note, by: b.by }; });
    var locks = (X.evidenceLocks || []).map(function (l) { return { cameraId: l.cameraId, from: ms(l.fromUtc), to: ms(l.toUtc), reason: l.reason, by: l.by }; });

    /// The file and position in it for one camera at a clock time, or null when nothing was recorded then.
    function locate(camera, at) {
        for (var i = 0; i < camera.files.length; i++) {
            var f = camera.files[i];
            for (var j = 0; j < f.pieces.length; j++) {
                var p = f.pieces[j];
                if (at >= p.start && at < p.end) return { file: f, time: p.offset + (at - p.start) / 1000 };
            }
        }
        return null;
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    var tabs = [
        { id: 'player', label: t('js.exportPlayer.tabPlayer') },
        { id: 'events', label: t('js.exportPlayer.tabEvents') },
        { id: 'verify', label: t('js.exportPlayer.tabVerify') },
        { id: 'about', label: t('js.exportPlayer.tabAbout') }
    ];
    var tabButtons = {}, pages = {};

    var zoneSelect = el('select', { id: 'epZone', 'aria-label': t('js.exportPlayer.showTimesIn') },
        zones.map(function (z) { return el('option', { value: z.value, text: z.label }); }));
    zoneSelect.addEventListener('change', function () {
        zoneMode = zoneSelect.value;
        renderHeader();
        renderClock();
        drawTimeline();
        if (!pages.events.hidden) renderEvents();
        if (!pages.about.hidden) renderAbout();
    });

    var rangeText = el('span', { class: 'ep-range' });
    function renderHeader() { rangeText.textContent = fmt('full', rangeStart) + ' – ' + fmt('full', rangeEnd); }

    var header = el('header', { class: 'ep-header' }, [
        el('h1', { text: t('js.exportPlayer.title') }),
        rangeText,
        el('label', { class: 'ep-tz' }, [el('span', { class: 'ep-muted ep-small', text: t('js.exportPlayer.showTimesIn') }), zoneSelect]),
        el('nav', { class: 'ep-tabs', role: 'tablist' }, tabs.map(function (tab) {
            var b = el('button', { type: 'button', class: 'ep-tab', role: 'tab', id: 'tab-' + tab.id, 'aria-controls': 'page-' + tab.id, 'aria-selected': 'false', text: tab.label });
            b.addEventListener('click', function () { showTab(tab.id); });
            tabButtons[tab.id] = b;
            return b;
        }))
    ]);
    var main = el('main', { class: 'ep-main' });
    app.appendChild(header);
    app.appendChild(main);
    tabs.forEach(function (tab) {
        pages[tab.id] = el('section', { id: 'page-' + tab.id, role: 'tabpanel', 'aria-labelledby': 'tab-' + tab.id, hidden: true,
            class: tab.id === 'player' ? 'ep-player' : 'ep-page' });
        main.appendChild(pages[tab.id]);
    });

    function showTab(id) {
        tabs.forEach(function (tab) {
            var on = tab.id === id;
            tabButtons[tab.id].setAttribute('aria-selected', on ? 'true' : 'false');
            pages[tab.id].hidden = !on;
        });
        if (id === 'events') renderEvents();
        if (id === 'about') renderAbout();
        if (id === 'player') { resizeTimeline(); drawTimeline(); }
    }

    // ── Player: camera list ─────────────────────────────────────────────────

    var camList = el('ul', { class: 'ep-camlist' });
    var side = el('aside', { class: 'ep-side', 'aria-label': t('js.exportPlayer.cameras') }, [
        el('h2', { text: t('js.exportPlayer.cameras') }),
        camList,
        el('p', { class: 'ep-muted ep-small', text: t('js.exportPlayer.dragHint') })
    ]);

    function renderCameraList() {
        clear(camList);
        cameras.forEach(function (c) {
            var shown = cells.indexOf(c.id) >= 0;
            var b = el('button', { type: 'button', class: 'ep-cam' + (shown ? ' is-shown' : ''), draggable: 'true', title: c.name },
                [el('span', { class: 'ep-dot', 'aria-hidden': 'true' }), el('span', { class: 'ep-cam-name', text: c.name })]);
            b.setAttribute('aria-pressed', shown ? 'true' : 'false');
            b.addEventListener('click', function () { placeCamera(c.id, selectedCell); });
            b.addEventListener('dragstart', function (e) { e.dataTransfer.setData('text/plain', 'camera:' + c.id); e.dataTransfer.effectAllowed = 'copyMove'; });
            camList.appendChild(el('li', null, [b]));
        });
    }

    // ── Player: grid ────────────────────────────────────────────────────────

    var layouts = [
        { id: '1', cols: 1, rows: 1, label: '1' },
        { id: '2', cols: 2, rows: 1, label: '1 × 2' },
        { id: '4', cols: 2, rows: 2, label: '2 × 2' },
        { id: '6', cols: 3, rows: 2, label: '2 × 3' },
        { id: '9', cols: 3, rows: 3, label: '3 × 3' },
        { id: '16', cols: 4, rows: 4, label: '4 × 4' }
    ];
    var layout = cameras.length <= 1 ? layouts[0] : cameras.length === 2 ? layouts[1] : cameras.length <= 4 ? layouts[2]
        : cameras.length <= 6 ? layouts[3] : cameras.length <= 9 ? layouts[4] : layouts[5];
    var cells = [];
    var selectedCell = 0;
    var tiles = [];
    var showBoxes = true;
    for (var n = 0; n < layout.cols * layout.rows; n++) cells.push(cameras[n] ? cameras[n].id : null);

    var grid = el('div', { class: 'ep-grid', role: 'group', 'aria-label': t('js.exportPlayer.grid') });
    var layoutSelect = el('select', { 'aria-label': t('js.exportPlayer.layout') }, layouts.map(function (l) {
        return el('option', { value: l.id, text: l.label, selected: l === layout });
    }));
    layoutSelect.addEventListener('change', function () {
        layout = layouts.filter(function (l) { return l.id === layoutSelect.value; })[0];
        var size = layout.cols * layout.rows;
        cells = cells.slice(0, size);
        while (cells.length < size) cells.push(null);
        if (selectedCell >= size) selectedCell = 0;
        buildGrid();
    });
    var boxesToggle = el('input', { type: 'checkbox', checked: true });
    boxesToggle.addEventListener('change', function () { showBoxes = boxesToggle.checked; syncTiles(true); });

    function placeCamera(cameraId, index) {
        var existing = cells.indexOf(cameraId);
        if (existing >= 0 && existing !== index) cells[existing] = cells[index];
        cells[index] = cameraId;
        // Next click fills the next empty cell.
        var next = cells.indexOf(null);
        selectedCell = next >= 0 ? next : index;
        buildGrid();
    }

    function buildGrid() {
        tiles.forEach(function (tile) { if (tile.video) { tile.video.pause(); tile.video.removeAttribute('src'); tile.video.load(); } });
        tiles = [];
        clear(grid);
        grid.style.gridTemplateColumns = 'repeat(' + layout.cols + ', minmax(0, 1fr))';
        grid.style.gridTemplateRows = 'repeat(' + layout.rows + ', minmax(0, 1fr))';
        cells.forEach(function (cameraId, index) { tiles.push(buildTile(cameraId, index)); });
        renderCameraList();
        syncTiles(true);
        resizeTimeline();
        drawTimeline();
    }

    function buildTile(cameraId, index) {
        var camera = cameraId ? camerasById[cameraId] : null;
        var cell = el('div', { class: 'ep-cell' + (index === selectedCell ? ' is-selected' : ''), tabindex: '0', role: 'group',
            'aria-label': camera ? camera.name : t('js.exportPlayer.emptyCell', { number: index + 1 }) });
        var tile = { index: index, camera: camera, cell: cell, video: null, boxes: null, notice: null, src: null, zoom: 1, panX: 0, panY: 0 };

        cell.addEventListener('click', function () { selectCell(index); });
        cell.addEventListener('keydown', function (e) {
            if (e.key === 'Delete' || e.key === 'Backspace') { cells[index] = null; buildGrid(); e.preventDefault(); }
        });
        cell.addEventListener('dragover', function (e) { e.preventDefault(); cell.classList.add('is-drop'); });
        cell.addEventListener('dragleave', function () { cell.classList.remove('is-drop'); });
        cell.addEventListener('drop', function (e) {
            e.preventDefault();
            cell.classList.remove('is-drop');
            var data = e.dataTransfer.getData('text/plain') || '';
            if (data.indexOf('camera:') === 0) placeCamera(data.slice(7), index);
            else if (data.indexOf('cell:') === 0) {
                var from = +data.slice(5);
                var moving = cells[from];
                cells[from] = cells[index];
                cells[index] = moving;
                buildGrid();
            }
        });

        if (!camera) {
            cell.appendChild(el('div', { class: 'ep-empty', text: t('js.exportPlayer.dropHere') }));
            grid.appendChild(cell);
            return tile;
        }

        var video = el('video', { muted: true, playsinline: true, preload: 'auto' });
        video.muted = true;
        var boxes = el('canvas', { class: 'ep-boxes', 'aria-hidden': 'true' });
        var notice = el('div', { class: 'ep-notice', hidden: true });
        var empty = el('div', { class: 'ep-empty', hidden: true, text: t('js.exportPlayer.noRecording') });
        var sound = el('button', { type: 'button', title: t('js.exportPlayer.sound'), 'aria-label': t('js.exportPlayer.sound'), 'aria-pressed': 'false', text: '🔇' });
        sound.addEventListener('click', function (e) {
            e.stopPropagation();
            // One tile at a time has sound, so voices don't overlap.
            var on = video.muted;
            tiles.forEach(function (other) { if (other.video) { other.video.muted = true; other.sound.textContent = '🔇'; other.sound.setAttribute('aria-pressed', 'false'); } });
            video.muted = !on;
            sound.textContent = on ? '🔊' : '🔇';
            sound.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
        var full = el('button', { type: 'button', title: t('js.exportPlayer.fullscreen'), 'aria-label': t('js.exportPlayer.fullscreen'), text: '⛶' });
        full.addEventListener('click', function (e) {
            e.stopPropagation();
            if (document.fullscreenElement) document.exitFullscreen();
            else if (cell.requestFullscreen) cell.requestFullscreen();
        });
        var remove = el('button', { type: 'button', title: t('js.exportPlayer.removeFromGrid'), 'aria-label': t('js.exportPlayer.removeFromGrid'), text: '×' });
        remove.addEventListener('click', function (e) { e.stopPropagation(); cells[index] = null; buildGrid(); });
        var label = el('div', { class: 'ep-label', draggable: 'true' }, [el('span', { class: 'ep-name', text: camera.name }), sound, full, remove]);
        label.addEventListener('dragstart', function (e) { e.dataTransfer.setData('text/plain', 'cell:' + index); });

        // Digital zoom: the wheel zooms around the pointer, dragging pans when zoomed in.
        cell.addEventListener('wheel', function (e) {
            e.preventDefault();
            var rect = cell.getBoundingClientRect();
            var px = e.clientX - rect.left, py = e.clientY - rect.top;
            var next = Math.min(8, Math.max(1, tile.zoom * (e.deltaY < 0 ? 1.2 : 1 / 1.2)));
            tile.panX = px - (px - tile.panX) * (next / tile.zoom);
            tile.panY = py - (py - tile.panY) * (next / tile.zoom);
            tile.zoom = next;
            applyZoom(tile);
        }, { passive: false });
        var dragging = null;
        cell.addEventListener('pointerdown', function (e) {
            if (tile.zoom <= 1 || e.target.closest('button')) return;
            dragging = { x: e.clientX, y: e.clientY, panX: tile.panX, panY: tile.panY };
            cell.setPointerCapture(e.pointerId);
        });
        cell.addEventListener('pointermove', function (e) {
            if (!dragging) return;
            tile.panX = dragging.panX + e.clientX - dragging.x;
            tile.panY = dragging.panY + e.clientY - dragging.y;
            applyZoom(tile);
        });
        cell.addEventListener('pointerup', function () { dragging = null; });
        cell.addEventListener('dblclick', function () { tile.zoom = 1; tile.panX = 0; tile.panY = 0; applyZoom(tile); });

        video.addEventListener('loadedmetadata', function () { tile.ready = true; syncTile(tile, true); });
        video.addEventListener('seeked', function () {
            // The newest target that came in during the seek, if it's still somewhere else.
            if (tile.pending == null) return;
            var target = tile.pending;
            tile.pending = null;
            if (Math.abs(video.currentTime - target) > 0.05) video.currentTime = target;
        });
        video.addEventListener('error', function () {
            notice.hidden = false;
            notice.textContent = t('js.exportPlayer.cantPlay');
        });

        cell.appendChild(video);
        cell.appendChild(boxes);
        cell.appendChild(empty);
        cell.appendChild(label);
        cell.appendChild(notice);
        grid.appendChild(cell);
        tile.video = video;
        tile.boxes = boxes;
        tile.notice = notice;
        tile.empty = empty;
        tile.sound = sound;
        if (!camera.files.some(function (f) { return f.playable; })) {
            notice.hidden = false;
            notice.textContent = t('js.exportPlayer.codecUnsupported');
        }
        return tile;
    }

    function applyZoom(tile) {
        if (tile.zoom <= 1) { tile.zoom = 1; tile.panX = 0; tile.panY = 0; }
        tile.video.style.transform = 'translate(' + tile.panX + 'px,' + tile.panY + 'px) scale(' + tile.zoom + ')';
        drawBoxes(tile);
    }

    function selectCell(index) {
        selectedCell = index;
        tiles.forEach(function (tile) { tile.cell.classList.toggle('is-selected', tile.index === index); });
    }

    // ── The clock ───────────────────────────────────────────────────────────

    // While playing, the clock follows a tile's video as it actually plays (the "lead" tile), so it
    // can never run ahead of a decoder that's slow to seek or start, which used to set off endless
    // re-seeking. With no video playing (a gap in the footage, or every tile still loading), it
    // runs on its own.
    var now = rangeStart;
    var playing = false;
    var speed = 1;
    var lastFrame = 0;
    var lastSync = 0;
    var lastDraw = 0;
    var speeds = [0.125, 0.25, 0.5, 1, 2, 4, 8, 16];

    function seek(at) {
        now = Math.min(rangeEnd, Math.max(rangeStart, at));
        syncTiles(true);
        renderClock();
        drawTimeline();
    }

    function setPlaying(on) {
        playing = on && now < rangeEnd;
        playButton.textContent = playing ? '⏸' : '▶';
        playButton.setAttribute('aria-label', playing ? t('js.exportPlayer.pause') : t('js.exportPlayer.play'));
        playButton.title = playButton.getAttribute('aria-label');
        lastFrame = performance.now();
        syncTiles(true);
    }

    /// The clock time a tile's video is showing, or null when it isn't playing smoothly right now.
    function leadTime(tile) {
        var video = tile.video;
        if (!tile.camera || !video || !tile.ready || tile.pending != null || video.paused || video.seeking
            || video.readyState < 3 || video.style.visibility === 'hidden') return null;
        var file = tile.camera.files.filter(function (f) { return f.src === tile.src; })[0];
        if (!file) return null;
        var at = video.currentTime;
        for (var i = 0; i < file.pieces.length; i++) {
            var p = file.pieces[i];
            var length = (p.end - p.start) / 1000;
            if (at >= p.offset && at < p.offset + length) return p.start + (at - p.offset) * 1000;
        }
        return null;
    }

    function tick(frameTime) {
        if (playing) {
            var led = null;
            for (var i = 0; i < tiles.length && led == null; i++) led = leadTime(tiles[i]);
            // Follow the lead video, but never backwards by more than a moment (a stale frame).
            if (led != null && led > now - 250) now = led;
            else now += (frameTime - lastFrame) * speed;
            if (now >= rangeEnd) { now = rangeEnd; setPlaying(false); }
            if (frameTime - lastSync > 250) { syncTiles(false); lastSync = frameTime; }
            renderClock();
            // The timeline only needs a few redraws a second; boxes every frame (they're cheap).
            if (frameTime - lastDraw > 200) { drawTimeline(); lastDraw = frameTime; }
            tiles.forEach(drawBoxes);
        }
        lastFrame = frameTime;
        requestAnimationFrame(tick);
    }

    function syncTiles(force) { tiles.forEach(function (tile) { syncTile(tile, force); }); }

    /// Moves a video to a time, one seek at a time: while one is under way, a newer target waits and
    /// is applied when it finishes ('seeked'), instead of restarting the seek over and over.
    function seekVideo(tile, time) {
        if (tile.video.seeking) { tile.pending = time; return; }
        tile.pending = null;
        tile.video.currentTime = time;
    }

    /// Keeps one tile's video at the clock: the right file, the right place in it, playing or not.
    /// While playing, a small drift is caught up by playing slightly faster or slower; only a large
    /// one seeks.
    function syncTile(tile, force) {
        if (!tile.camera || !tile.video) return;
        var video = tile.video;
        var spot = locate(tile.camera, now);
        if (!spot || !spot.file.playable) {
            tile.empty.hidden = !!spot;
            if (!video.paused) video.pause();
            video.style.visibility = 'hidden';
            drawBoxes(tile);
            return;
        }
        tile.empty.hidden = true;
        video.style.visibility = '';
        if (tile.src !== spot.file.src) {
            tile.src = spot.file.src;
            tile.ready = false;
            tile.pending = null;
            video.src = spot.file.src.split('/').map(encodeURIComponent).join('/');
            return; // loadedmetadata syncs it again
        }
        if (!tile.ready) return;
        var behind = spot.time - video.currentTime;
        var drift = Math.abs(behind);
        var rate = Math.min(16, Math.max(0.0625, speed));
        if (playing) {
            if (force || drift > Math.max(2, speed)) {
                seekVideo(tile, spot.time);
            } else if (!video.seeking && drift > 0.15) {
                rate = Math.min(16, Math.max(0.0625, rate * (behind > 0 ? 1.1 : 0.9)));
            }
            if (video.playbackRate !== rate) video.playbackRate = rate;
            if (video.paused) { var p = video.play(); if (p && p.catch) p.catch(function () { /* autoplay refused: stays paused */ }); }
        } else {
            if (video.playbackRate !== rate) video.playbackRate = rate;
            if (!video.paused) video.pause();
            if (force || drift > 0.04) seekVideo(tile, spot.time);
        }
        drawBoxes(tile);
    }

    /// Detection boxes near their best frame, scaled to where the video sits in the tile.
    function drawBoxes(tile) {
        if (!tile.boxes || !tile.video) return;
        var canvas = tile.boxes;
        var rect = tile.cell.getBoundingClientRect();
        var dpr = window.devicePixelRatio || 1;
        if (canvas.width !== Math.round(rect.width * dpr) || canvas.height !== Math.round(rect.height * dpr)) {
            canvas.width = Math.round(rect.width * dpr);
            canvas.height = Math.round(rect.height * dpr);
        }
        // Only the boxes near now, from this camera's index — not a pass over every event per frame.
        var near = showBoxes && tile.camera && tile.video.style.visibility !== 'hidden' ? boxesNear(tile.camera.id, now) : [];
        var vw = tile.video.videoWidth, vh = tile.video.videoHeight;
        if (!vw || !vh) near = [];
        if (near.length === 0 && !tile.drewBoxes) return;
        var ctx = canvas.getContext('2d');
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        tile.drewBoxes = near.length > 0;
        if (!tile.drewBoxes) return;
        var scale = Math.min(rect.width / vw, rect.height / vh);
        var w = vw * scale, h = vh * scale;
        var x0 = (rect.width - w) / 2, y0 = (rect.height - h) / 2;
        ctx.setTransform(dpr * tile.zoom, 0, 0, dpr * tile.zoom, dpr * tile.panX, dpr * tile.panY);
        ctx.lineWidth = 2 / tile.zoom;
        ctx.font = I.font ? I.font('bold ' + Math.round(12 / tile.zoom) + 'px') : 'bold 12px sans-serif';
        near.forEach(function (e) {
            var bx = x0 + e.box[0] * w, by = y0 + e.box[1] * h, bw = e.box[2] * w, bh = e.box[3] * h;
            ctx.strokeStyle = e.color;
            ctx.strokeRect(bx, by, bw, bh);
            ctx.fillStyle = e.color;
            ctx.fillText(e.label, bx + 2, Math.max(12, by - 4));
        });
    }

    // ── Transport ───────────────────────────────────────────────────────────

    function button(text, label, onClick) {
        var b = el('button', { type: 'button', class: 'btn', title: label, 'aria-label': label, text: text });
        b.addEventListener('click', onClick);
        return b;
    }

    function nextEvent(direction) {
        var shown = cells.filter(Boolean);
        var pool = events.filter(function (e) { return shown.length === 0 || shown.indexOf(e.cameraId) >= 0; });
        var target = null;
        if (direction > 0) target = pool.filter(function (e) { return e.start > now + 500; })[0];
        else target = pool.filter(function (e) { return e.start < now - 1500; }).pop();
        if (target) seek(target.start - 1000);
    }

    function step(frames) { setPlaying(false); seek(now + frames * 40); }

    var playButton = button('▶', t('js.exportPlayer.play'), function () { setPlaying(!playing); });
    var speedSelect = el('select', { 'aria-label': t('js.exportPlayer.speed') }, speeds.map(function (s) {
        return el('option', { value: String(s), text: (s < 1 ? '1/' + Math.round(1 / s) : String(s)) + '×', selected: s === 1 });
    }));
    speedSelect.addEventListener('change', function () { speed = +speedSelect.value; syncTiles(false); });
    var clock = el('span', { class: 'ep-clock', 'aria-live': 'off' });
    function renderClock() { clock.textContent = clockText(now); }

    var transport = el('div', { class: 'ep-transport' }, [
        button('⏮', t('js.exportPlayer.previousEvent'), function () { nextEvent(-1); }),
        button('−10s', t('js.exportPlayer.back10'), function () { seek(now - 10000); }),
        button('◂|', t('js.exportPlayer.frameBack'), function () { step(-1); }),
        playButton,
        button('|▸', t('js.exportPlayer.frameForward'), function () { step(1); }),
        button('+10s', t('js.exportPlayer.forward10'), function () { seek(now + 10000); }),
        button('⏭', t('js.exportPlayer.nextEvent'), function () { nextEvent(1); }),
        el('label', { class: 'ep-muted ep-small' }, [t('js.exportPlayer.speed') + ' ', speedSelect]),
        clock
    ]);

    // ── Timeline ────────────────────────────────────────────────────────────

    var LANE = 22, AXIS = 22;
    var view = { start: rangeStart, end: rangeEnd };
    var timeline = el('canvas', { class: 'ep-timeline', tabindex: '0', role: 'slider', 'aria-label': t('js.exportPlayer.timeline'),
        'aria-valuemin': String(rangeStart), 'aria-valuemax': String(rangeEnd) });
    var zoomBar = el('div', { class: 'ep-zoom' }, [
        button('−', t('js.exportPlayer.zoomOut'), function () { zoomView(2, now); }),
        button('+', t('js.exportPlayer.zoomIn'), function () { zoomView(0.5, now); }),
        button('↔', t('js.exportPlayer.zoomFit'), function () { view = { start: rangeStart, end: rangeEnd }; drawTimeline(); }),
        el('span', { class: 'ep-muted ep-small', text: t('js.exportPlayer.timelineHint') })
    ]);

    function lanes() {
        var shown = cells.filter(Boolean);
        return shown.length ? shown.map(function (id) { return camerasById[id]; }) : cameras;
    }

    function resizeTimeline() {
        var count = lanes().length;
        var cssHeight = AXIS + Math.max(1, count) * LANE + 6;
        var dpr = window.devicePixelRatio || 1;
        var width = timeline.clientWidth || 600;
        timeline.style.height = cssHeight + 'px';
        timeline.width = Math.round(width * dpr);
        timeline.height = Math.round(cssHeight * dpr);
    }

    function xOf(at, width) { return (at - view.start) / (view.end - view.start) * width; }
    function timeAt(x, width) { return view.start + (x / width) * (view.end - view.start); }

    function zoomView(factor, around) {
        var span = Math.min(rangeEnd - rangeStart, Math.max(10000, (view.end - view.start) * factor));
        var ratio = (around - view.start) / (view.end - view.start);
        var start = around - span * ratio;
        start = Math.max(rangeStart, Math.min(rangeEnd - span, start));
        view = { start: start, end: start + span };
        drawTimeline();
    }

    var tickSteps = [1000, 5000, 10000, 30000, 60000, 300000, 600000, 1800000, 3600000, 7200000, 21600000, 43200000, 86400000];

    function drawTimeline() {
        if (pages.player.hidden) return;
        var dpr = window.devicePixelRatio || 1;
        var width = timeline.width / dpr, height = timeline.height / dpr;
        if (!width) return;
        var ctx = timeline.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, width, height);
        var style = getComputedStyle(document.documentElement);
        var text = style.getPropertyValue('--ep-text').trim() || '#000';
        var muted = style.getPropertyValue('--ep-muted').trim() || '#666';
        ctx.font = I.font ? I.font('11px') : '11px sans-serif';
        ctx.textBaseline = 'middle';

        // Axis
        var span = view.end - view.start;
        var stepMs = tickSteps.filter(function (s) { return width / (span / s) >= 70; })[0] || tickSteps[tickSteps.length - 1];
        ctx.fillStyle = muted;
        ctx.strokeStyle = muted;
        ctx.globalAlpha = 0.6;
        for (var tk = Math.ceil(view.start / stepMs) * stepMs; tk <= view.end; tk += stepMs) {
            var tx = xOf(tk, width);
            ctx.beginPath(); ctx.moveTo(tx + 0.5, AXIS - 6); ctx.lineTo(tx + 0.5, height); ctx.stroke();
            ctx.fillText(stepMs < 60000 ? fmt('time', tk) : fmt('hm', tk), tx + 3, AXIS / 2);
        }
        ctx.globalAlpha = 1;

        // Lanes
        lanes().forEach(function (camera, i) {
            var y = AXIS + i * LANE;
            camera.files.forEach(function (f) {
                f.pieces.forEach(function (p) {
                    if (p.end < view.start || p.start > view.end) return;
                    ctx.fillStyle = X.colors.recording;
                    ctx.globalAlpha = 0.45;
                    var x1 = Math.max(0, xOf(p.start, width)), x2 = Math.min(width, xOf(p.end, width));
                    ctx.fillRect(x1, y + 4, Math.max(1, x2 - x1), LANE - 8);
                });
            });
            ctx.globalAlpha = 1;
            events.forEach(function (e) {
                if (e.cameraId !== camera.id || e.end < view.start || e.start > view.end) return;
                ctx.fillStyle = e.color;
                var x1 = Math.max(0, xOf(e.start, width)), x2 = Math.min(width, xOf(e.end, width));
                ctx.fillRect(x1, y + 6, Math.max(2, x2 - x1), LANE - 12);
            });
            locks.forEach(function (l) {
                if (l.cameraId !== camera.id || l.to < view.start || l.from > view.end) return;
                ctx.fillStyle = muted;
                var x1 = Math.max(0, xOf(l.from, width)), x2 = Math.min(width, xOf(l.to, width));
                ctx.fillRect(x1, y + 2, Math.max(1, x2 - x1), 2);
            });
            bookmarks.forEach(function (b) {
                if (b.cameraId !== camera.id || b.at < view.start || b.at > view.end) return;
                var bx = xOf(b.at, width);
                ctx.fillStyle = text;
                ctx.beginPath(); ctx.moveTo(bx - 4, y + 2); ctx.lineTo(bx + 4, y + 2); ctx.lineTo(bx, y + 9); ctx.closePath(); ctx.fill();
            });
            ctx.fillStyle = text;
            ctx.globalAlpha = 0.85;
            ctx.fillText(camera.name, 4, y + LANE / 2);
            ctx.globalAlpha = 1;
        });

        // Playhead
        var px = xOf(now, width);
        ctx.strokeStyle = '#e5484d';
        ctx.lineWidth = 2;
        ctx.beginPath(); ctx.moveTo(px, 0); ctx.lineTo(px, height); ctx.stroke();
        ctx.lineWidth = 1;
        timeline.setAttribute('aria-valuenow', String(Math.round(now)));
        timeline.setAttribute('aria-valuetext', clockText(now));
    }

    var scrubbing = null;
    timeline.addEventListener('pointerdown', function (e) {
        var rect = timeline.getBoundingClientRect();
        scrubbing = { x: e.clientX, pan: e.shiftKey || e.button === 1, start: view.start, end: view.end };
        timeline.setPointerCapture(e.pointerId);
        if (!scrubbing.pan) seek(timeAt(e.clientX - rect.left, rect.width));
    });
    timeline.addEventListener('pointermove', function (e) {
        if (!scrubbing) return;
        var rect = timeline.getBoundingClientRect();
        if (scrubbing.pan) {
            var shift = (scrubbing.x - e.clientX) / rect.width * (scrubbing.end - scrubbing.start);
            var span = scrubbing.end - scrubbing.start;
            var start = Math.max(rangeStart, Math.min(rangeEnd - span, scrubbing.start + shift));
            view = { start: start, end: start + span };
            drawTimeline();
        } else {
            seek(timeAt(e.clientX - rect.left, rect.width));
        }
    });
    timeline.addEventListener('pointerup', function () { scrubbing = null; });
    timeline.addEventListener('wheel', function (e) {
        e.preventDefault();
        var rect = timeline.getBoundingClientRect();
        zoomView(e.deltaY < 0 ? 0.8 : 1.25, timeAt(e.clientX - rect.left, rect.width));
    }, { passive: false });
    window.addEventListener('resize', function () { resizeTimeline(); drawTimeline(); tiles.forEach(drawBoxes); });

    // Keyboard: space plays and pauses; arrows move 5 s (with Shift, one frame); [ and ] jump between
    // events; - and + change speed. Not while typing in a field.
    document.addEventListener('keydown', function (e) {
        if (pages.player.hidden || e.ctrlKey || e.metaKey || e.altKey) return;
        var tag = (e.target && e.target.tagName) || '';
        if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
        if (e.key === ' ' && tag !== 'BUTTON') { setPlaying(!playing); e.preventDefault(); }
        else if (e.key === 'ArrowLeft') { if (e.shiftKey) step(-1); else seek(now - 5000); e.preventDefault(); }
        else if (e.key === 'ArrowRight') { if (e.shiftKey) step(1); else seek(now + 5000); e.preventDefault(); }
        else if (e.key === '[') nextEvent(-1);
        else if (e.key === ']') nextEvent(1);
        else if (e.key === '-' || e.key === '+' || e.key === '=') {
            var i = speeds.indexOf(speed) + (e.key === '-' ? -1 : 1);
            if (i >= 0 && i < speeds.length) { speed = speeds[i]; speedSelect.value = String(speed); syncTiles(false); }
        }
    });

    var stage = el('div', { class: 'ep-stage' }, [
        el('div', { class: 'ep-toolbar' }, [
            el('label', null, [t('js.exportPlayer.layout') + ' ', layoutSelect]),
            el('label', null, [boxesToggle, t('js.exportPlayer.showBoxes')])
        ]),
        grid,
        transport,
        el('div', { class: 'ep-timeline-wrap' }, [timeline]),
        zoomBar
    ]);
    pages.player.appendChild(side);
    pages.player.appendChild(stage);

    // ── Events tab ──────────────────────────────────────────────────────────

    var filterCamera = el('select', { 'aria-label': t('js.exportPlayer.camera') },
        [el('option', { value: '', text: t('js.exportPlayer.allCameras') })].concat(cameras.map(function (c) { return el('option', { value: c.id, text: c.name }); })));
    var labels = [];
    events.forEach(function (e) { if (labels.indexOf(e.label) < 0) labels.push(e.label); });
    labels.sort();
    var filterKind = el('select', { 'aria-label': t('js.exportPlayer.what') },
        [el('option', { value: '', text: t('js.exportPlayer.everything') })].concat(labels.map(function (l) { return el('option', { value: l, text: l }); })));
    var eventsBody = el('div', { class: 'ep-table-wrap' });
    filterCamera.addEventListener('change', renderEvents);
    filterKind.addEventListener('change', renderEvents);
    pages.events.appendChild(el('div', { class: 'ep-filters' }, [filterCamera, filterKind]));
    pages.events.appendChild(eventsBody);

    var MAX_ROWS = 2000;
    function renderEvents() {
        clear(eventsBody);
        var rows = events.filter(function (e) {
            return (!filterCamera.value || e.cameraId === filterCamera.value) && (!filterKind.value || e.label === filterKind.value);
        });
        if (rows.length === 0) { eventsBody.appendChild(el('p', { class: 'ep-muted', text: t('js.exportPlayer.noEvents') })); return; }
        var tbody = el('tbody');
        rows.slice(0, MAX_ROWS).forEach(function (e) {
            var play = el('button', { type: 'button', class: 'btn', text: t('js.exportPlayer.play') });
            play.addEventListener('click', function () {
                if (cells.indexOf(e.cameraId) < 0) {
                    var free = cells.indexOf(null);
                    cells[free >= 0 ? free : selectedCell] = e.cameraId;
                    buildGrid();
                }
                showTab('player');
                seek(e.start - 1000);
            });
            tbody.appendChild(el('tr', null, [
                el('td', { text: fmt('full', e.start) }),
                el('td', { text: (camerasById[e.cameraId] || {}).name || '' }),
                el('td', null, [el('span', { class: 'ep-swatch', style: 'background:' + e.color }), e.label + (e.count > 1 ? ' ×' + e.count : '')]),
                el('td', { text: duration(e.end - e.start) }),
                el('td', null, [play])
            ]));
        });
        eventsBody.appendChild(el('table', { class: 'ep-table' }, [
            el('thead', null, [el('tr', null, [
                el('th', { text: t('js.exportPlayer.when') }), el('th', { text: t('js.exportPlayer.camera') }),
                el('th', { text: t('js.exportPlayer.what') }), el('th', { text: t('js.exportPlayer.howLong') }), el('th', { text: '' })
            ])]),
            tbody
        ]));
        if (rows.length > MAX_ROWS) eventsBody.appendChild(el('p', { class: 'ep-muted ep-small', text: t('js.exportPlayer.moreEvents', { shown: MAX_ROWS, total: rows.length }) }));
        if (X.eventsTruncated) eventsBody.appendChild(el('p', { class: 'ep-muted ep-small', text: t('js.exportPlayer.eventsTruncated') }));
    }

    // ── About tab ───────────────────────────────────────────────────────────

    function facts(pairs) {
        var dl = el('dl', { class: 'ep-facts' });
        pairs.forEach(function (p) { if (p[1] != null && p[1] !== '') { dl.appendChild(el('dt', { text: p[0] })); dl.appendChild(el('dd', { text: String(p[1]) })); } });
        return dl;
    }

    function table(headers, rows) {
        return el('div', { class: 'ep-table-wrap' }, [el('table', { class: 'ep-table' }, [
            el('thead', null, [el('tr', null, headers.map(function (h) { return el('th', { text: h }); }))]),
            el('tbody', null, rows.map(function (r) { return el('tr', null, r.map(function (c) { return el('td', { text: c == null ? '' : String(c) }); })); }))
        ])]);
    }

    function size(bytes) {
        if (bytes == null) return '';
        var units = ['B', 'KB', 'MB', 'GB', 'TB'], i = 0, v = bytes;
        while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
        return (i ? v.toFixed(1) : String(v)) + ' ' + units[i];
    }

    function logAction(entry) {
        switch (entry.action) {
            case 'requested': return t('js.exportPlayer.logRequested', { by: entry.by || '?' });
            case 'exported': return t('js.exportPlayer.logExported', { camera: entry.camera || '?', node: entry.node || '?' })
                + (entry.redacted ? ' ' + t('js.exportPlayer.logRedacted') : '') + (entry.converted ? ' ' + t('js.exportPlayer.logConverted') : '');
            case 'packaged': return t('js.exportPlayer.logPackaged', { by: entry.by || '?' });
            default: return entry.action;
        }
    }

    function renderAbout() {
        var page = pages.about;
        clear(page);
        page.appendChild(el('div', { class: 'ep-section' }, [
            el('h2', { text: t('js.exportPlayer.aboutThisExport') }),
            facts([
                [t('js.exportPlayer.range'), fmt('full', rangeStart) + ' – ' + fmt('full', rangeEnd)],
                [t('js.exportPlayer.cameras'), cameras.length],
                [t('js.exportPlayer.requestedBy'), X.requestedBy],
                [t('js.exportPlayer.packagedBy'), (X.packagedBy || '') + ', ' + fmt('full', ms(X.packagedUtc))],
                [t('js.exportPlayer.siteTimeZone'), X.siteTimeZone],
                [t('js.exportPlayer.software'), 'LarisVMS ' + (X.serverVersion || '')],
                [t('js.exportPlayer.exportId'), X.jobId]
            ]),
            el('p', { class: 'ep-muted ep-small', text: t('js.exportPlayer.timesNote') })
        ]));

        page.appendChild(el('div', { class: 'ep-section' }, [
            el('h2', { text: t('js.exportPlayer.log') }),
            table([t('js.exportPlayer.when'), t('js.exportPlayer.what')], (X.log || []).map(function (l) { return [fmt('full', ms(l.atUtc)), logAction(l)]; }))
        ]));

        if ((X.notExported || []).length) {
            page.appendChild(el('div', { class: 'ep-section' }, [
                el('h2', { text: t('js.exportPlayer.notExported') }),
                table([t('js.exportPlayer.camera'), t('js.exportPlayer.reason')], X.notExported.map(function (n) { return [n.camera, n.error]; }))
            ]));
        }

        var fileRows = [];
        cameras.forEach(function (c) {
            c.files.forEach(function (f) {
                fileRows.push([c.name, f.path, f.raw.codec, size(f.raw.sizeBytes), f.raw.redacted ? t('js.exportPlayer.yes') : '', f.h264Path ? t('js.exportPlayer.yes') : '']);
            });
        });
        page.appendChild(el('div', { class: 'ep-section' }, [
            el('h2', { text: t('js.exportPlayer.files') }),
            table([t('js.exportPlayer.camera'), t('js.exportPlayer.file'), t('js.exportPlayer.codec'), t('js.exportPlayer.size'), t('js.exportPlayer.redacted'), t('js.exportPlayer.h264Copy')], fileRows)
        ]));

        if (bookmarks.length) {
            page.appendChild(el('div', { class: 'ep-section' }, [
                el('h2', { text: t('js.exportPlayer.bookmarks') }),
                table([t('js.exportPlayer.when'), t('js.exportPlayer.camera'), t('js.exportPlayer.note'), t('js.exportPlayer.by')],
                    bookmarks.map(function (b) { return [fmt('full', b.at), (camerasById[b.cameraId] || {}).name, b.note, b.by]; }))
            ]));
        }
        if (locks.length) {
            page.appendChild(el('div', { class: 'ep-section' }, [
                el('h2', { text: t('js.exportPlayer.evidenceLocks') }),
                table([t('js.exportPlayer.camera'), t('js.exportPlayer.range'), t('js.exportPlayer.reason'), t('js.exportPlayer.by')],
                    locks.map(function (l) { return [(camerasById[l.cameraId] || {}).name, fmt('full', l.from) + ' – ' + fmt('full', l.to), l.reason, l.by]; }))
            ]));
        }
    }

    // ── Verify tab ──────────────────────────────────────────────────────────

    var core = window.larisvmsExportVerify;
    var folderInput = el('input', { type: 'file', id: 'epFolder', webkitdirectory: true, multiple: true });
    var verifyButton = el('button', { type: 'button', class: 'btn btn-primary', disabled: true, text: t('js.exportPlayer.verify') });
    var progressBar = el('div');
    var progressWrap = el('div', { class: 'ep-progress', hidden: true, role: 'progressbar', 'aria-label': t('js.exportPlayer.checking') }, [progressBar]);
    var verifyResult = el('div', { 'aria-live': 'polite' });
    folderInput.addEventListener('change', function () { verifyButton.disabled = !folderInput.files.length; });
    verifyButton.addEventListener('click', runVerify);
    pages.verify.appendChild(el('div', { class: 'ep-section' }, [
        el('h2', { text: t('js.exportPlayer.verifyTitle') }),
        el('p', { text: t('js.exportPlayer.verifyIntro') }),
        el('p', { class: 'ep-muted ep-small', text: t('js.exportPlayer.verifyHow') }),
        el('p', null, [el('label', { for: 'epFolder', text: t('js.exportPlayer.chooseFolder') + ' ' }), folderInput]),
        verifyButton,
        progressWrap,
        verifyResult
    ]));

    // Junk an operating system adds to a folder by itself; not counted as an added file.
    var IGNORED = /(^|\/)(\.DS_Store|Thumbs\.db|desktop\.ini|__MACOSX\/.*|\._[^/]*)$/i;

    async function runVerify() {
        clear(verifyResult);
        verifyButton.disabled = true;
        var lines = [];
        try {
            if (!core || !window.crypto || !crypto.subtle) throw new Error(t('js.exportPlayer.noCrypto'));
            // The chosen folder's own name comes first in each path; the package's list doesn't have it.
            var byPath = {};
            Array.prototype.forEach.call(folderInput.files, function (f) {
                var rel = (f.webkitRelativePath || f.name).split('/').slice(1).join('/');
                if (rel) byPath[rel] = f;
            });
            var manifestFile = byPath['package.manifest.json'];
            if (!manifestFile) throw new Error(t('js.exportPlayer.noPackageManifest'));
            var signed = core.readSigned(await manifestFile.text());
            var manifest = signed.manifest, payload = signed.payload;

            var valid = await core.verifySignature(manifest.publicKeyPem, manifest.signedPayload, manifest.signature);
            var print = await core.fingerprint(manifest.publicKeyPem);
            lines.push(valid
                ? core.line('ok', t('js.exportPlayer.listSigned'), t('js.exportPlayer.listSignedDetail', { key: manifest.keyId, fingerprint: print }))
                : core.line('bad', t('js.exportPlayer.listNotSigned'), t('js.exportPlayer.listNotSignedDetail')));

            // Every listed file: there, the same size, the same SHA-256.
            var listed = payload.files || [];
            var total = listed.reduce(function (sum, f) { return sum + (f.sizeBytes || 0); }, 0);
            var done = 0, missing = [], changed = [];
            progressWrap.hidden = false;
            for (var i = 0; i < listed.length; i++) {
                var entry = listed[i], file = byPath[entry.path];
                if (!file) { missing.push(entry.path); done += entry.sizeBytes || 0; continue; }
                if (file.size !== entry.sizeBytes) { changed.push(entry.path); done += entry.sizeBytes || 0; continue; }
                var base = done;
                var hex = await core.hashFile(file, function (fraction) { progressBar.style.width = Math.round((base + fraction * file.size) / Math.max(1, total) * 100) + '%'; });
                if (hex !== entry.sha256) changed.push(entry.path);
                done += entry.sizeBytes || 0;
            }
            progressWrap.hidden = true;
            if (missing.length === 0 && changed.length === 0) lines.push(core.line('ok', t('js.exportPlayer.filesIntact'), plural('js.exportPlayer.filesIntactDetail', listed.length)));
            if (changed.length) lines.push(core.line('bad', t('js.exportPlayer.filesChanged'), changed.join(', ')));
            if (missing.length) lines.push(core.line('bad', t('js.exportPlayer.filesMissing'), missing.join(', ')));
            var known = {};
            listed.forEach(function (f) { known[f.path] = true; });
            var extra = Object.keys(byPath).filter(function (p) { return p !== 'package.manifest.json' && !known[p] && !IGNORED.test(p); });
            if (extra.length) lines.push(core.line('warn', t('js.exportPlayer.filesAdded'), extra.join(', ')));

            // Each camera's own signed manifest, and its footage chain.
            var manifests = listed.filter(function (f) { return f.path.indexOf('manifests/') === 0; });
            for (var m = 0; m < manifests.length; m++) {
                var mf = byPath[manifests[m].path];
                if (!mf) continue;
                var cam = core.readSigned(await mf.text());
                var name = cam.payload.camera ? cam.payload.camera.name : manifests[m].path;
                var camValid = await core.verifySignature(cam.manifest.publicKeyPem, cam.manifest.signedPayload, cam.manifest.signature);
                lines.push(camValid
                    ? core.line('ok', t('js.exportPlayer.cameraManifestSigned', { camera: name }), cam.manifest.keyId === manifest.keyId ? '' : t('js.exportPlayer.otherKey', { key: cam.manifest.keyId }))
                    : core.line('bad', t('js.exportPlayer.cameraManifestNotSigned', { camera: name }), ''));
                // The video it vouches for is the one in the package.
                var videoName = cam.payload.file && cam.payload.file.name;
                var video = listed.filter(function (f) { return videoName && f.path.indexOf('video/') === 0 && f.path.slice(-videoName.length - 1) === '/' + videoName; })[0];
                if (video && video.sha256 !== cam.payload.file.sha256) lines.push(core.line('bad', t('js.exportPlayer.videoNotTheExported', { camera: name }), video.path));
                var chain = await core.checkChain(cam.payload);
                chain.forEach(function (l) { lines.push(core.line(l.state, name + ': ' + l.title, l.detail)); });
            }

            var ok = !lines.some(function (l) { return l.state === 'bad'; });
            verifyResult.appendChild(el('div', { class: 'ep-verdict ' + (ok ? 'ok' : 'bad'), text: ok ? t('js.exportPlayer.verified') : t('js.exportPlayer.notVerified') }));
            verifyResult.appendChild(el('ul', { class: 'ep-checks' }, lines.map(function (l) {
                return el('li', { class: l.state }, [
                    el('span', { class: 'ep-mark', 'aria-hidden': 'true', text: l.state === 'ok' ? '✓' : l.state === 'warn' ? '!' : '✗' }),
                    el('div', null, [el('div', { text: l.title }), l.detail ? el('div', { class: 'ep-muted ep-small ep-mono', text: l.detail }) : null])
                ]);
            })));
        } catch (e) {
            verifyResult.appendChild(el('div', { class: 'ep-verdict bad', text: (e && e.message) || t('js.exportPlayer.couldntVerify') }));
        } finally {
            progressWrap.hidden = true;
            verifyButton.disabled = !folderInput.files.length;
        }
    }

    // ── Start ───────────────────────────────────────────────────────────────

    renderHeader();
    renderClock();
    showTab('player');
    buildGrid();
    resizeTimeline();
    drawTimeline();
    requestAnimationFrame(function (frameTime) { lastFrame = frameTime; requestAnimationFrame(tick); });
})();

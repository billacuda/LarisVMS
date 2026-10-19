// Map page (Components/Pages/Maps): cameras placed on a street map (Leaflet tiles, OpenStreetMap by
// default) or an uploaded floorplan, each drawn with a cone showing where it points, how wide it sees
// and how far — coloured by status (recording / offline / disabled), refreshed every 30 seconds.
// Clicking a camera shows its latest picture with links to live view and playback.
//
// Edit mode (Cameras.Edit): drag a camera from the side list onto the map to place it; drag its icon
// to move it; drag the handle at the tip of its cone to aim it (direction and range); use the side
// panel for the cone's width or to take the camera off the map. Every change saves straight away.
//
// Coordinates: on a street map, Y/X are latitude/longitude and range is metres. On a floorplan the map
// uses Leaflet's simple CRS over the image — Y up from the bottom edge, X from the left, in image
// pixels — and range is in pixels too. Bearing is clockwise from north (the top of the image).
(function () {
    'use strict';

    var STATUS = {
        recording: { color: '#198754', label: 'Recording' },
        offline: { color: '#dc3545', label: 'Offline' },
        disabled: { color: '#6c757d', label: 'Disabled' }
    };
    var POLL_MS = 30000;
    var MAP_PREF = 'mapId';
    var OSM = {
        tileUrl: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
    };

    var configEl = document.getElementById('mapConfig');
    var canvas = document.getElementById('mapCanvas');
    if (!configEl || !canvas || typeof L === 'undefined') return;
    var config = JSON.parse(configEl.textContent || '{}');

    var select = document.getElementById('mapSelect');
    var statusEl = document.getElementById('mapStatus');
    var emptyEl = document.getElementById('mapEmpty');
    var editToggle = document.getElementById('mapEditToggle');
    var editPanel = document.getElementById('mapEditPanel');
    var selectedEl = document.getElementById('mapSelected');
    var paletteEl = document.getElementById('mapPalette');
    var paletteFilter = document.getElementById('mapPaletteFilter');

    var maps = [];
    var tiles = OSM;
    var current = null;     // SiteMapDto
    var lmap = null;        // Leaflet map
    var layer = null;       // cameras layer
    var editing = false;
    var selectedId = null;
    var dragging = false;
    var pollTimer = null;

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function setStatus(text, isError) {
        statusEl.textContent = text || '';
        statusEl.className = 'small ms-auto ' + (isError ? 'text-danger' : 'text-muted');
    }

    function api(url, method, body) {
        return fetch(url, {
            method: method || 'GET',
            headers: body ? { 'Content-Type': 'application/json' } : undefined,
            body: body ? JSON.stringify(body) : undefined
        }).then(function (r) {
            if (!r.ok) return r.text().then(function (t) { throw new Error(t || ('HTTP ' + r.status)); });
            return r.status === 204 ? null : r.json();
        });
    }

    function isStreet() { return current && current.kind === 'Street'; }

    // ── Geometry ────────────────────────────────────────────────────────────

    var R = 6378137; // metres
    function rad(d) { return d * Math.PI / 180; }
    function deg(r) { return r * 180 / Math.PI; }

    // The point `range` away from (y, x) in direction `bearing`.
    function destination(y, x, bearing, range) {
        var t = rad(bearing);
        if (!isStreet()) return [y + range * Math.cos(t), x + range * Math.sin(t)];
        var d = range / R, p1 = rad(y), l1 = rad(x);
        var p2 = Math.asin(Math.sin(p1) * Math.cos(d) + Math.cos(p1) * Math.sin(d) * Math.cos(t));
        var l2 = l1 + Math.atan2(Math.sin(t) * Math.sin(d) * Math.cos(p1), Math.cos(d) - Math.sin(p1) * Math.sin(p2));
        return [deg(p2), deg(l2)];
    }

    // Bearing and range from (y, x) to a point — for the aiming handle.
    function aim(y, x, to) {
        if (!isStreet()) {
            var dy = to.lat - y, dx = to.lng - x;
            return { bearing: (deg(Math.atan2(dx, dy)) + 360) % 360, range: Math.max(1, Math.hypot(dx, dy)) };
        }
        var p1 = rad(y), p2 = rad(to.lat), dl = rad(to.lng - x);
        var b = Math.atan2(Math.sin(dl) * Math.cos(p2), Math.cos(p1) * Math.sin(p2) - Math.sin(p1) * Math.cos(p2) * Math.cos(dl));
        return { bearing: (deg(b) + 360) % 360, range: Math.max(1, L.latLng(y, x).distanceTo(to)) };
    }

    function conePoints(p) {
        if (p.fovDeg >= 360) {
            var circle = [];
            for (var a = 0; a < 360; a += 10) circle.push(destination(p.y, p.x, a, p.range));
            return circle;
        }
        var pts = [[p.y, p.x]];
        var steps = Math.max(2, Math.ceil(p.fovDeg / 5));
        for (var i = 0; i <= steps; i++) pts.push(destination(p.y, p.x, p.bearingDeg - p.fovDeg / 2 + p.fovDeg * i / steps, p.range));
        return pts;
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    function cameraIcon(p) {
        var color = (STATUS[p.status] || STATUS.offline).color;
        return L.divIcon({
            className: 'lv-map-cam' + (p.cameraId === selectedId ? ' lv-map-cam--selected' : ''),
            html: '<span style="background:' + color + '" aria-hidden="true">📷</span>',
            iconSize: [30, 30], iconAnchor: [15, 15]
        });
    }

    var handleIcon = L.divIcon({ className: 'lv-map-aim', html: '<span></span>', iconSize: [16, 16], iconAnchor: [8, 8] });

    function popupHtml(p) {
        var st = STATUS[p.status] || STATUS.offline;
        return '<div class="lv-map-popup">' +
            '<div class="fw-semibold mb-1">' + escapeHtml(p.cameraName) + ' <span class="badge" style="background:' + st.color + '">' + st.label + '</span></div>' +
            '<img src="/playback-thumbnail/' + p.cameraId + '/latest?t=' + Date.now() + '" alt="" class="rounded mb-2" data-on-error="hide">' +
            '<div class="d-flex gap-2"><a class="btn btn-sm btn-primary" href="/Views/Play?cameraId=' + p.cameraId + '">Live</a>' +
            '<a class="btn btn-sm btn-outline-secondary" href="/Playback?cameraId=' + p.cameraId + '">Playback</a></div></div>';
    }

    function render() {
        if (!lmap || !current) return;
        layer.clearLayers();
        current.placements.forEach(function (p) {
            var color = (STATUS[p.status] || STATUS.offline).color;
            var cone = L.polygon(conePoints(p), { color: color, weight: 1, fillOpacity: p.cameraId === selectedId ? 0.35 : 0.2, interactive: false }).addTo(layer);
            var marker = L.marker([p.y, p.x], { icon: cameraIcon(p), draggable: editing, keyboard: true, title: p.cameraName, zIndexOffset: 1000 }).addTo(layer);
            marker.bindTooltip(escapeHtml(p.cameraName), { direction: 'top', offset: [0, -14] });

            if (!editing) {
                marker.bindPopup(function () { return popupHtml(p); }, { minWidth: 220 });
                return;
            }

            marker.on('click', function () { selectCamera(p.cameraId); });
            marker.on('dragstart', function () { dragging = true; });
            marker.on('drag', function (e) {
                p.y = e.latlng.lat; p.x = e.latlng.lng;
                cone.setLatLngs(conePoints(p));
                if (handle) handle.setLatLng(destination(p.y, p.x, p.bearingDeg, p.range));
            });
            marker.on('dragend', function () { dragging = false; save(p); });

            // The aiming handle — only on the selected camera, to keep the map uncluttered.
            var handle = null;
            if (p.cameraId === selectedId && p.fovDeg < 360) {
                handle = L.marker(destination(p.y, p.x, p.bearingDeg, p.range), { icon: handleIcon, draggable: true, title: 'Drag to aim' }).addTo(layer);
                handle.on('dragstart', function () { dragging = true; });
                handle.on('drag', function (e) {
                    var a = aim(p.y, p.x, e.latlng);
                    p.bearingDeg = a.bearing; p.range = a.range;
                    cone.setLatLngs(conePoints(p));
                });
                handle.on('dragend', function () {
                    dragging = false;
                    handle.setLatLng(destination(p.y, p.x, p.bearingDeg, p.range));
                    save(p);
                    renderSelected();
                });
            }
        });
        renderPalette();
        renderSelected();
    }

    // ── Edit panel ──────────────────────────────────────────────────────────

    function placementOf(id) { return current && current.placements.filter(function (p) { return p.cameraId === id; })[0]; }

    function selectCamera(id) {
        selectedId = id;
        render();
    }

    function rangeUnit() { return isStreet() ? 'm' : 'px'; }

    function renderSelected() {
        if (!selectedEl) return;
        var p = selectedId && placementOf(selectedId);
        if (!p) { selectedEl.innerHTML = '<span class="text-muted">Click a camera on the map.</span>'; return; }
        selectedEl.innerHTML =
            '<div class="fw-semibold mb-2">' + escapeHtml(p.cameraName) + '</div>' +
            '<label class="form-label mb-0" for="mapFov">Field of view: <span id="mapFovValue">' + Math.round(p.fovDeg) + '°</span></label>' +
            '<input type="range" class="form-range" id="mapFov" min="5" max="360" step="5" value="' + Math.round(p.fovDeg) + '">' +
            '<label class="form-label mb-0" for="mapRange">Range (' + rangeUnit() + ')</label>' +
            '<input type="number" class="form-control form-control-sm mb-1" id="mapRange" min="1" step="1" value="' + Math.round(p.range) + '">' +
            '<label class="form-label mb-0" for="mapBearing">Direction (° from north)</label>' +
            '<input type="number" class="form-control form-control-sm mb-2" id="mapBearing" min="0" max="359" step="1" value="' + Math.round(p.bearingDeg) + '">' +
            '<div class="text-muted mb-2">Or drag the white handle at the tip of the cone.</div>' +
            '<button type="button" class="btn btn-sm btn-outline-danger" id="mapRemove">Take off this map</button>';
        var fov = document.getElementById('mapFov');
        fov.addEventListener('input', function () {
            p.fovDeg = +fov.value;
            document.getElementById('mapFovValue').textContent = fov.value + '°';
            render();
        });
        fov.addEventListener('change', function () { save(p); });
        document.getElementById('mapRange').addEventListener('change', function (e) {
            var v = +e.target.value; if (v > 0) { p.range = v; save(p); render(); }
        });
        document.getElementById('mapBearing').addEventListener('change', function (e) {
            var v = +e.target.value; if (isFinite(v)) { p.bearingDeg = ((v % 360) + 360) % 360; save(p); render(); }
        });
        document.getElementById('mapRemove').addEventListener('click', function () {
            api('/api/maps/' + current.id + '/placements/' + p.cameraId, 'DELETE').then(function () {
                current.placements = current.placements.filter(function (x) { return x.cameraId !== p.cameraId; });
                selectedId = null;
                render();
                setStatus('Removed ' + p.cameraName + ' from this map.');
            }).catch(function () { setStatus('Couldn’t remove that camera. Try again.', true); });
        });
    }

    function renderPalette() {
        if (!paletteEl) return;
        var placed = {};
        (current ? current.placements : []).forEach(function (p) { placed[p.cameraId] = true; });
        var q = (paletteFilter && paletteFilter.value || '').toLowerCase();
        var available = (config.cameras || []).filter(function (c) { return !placed[c.id] && c.name.toLowerCase().indexOf(q) >= 0; });
        paletteEl.innerHTML = available.length === 0
            ? '<div class="text-muted px-2">' + ((config.cameras || []).length ? 'Every camera is on this map.' : 'No cameras.') + '</div>'
            : available.map(function (c) {
                return '<div class="list-group-item list-group-item-action py-1 px-2 lv-map-palette-item" draggable="true" data-camera-id="' + c.id + '">📷 ' + escapeHtml(c.name) + '</div>';
            }).join('');
    }

    if (paletteEl) {
        paletteEl.addEventListener('dragstart', function (e) {
            var item = e.target.closest('[data-camera-id]');
            if (!item) return;
            e.dataTransfer.setData('text/plain', item.getAttribute('data-camera-id'));
            e.dataTransfer.effectAllowed = 'copy';
        });
        paletteFilter.addEventListener('input', renderPalette);
    }

    canvas.addEventListener('dragover', function (e) { if (editing) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
    canvas.addEventListener('drop', function (e) {
        if (!editing || !lmap || !current) return;
        e.preventDefault();
        var cameraId = e.dataTransfer.getData('text/plain');
        var cam = (config.cameras || []).filter(function (c) { return c.id === cameraId; })[0];
        if (!cam) return;
        var at = lmap.containerPointToLatLng(lmap.mouseEventToContainerPoint(e));
        var p = {
            cameraId: cam.id, cameraName: cam.name, status: 'offline', y: at.lat, x: at.lng,
            bearingDeg: 0, fovDeg: 90,
            // A sensible first size: 30 m outdoors, a tenth of the image's width on a floorplan.
            range: isStreet() ? 30 : Math.max(20, (current.imageWidth || 1000) / 10)
        };
        current.placements.push(p);
        selectedId = p.cameraId;
        save(p).then(function () { refresh(); });
        render();
    });

    function save(p) {
        setStatus('Saving…');
        return api('/api/maps/' + current.id + '/placements/' + p.cameraId, 'PUT',
            { y: p.y, x: p.x, bearingDeg: p.bearingDeg, fovDeg: p.fovDeg, range: p.range })
            .then(function () { setStatus('Saved.'); })
            .catch(function () { setStatus('Couldn’t save that change. Try again.', true); });
    }

    // ── Maps ────────────────────────────────────────────────────────────────

    // Each person's own last view of each map, so coming back opens where they left it.
    function viewKey(id) { return 'mapView.' + id; }

    function rememberedView(id) {
        try {
            var v = JSON.parse(window.larisvmsPreferences.get(viewKey(id), '') || 'null');
            return v && isFinite(v.lat) && isFinite(v.lng) && isFinite(v.zoom) ? v : null;
        } catch (e) { return null; }
    }

    var viewSaveTimer = null;
    function rememberView() {
        if (!lmap || !current) return;
        var id = current.id, c = lmap.getCenter(), z = lmap.getZoom();
        clearTimeout(viewSaveTimer);
        viewSaveTimer = setTimeout(function () {
            window.larisvmsPreferences.set(viewKey(id), JSON.stringify({
                lat: Math.round(c.lat * 1e6) / 1e6, lng: Math.round(c.lng * 1e6) / 1e6, zoom: z
            }));
        }, 800);
    }

    function buildLeaflet() {
        if (lmap) { lmap.remove(); lmap = null; }
        if (!current) return;
        // Opens at this person's last view, else the map's own default, else the whole world/image.
        var mine = rememberedView(current.id);
        if (isStreet()) {
            lmap = L.map(canvas, { worldCopyJump: true });
            L.tileLayer(tiles.tileUrl, { attribution: tiles.attribution, maxZoom: 20, maxNativeZoom: 19 }).addTo(lmap);
            if (mine) lmap.setView([mine.lat, mine.lng], mine.zoom);
            else lmap.setView([current.centerLat != null ? current.centerLat : 20, current.centerLng != null ? current.centerLng : 0],
                current.zoom != null ? current.zoom : 2);
        } else {
            lmap = L.map(canvas, { crs: L.CRS.Simple, minZoom: -5, maxZoom: 4, zoomSnap: 0.25, attributionControl: false });
            var bounds = [[0, 0], [current.imageHeight || 1000, current.imageWidth || 1000]];
            L.imageOverlay('/api/maps/' + current.id + '/image', bounds).addTo(lmap);
            if (mine) lmap.setView([mine.lat, mine.lng], mine.zoom);
            else if (current.zoom != null && current.centerLat != null && current.centerLng != null)
                lmap.setView([current.centerLat, current.centerLng], current.zoom);
            else lmap.fitBounds(bounds);
            lmap.setMaxBounds(L.latLngBounds(bounds).pad(0.5));
        }
        layer = L.layerGroup().addTo(lmap);
        lmap.on('click', function () { if (editing && selectedId) { selectedId = null; render(); } });
        // Attached after the initial view, so only the person's own panning and zooming is remembered.
        lmap.on('moveend', rememberView);
    }

    // Saves the current view as where this map opens for everyone without a view of their own.
    function saveDefaultView() {
        if (!current || !lmap) return Promise.resolve();
        var c = lmap.getCenter(), z = Math.round(lmap.getZoom());
        return api('/api/maps/' + current.id, 'PUT', { centerLat: c.lat, centerLng: c.lng, zoom: z })
            .then(function () {
                setStatus('Saved. This map now opens here for everyone.');
                current.centerLat = c.lat; current.centerLng = c.lng; current.zoom = z;
            })
            .catch(function () { setStatus('Couldn’t save the view.', true); });
    }

    function openMap(id) {
        selectedId = null;
        return api('/api/maps/' + id).then(function (dto) {
            current = dto;
            window.larisvmsPreferences.set(MAP_PREF, id);
            try { history.replaceState(null, '', '/Map?mapId=' + id); } catch (e) { /* bookmarking is a nicety */ }
            buildLeaflet();
            render();
        }).catch(function () { setStatus('Couldn’t load that map.', true); });
    }

    // Statuses change; positions don't (unless edited here). Refresh without disturbing a drag.
    function refresh() {
        if (!current || dragging) return;
        api('/api/maps/' + current.id).then(function (dto) {
            if (dragging || !current || dto.id !== current.id) return;
            current.placements = dto.placements;
            render();
        }).catch(function () { /* keep what's shown */ });
    }

    function loadMaps(preferId) {
        return api('/api/maps').then(function (data) {
            maps = data.maps || [];
            tiles = data.tiles || OSM;
            select.innerHTML = maps.map(function (m) {
                return '<option value="' + m.id + '">' + escapeHtml(m.name) + (m.kind === 'Floorplan' ? ' (floorplan)' : '') + '</option>';
            }).join('');
            var empty = maps.length === 0;
            emptyEl.classList.toggle('d-none', !empty);
            canvas.classList.toggle('d-none', empty);
            select.classList.toggle('d-none', empty);
            if (empty) { current = null; if (lmap) { lmap.remove(); lmap = null; } return; }
            var want = preferId || config.mapId || window.larisvmsPreferences.get(MAP_PREF, '');
            var chosen = maps.filter(function (m) { return m.id === want; })[0] || maps[0];
            select.value = chosen.id;
            return openMap(chosen.id);
        }).catch(function () { setStatus('Couldn’t load maps.', true); });
    }

    select.addEventListener('change', function () { openMap(select.value); });

    // ── Editing and management (Cameras.Edit) ───────────────────────────────

    if (editToggle) {
        editToggle.addEventListener('click', function () {
            editing = !editing;
            editToggle.classList.toggle('active', editing);
            editToggle.textContent = editing ? 'Done editing' : 'Edit map';
            editPanel.classList.toggle('d-none', !editing);
            if (!editing) { selectedId = null; saveDefaultView(); }
            if (lmap) setTimeout(function () { lmap.invalidateSize(); }, 50);
            render();
        });
    }

    var newModalEl = document.getElementById('mapNewModal');
    var newKind = 'Street';
    Array.prototype.forEach.call(document.querySelectorAll('[data-new-map]'), function (b) {
        b.addEventListener('click', function () {
            newKind = b.getAttribute('data-new-map');
            document.getElementById('mapNewTitle').textContent = newKind === 'Floorplan' ? 'New floorplan' : 'New street map';
            document.getElementById('mapNewFloorplan').classList.toggle('d-none', newKind !== 'Floorplan');
            document.getElementById('mapNewStreetHelp').classList.toggle('d-none', newKind === 'Floorplan');
            document.getElementById('mapNewName').value = '';
            document.getElementById('mapNewFile').value = '';
            document.getElementById('mapNewError').textContent = '';
            bootstrap.Modal.getOrCreateInstance(newModalEl).show();
        });
    });

    function readImage(file) {
        return new Promise(function (resolve, reject) {
            var reader = new FileReader();
            reader.onerror = function () { reject(new Error('That file couldn’t be read.')); };
            reader.onload = function () {
                var img = new Image();
                img.onload = function () {
                    var dataUrl = String(reader.result);
                    resolve({ base64: dataUrl.slice(dataUrl.indexOf(',') + 1), contentType: file.type, width: img.naturalWidth, height: img.naturalHeight });
                };
                img.onerror = function () { reject(new Error('That isn’t an image this browser can show.')); };
                img.src = String(reader.result);
            };
            reader.readAsDataURL(file);
        });
    }

    var createBtn = document.getElementById('mapNewCreate');
    if (createBtn) createBtn.addEventListener('click', function () {
        var errorEl = document.getElementById('mapNewError');
        var name = document.getElementById('mapNewName').value.trim();
        if (!name) { errorEl.textContent = 'Give the map a name.'; return; }
        var body = { name: name, kind: newKind };
        var ready;
        if (newKind === 'Floorplan') {
            var file = document.getElementById('mapNewFile').files[0];
            if (!file) { errorEl.textContent = 'Choose a floorplan image.'; return; }
            if (file.size > 10 * 1024 * 1024) { errorEl.textContent = 'Floorplan images can be up to 10 MB.'; return; }
            ready = readImage(file).then(function (img) {
                body.imageBase64 = img.base64; body.imageContentType = img.contentType;
                body.imageWidth = img.width; body.imageHeight = img.height;
            });
        } else {
            // Opens where the current street map is showing, if one is.
            if (lmap && isStreet()) {
                var c = lmap.getCenter();
                body.centerLat = c.lat; body.centerLng = c.lng; body.zoom = lmap.getZoom();
            }
            ready = Promise.resolve();
        }
        createBtn.disabled = true;
        ready.then(function () { return api('/api/maps', 'POST', body); })
            .then(function (res) {
                bootstrap.Modal.getOrCreateInstance(newModalEl).hide();
                setStatus('Created ' + name + '.');
                return loadMaps(res.id).then(function () {
                    if (!editing && editToggle) editToggle.click(); // straight into placing cameras
                });
            })
            .catch(function (e) { errorEl.textContent = (e && e.message) || 'Couldn’t create the map.'; })
            .then(function () { createBtn.disabled = false; });
    });

    function byId(id) { return document.getElementById(id); }

    if (byId('mapRename')) byId('mapRename').addEventListener('click', function () {
        if (!current) return;
        var name = prompt('New name for this map', current.name);
        if (!name || !name.trim()) return;
        api('/api/maps/' + current.id, 'PUT', { name: name.trim() })
            .then(function () { return loadMaps(current.id); })
            .catch(function () { setStatus('Couldn’t rename the map.', true); });
    });

    if (byId('mapSaveView')) byId('mapSaveView').addEventListener('click', saveDefaultView);

    if (byId('mapDelete')) byId('mapDelete').addEventListener('click', function () {
        if (!current || !confirm('Delete the map "' + current.name + '"? Its camera placements go with it; the cameras themselves aren’t affected.')) return;
        api('/api/maps/' + current.id, 'DELETE')
            .then(function () { window.larisvmsPreferences.set(MAP_PREF, ''); return loadMaps(); })
            .catch(function () { setStatus('Couldn’t delete the map.', true); });
    });

    var tilesModalEl = byId('mapTilesModal');
    if (byId('mapTileSettings')) byId('mapTileSettings').addEventListener('click', function () {
        byId('mapTileUrl').value = tiles.tileUrl;
        byId('mapTileAttribution').value = tiles.attribution;
        byId('mapTilesError').textContent = '';
        bootstrap.Modal.getOrCreateInstance(tilesModalEl).show();
    });
    if (byId('mapTilesDefault')) byId('mapTilesDefault').addEventListener('click', function () {
        byId('mapTileUrl').value = OSM.tileUrl;
        byId('mapTileAttribution').value = OSM.attribution;
    });
    if (byId('mapTilesSave')) byId('mapTilesSave').addEventListener('click', function () {
        var body = { tileUrl: byId('mapTileUrl').value.trim(), attribution: byId('mapTileAttribution').value.trim() };
        api('/api/maps/tile-settings', 'PUT', body).then(function () {
            bootstrap.Modal.getOrCreateInstance(tilesModalEl).hide();
            return loadMaps(current && current.id);
        }).catch(function (e) { byId('mapTilesError').textContent = (e && e.message) || 'Couldn’t save.'; });
    });

    window.larisvmsPreferences.whenReady().then(function () {
        loadMaps();
        pollTimer = setInterval(refresh, POLL_MS);
    });
})();

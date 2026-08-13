// M6 read-only view display (Pages/Views/Play). Desktop renders the saved GridStack geometry with
// plain CSS grid (no GridStack JS needed just to display a fixed layout); phones get a derived
// single/two-column stack — ordered top-to-bottom/left-to-right from the desktop positions, sized
// from each cell's aspect ratio, cameras with hideOnPhone skipped — never persisted, same "a view
// built on desktop gets a usable phone view for free" idea as frcastr's dashboard.
window.nidusvmsViewPlay = (function () {
    'use strict';

    var CELL_HEIGHT = 60; // matches the editor's GridStack cellHeight, for a consistent look
    var phoneQuery = window.matchMedia('(max-width: 767.98px)');

    var opts = null;
    var cells = [];
    var mobileTwoColumn = false;
    var cameraById = {};
    var stopFns = {};

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function buildCellHtml(cell) {
        var cam = cameraById[cell.cameraId];
        var name = cam ? cam.name : '';
        return (
            '<div class="h-100 d-flex flex-column border rounded overflow-hidden">' +
                '<div class="position-relative flex-grow-1 bg-black" style="min-height: 0;">' +
                    '<video class="view-cell-video" style="width:100%; height:100%; object-fit:contain;" muted playsinline></video>' +
                    '<div class="position-absolute top-50 start-50 translate-middle text-white small text-center px-2 view-cell-status"></div>' +
                    // Exactly two buttons, shown on hover — not native <video controls>: that
                    // includes click-anywhere-on-the-video-to-pause in most browsers, which makes no
                    // sense for a continuous live feed and was confirmed live as unwanted.
                    '<div class="position-absolute bottom-0 end-0 m-1 btn-group btn-group-sm view-cell-controls d-none">' +
                        '<button type="button" class="btn btn-outline-light view-cell-mute" style="padding:.1rem .35rem;" title="Mute">🔊</button>' +
                        '<button type="button" class="btn btn-outline-light view-cell-fullscreen" style="padding:.1rem .35rem;" title="Fullscreen">⛶</button>' +
                    '</div>' +
                '</div>' +
                '<div class="px-1 small text-truncate bg-body-tertiary">' + escHtml(name) + '</div>' +
            '</div>'
        );
    }

    function startCellVideo(el, cell) {
        var cam = cameraById[cell.cameraId];
        var video = el.querySelector('.view-cell-video');
        var status = el.querySelector('.view-cell-status');
        if (!video || !cam) return;

        var hoverTarget = video.parentElement;
        var controls = el.querySelector('.view-cell-controls');
        var muteBtn = el.querySelector('.view-cell-mute');
        var fullscreenBtn = el.querySelector('.view-cell-fullscreen');

        if (hoverTarget && controls) {
            hoverTarget.addEventListener('mouseenter', function () { controls.classList.remove('d-none'); });
            hoverTarget.addEventListener('mouseleave', function () { controls.classList.add('d-none'); });
        }
        function setMuteIcon() {
            if (!muteBtn) return;
            muteBtn.textContent = video.muted ? '🔇' : '🔊';
            muteBtn.title = video.muted ? 'Unmute' : 'Mute';
        }
        if (muteBtn) {
            muteBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                video.muted = !video.muted;
                setMuteIcon();
            });
        }
        if (fullscreenBtn) {
            fullscreenBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                if (video.requestFullscreen) video.requestFullscreen().catch(function () {});
            });
        }
        setMuteIcon();

        stopFns[cell.id] = window.nidusvmsLiveView.start(cell.cameraId, video, status, cam.codec, cam.hasAudio);
    }

    function stopAll() {
        Object.keys(stopFns).forEach(function (id) { stopFns[id](); });
        stopFns = {};
    }

    // Groups cells sharing a "row" the same way as the desktop layout reads: a row is defined by
    // the vertical span of its first cell — anything starting before that cell ends was beside it.
    function desktopRows(orderedCells) {
        var rows = [], bottom = -1;
        orderedCells.forEach(function (c) {
            if (rows.length && c.y < bottom) {
                rows[rows.length - 1].push(c);
            } else {
                rows.push([c]);
                bottom = c.y + c.h;
            }
        });
        return rows;
    }

    function renderDesktop(container) {
        var visible = cells.filter(function (c) { return cameraById[c.cameraId]; });
        container.style.display = 'grid';
        container.style.gridTemplateColumns = 'repeat(12, 1fr)';
        container.style.gridAutoRows = CELL_HEIGHT + 'px';
        container.style.gap = '6px';
        container.innerHTML = '';

        if (!visible.length) {
            container.innerHTML = '<p class="text-muted">This view has no cameras yet.</p>';
            return;
        }

        visible.forEach(function (cell) {
            var el = document.createElement('div');
            el.style.gridColumn = (cell.x + 1) + ' / span ' + cell.w;
            el.style.gridRow = (cell.y + 1) + ' / span ' + cell.h;
            el.innerHTML = buildCellHtml(cell);
            container.appendChild(el);
            startCellVideo(el, cell);
        });
    }

    function renderMobile(container) {
        var cols = mobileTwoColumn ? 2 : 1;
        var visible = cells.filter(function (c) { return !c.hideOnPhone && cameraById[c.cameraId]; });
        var ordered = visible.slice().sort(function (a, b) { return (a.y - b.y) || (a.x - b.x); });
        var rows = desktopRows(ordered);

        container.style.display = 'grid';
        container.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
        container.style.gap = '6px';
        container.style.gridAutoRows = '';
        container.innerHTML = '';

        if (!visible.length) {
            container.innerHTML = '<p class="text-muted">No cameras in this view are visible on phones.</p>';
            return;
        }

        rows.forEach(function (row) {
            for (var i = 0; i < row.length; i += cols) {
                var pair = row.slice(i, i + cols);
                pair.forEach(function (cell) {
                    var el = document.createElement('div');
                    var ratio = window.NidusVMSAspectRatio.isValid(cell.aspect) ? cell.aspect : window.NidusVMSAspectRatio.default;
                    el.style.aspectRatio = ratio.replace(':', '/');
                    if (pair.length < cols) el.style.gridColumn = '1 / -1'; // odd one out spans full width
                    el.innerHTML = buildCellHtml(cell);
                    container.appendChild(el);
                    startCellVideo(el, cell);
                });
            }
        });
    }

    function render() {
        stopAll();
        var container = document.getElementById(opts.gridElId);
        if (!container) return;
        if (phoneQuery.matches) renderMobile(container); else renderDesktop(container);
    }

    function wireKiosk(kioskBtnId, navElId, toolbarElId) {
        var btn = document.getElementById(kioskBtnId);
        if (!btn) return;
        btn.addEventListener('click', function () {
            if (!document.fullscreenElement) document.documentElement.requestFullscreen().catch(function () {});
            else document.exitFullscreen();
        });
        document.addEventListener('fullscreenchange', function () {
            var isFullscreen = !!document.fullscreenElement;
            var nav = document.getElementById(navElId);
            var toolbar = document.getElementById(toolbarElId);
            // The kiosk button lives in the toolbar being hidden here — the browser's own Esc
            // shortcut is what exits fullscreen while it's gone, same as any other fullscreen page.
            if (nav) nav.style.display = isFullscreen ? 'none' : '';
            if (toolbar) toolbar.style.display = isFullscreen ? 'none' : '';
        });
    }

    function wireTour(tourViewIds, tourIndex, tourIntervalSeconds) {
        if (!tourViewIds || tourViewIds.length < 2) return;
        var intervalMs = Math.max(3, tourIntervalSeconds || 15) * 1000;
        setTimeout(function () {
            var nextIndex = (tourIndex + 1) % tourViewIds.length;
            window.location.href = '/Views/Play/' + tourViewIds[nextIndex] + '?tour=true&i=' + nextIndex;
        }, intervalMs);
    }

    function init(o) {
        opts = o;
        var parsed = { cells: [], mobileTwoColumn: false };
        try {
            var p = JSON.parse(o.layoutJson);
            if (p && Array.isArray(p.cells)) parsed = p;
        } catch (e) { /* corrupted layout — render empty rather than fail the page */ }

        cells = parsed.cells;
        mobileTwoColumn = !!parsed.mobileTwoColumn;

        cameraById = {};
        (o.cameras || []).forEach(function (c) { cameraById[c.id] = c; });

        render();

        var onModeChange = function () { render(); };
        if (phoneQuery.addEventListener) phoneQuery.addEventListener('change', onModeChange);
        else if (phoneQuery.addListener) phoneQuery.addListener(onModeChange); // Safari < 14

        wireKiosk(o.kioskBtnId, o.navElId, o.toolbarElId);
        if (o.isTour) wireTour(o.tourViewIds, o.tourIndex, o.tourIntervalSeconds);
    }

    return { init: init };
})();

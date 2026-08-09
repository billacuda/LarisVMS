// M6 view editor: GridStack drag/resize for arranging cells, drag-from-palette (via
// GridStack.setupDragIn) or click-to-add as a fallback, per-cell aspect ratio and "hide on phone"
// flag, live video per cell via the same nidusvmsLiveView player Pages/Live uses. Saving re-reads
// GridStack's own geometry (grid.save) rather than tracking positions separately, so dragging/
// resizing never has to be mirrored into a second copy of the layout state.
window.nidusvmsViewEditor = (function () {
    'use strict';

    var grid = null;
    var cameraById = {};
    var stopFns = {}; // cellId -> live-view stop()

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function newCellId() {
        return 'cell-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8);
    }

    function buildCellHtml(cell) {
        var cam = cameraById[cell.cameraId];
        var camName = cam ? cam.name : 'Unknown camera';
        var aspect = window.NidusVMSAspectRatio.isValid(cell.aspect) ? cell.aspect : window.NidusVMSAspectRatio.default;
        var options = window.NidusVMSAspectRatio.options.map(function (a) {
            return '<option value="' + a + '"' + (a === aspect ? ' selected' : '') + '>' + a + '</option>';
        }).join('');
        return (
            '<div class="view-cell h-100 d-flex flex-column" data-cell-id="' + cell.id + '" data-camera-id="' + cell.cameraId + '">' +
                '<div class="view-cell-titlebar d-flex align-items-center gap-1 px-1 bg-dark text-white small" style="cursor: move;">' +
                    '<span class="flex-grow-1 text-truncate">' + escHtml(camName) + '</span>' +
                    '<select class="form-select form-select-sm view-cell-aspect" title="Aspect ratio" ' +
                        'style="width: auto; padding: 0 1.2rem 0 .3rem; font-size: .7rem;">' + options + '</select>' +
                    '<label class="mb-0" title="Hide on phone" style="cursor: pointer;">' +
                        '<input type="checkbox" class="view-cell-hide-mobile d-none"' + (cell.hideOnPhone ? ' checked' : '') + ' />' +
                        '<span class="view-cell-hide-mobile-icon">' + (cell.hideOnPhone ? '📵' : '📱') + '</span>' +
                    '</label>' +
                    '<button type="button" class="btn-close btn-close-white view-cell-remove" style="font-size: .6rem;" title="Remove"></button>' +
                '</div>' +
                '<div class="position-relative flex-grow-1 bg-black overflow-hidden" style="min-height: 0;">' +
                    '<video class="view-cell-video" style="width:100%; height:100%; object-fit:contain;" muted playsinline></video>' +
                    '<div class="position-absolute top-50 start-50 translate-middle text-white small text-center px-2 view-cell-status"></div>' +
                '</div>' +
            '</div>'
        );
    }

    function startCellVideo(cellEl) {
        var cellId = cellEl.dataset.cellId;
        var cameraId = cellEl.dataset.cameraId;
        var cam = cameraById[cameraId];
        var video = cellEl.querySelector('.view-cell-video');
        var status = cellEl.querySelector('.view-cell-status');
        if (!video || !cam) return;
        stopFns[cellId] = window.nidusvmsLiveView.start(cameraId, video, status, cam.codec, cam.hasAudio);
    }

    function stopCellVideo(cellId) {
        if (stopFns[cellId]) { stopFns[cellId](); delete stopFns[cellId]; }
    }

    function wireCellControls(itemEl) {
        var aspectSelect = itemEl.querySelector('.view-cell-aspect');
        if (aspectSelect) aspectSelect.addEventListener('click', function (e) { e.stopPropagation(); });

        var hideCheck = itemEl.querySelector('.view-cell-hide-mobile');
        var hideLabel = hideCheck && hideCheck.closest('label');
        var hideIcon = itemEl.querySelector('.view-cell-hide-mobile-icon');
        if (hideLabel) hideLabel.addEventListener('click', function (e) { e.stopPropagation(); });
        if (hideCheck && hideIcon) {
            hideCheck.addEventListener('change', function () {
                hideIcon.textContent = hideCheck.checked ? '📵' : '📱';
            });
        }

        var removeBtn = itemEl.querySelector('.view-cell-remove');
        if (removeBtn) {
            removeBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                var gsItem = itemEl.closest('.grid-stack-item');
                var cellId = itemEl.dataset.cellId;
                stopCellVideo(cellId);
                if (gsItem && grid) grid.removeWidget(gsItem);
            });
        }
    }

    // Finishes turning a grid item into a live camera tile: fills its content, wires the
    // titlebar controls, and starts video. Shared by every path that produces a cell (initial
    // load, click-to-add, and a successful palette drag-in) so they can't drift apart.
    function fillCell(contentEl, cell) {
        contentEl.innerHTML = buildCellHtml(cell);
        var cellEl = contentEl.querySelector('.view-cell');
        if (cellEl) {
            wireCellControls(cellEl);
            startCellVideo(cellEl);
        }
    }

    function placeCell(widgetOptions, cell) {
        var el = grid.addWidget(widgetOptions);
        if (!el) return;
        var contentEl = el.querySelector('.grid-stack-item-content');
        if (contentEl) fillCell(contentEl, cell);
    }

    function computeInitialHeight(aspect, w) {
        var container = document.getElementById('viewGrid');
        var totalWidth = (container && container.clientWidth) || 900;
        var pixelWidth = (totalWidth / 12) * w;
        var pixelHeight = pixelWidth / window.NidusVMSAspectRatio.ratio(aspect);
        return Math.max(1, Math.round(pixelHeight / 60)); // 60 = cellHeight passed to GridStack.init
    }

    function addCameraFromPalette(cameraId) {
        var aspect = window.NidusVMSAspectRatio.default;
        var w = 4;
        var cell = { id: newCellId(), aspect: aspect, cameraId: cameraId, hideOnPhone: false };
        // x/y intentionally omitted so GridStack auto-places this in the first free slot.
        placeCell({ id: cell.id, w: w, h: computeInitialHeight(aspect, w) }, cell);
    }

    // GridStack.setupDragIn clones a recognized external source (.grid-stack-item /
    // .grid-stack-item-content) into the grid on drop and fires 'added' for it same as any other
    // new widget — including the ones this file itself adds via placeCell, which already have
    // their real .view-cell content in place by the time this listener runs (grid.addWidget()
    // fires 'added' synchronously, before placeCell's fillCell() call after it returns). So a
    // dropped item is recognized here by what it *doesn't* have yet: a .view-cell wrapper, but a
    // bare cloned palette entry with the camera's data-camera-id still on it.
    function onGridItemAdded(event, items) {
        (items || []).forEach(function (item) {
            var el = item.el;
            if (!el || el.querySelector('.view-cell')) return;
            var contentEl = el.querySelector('.grid-stack-item-content');
            var cameraId = contentEl && contentEl.dataset && contentEl.dataset.cameraId;
            if (!contentEl || !cameraId) return;

            var cell = {
                id: (item.id && String(item.id)) || newCellId(),
                aspect: window.NidusVMSAspectRatio.default,
                cameraId: cameraId,
                hideOnPhone: false
            };
            fillCell(contentEl, cell);
        });
    }

    function serializeLayout(mobileTwoColumn) {
        var nodes = grid.save(false) || [];
        var cells = nodes.map(function (n) {
            var cellEl = document.querySelector('.view-cell[data-cell-id="' + n.id + '"]');
            var aspectSelect = cellEl && cellEl.querySelector('.view-cell-aspect');
            var hideCheck = cellEl && cellEl.querySelector('.view-cell-hide-mobile');
            return {
                id: n.id, x: n.x, y: n.y, w: n.w, h: n.h,
                aspect: (aspectSelect && aspectSelect.value) || window.NidusVMSAspectRatio.default,
                cameraId: cellEl && cellEl.dataset.cameraId,
                hideOnPhone: !!(hideCheck && hideCheck.checked)
            };
        }).filter(function (c) { return !!c.cameraId; });
        return JSON.stringify({ cells: cells, mobileTwoColumn: !!mobileTwoColumn });
    }

    function init(opts) {
        var initial = { cells: [], mobileTwoColumn: false };
        try {
            var parsed = JSON.parse(opts.layoutJson);
            if (parsed && Array.isArray(parsed.cells)) initial = parsed;
        } catch (e) { /* fresh view, or corrupted JSON — start empty rather than fail the page */ }

        cameraById = {};
        (opts.cameras || []).forEach(function (c) { cameraById[c.id] = c; });

        grid = GridStack.init({
            column: 12,
            cellHeight: 60,
            margin: 6,
            minH: 1,
            handle: '.view-cell-titlebar'
        }, '#viewGrid');

        grid.on('added', onGridItemAdded);

        initial.cells.forEach(function (cell) {
            // A camera removed/disabled/unassigned since this view was last saved is dropped
            // silently rather than showing a dead tile — the palette makes re-adding it one click
            // (or one drag) if the camera becomes available again.
            if (!cameraById[cell.cameraId]) return;
            placeCell({ id: cell.id, x: cell.x, y: cell.y, w: cell.w, h: cell.h }, cell);
        });

        var mobileCheck = document.getElementById(opts.mobileTwoColumnCheckId);
        if (mobileCheck) mobileCheck.checked = !!initial.mobileTwoColumn;

        // Drag straight from the palette onto the grid. GridStack recognizes a source element by
        // its .grid-stack-item/.grid-stack-item-content structure (see Editor.cshtml's palette
        // markup); onGridItemAdded above turns the resulting drop into a real camera tile.
        if (typeof GridStack.setupDragIn === 'function') {
            GridStack.setupDragIn('.camera-palette-item', { appendTo: 'body', helper: 'clone' });
        }

        // Click-to-add fallback — same result as a drag, lands in the first free slot instead of
        // wherever the pointer was released.
        var palette = document.getElementById('cameraPalette');
        if (palette) {
            palette.addEventListener('click', function (e) {
                var contentEl = e.target.closest('.grid-stack-item-content[data-camera-id]');
                if (contentEl) addCameraFromPalette(contentEl.getAttribute('data-camera-id'));
            });
        }

        var form = document.getElementById(opts.formId);
        var layoutInput = document.getElementById(opts.layoutInputId);
        if (form && layoutInput) {
            form.addEventListener('submit', function () {
                layoutInput.value = serializeLayout(mobileCheck && mobileCheck.checked);
            });
        }
    }

    return { init: init };
})();

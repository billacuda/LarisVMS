// Buttons that do what dragging does on a GridStack item: move it one step, or change its size. These
// are the keyboard way to arrange a grid, and the way for anyone who can't drag. GridStack raises its
// usual 'change' event, so a move saves the same way a drag does.
window.larisvmsGridKeyboard = (function () {
    'use strict';

    var BUTTONS = [
        ['left', '←', 'js.gridKeyboard.moveLeft'],
        ['up', '↑', 'js.gridKeyboard.moveUp'],
        ['down', '↓', 'js.gridKeyboard.moveDown'],
        ['right', '→', 'js.gridKeyboard.moveRight'],
        ['narrower', '↔−', 'js.gridKeyboard.narrower'],
        ['wider', '↔+', 'js.gridKeyboard.wider'],
        ['shorter', '↕−', 'js.gridKeyboard.shorter'],
        ['taller', '↕+', 'js.gridKeyboard.taller']
    ];

    function html(itemName) {
        return '<div class="lv-grid-toolbar" role="group" aria-label="' + larisvmsI18n.html('js.gridKeyboard.arrange', { item: itemName }) + '">' +
            BUTTONS.map(function (b) {
                var label = larisvmsI18n.html(b[2]);
                return '<button type="button" class="btn btn-sm btn-outline-secondary" data-grid-action="' + b[0] + '"' +
                    ' title="' + label + '" aria-label="' + label + '"><span aria-hidden="true">' + b[1] + '</span></button>';
            }).join('') +
            '</div>';
    }

    function wire(grid, itemEl) {
        itemEl.querySelectorAll('[data-grid-action]').forEach(function (button) {
            button.addEventListener('click', function (e) {
                e.stopPropagation();
                var node = itemEl.gridstackNode;
                if (!node) return;
                var change = {
                    left: { x: Math.max(0, node.x - 1) },
                    right: { x: node.x + 1 },
                    up: { y: Math.max(0, node.y - 1) },
                    down: { y: node.y + 1 },
                    narrower: { w: Math.max(1, node.w - 1) },
                    wider: { w: node.w + 1 },
                    shorter: { h: Math.max(1, node.h - 1) },
                    taller: { h: node.h + 1 }
                }[button.getAttribute('data-grid-action')];
                grid.update(itemEl, change);
            });
        });
    }

    return { html: html, wire: wire };
})();

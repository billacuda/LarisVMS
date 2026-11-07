// Keyboard drawing for the canvas editors (zones, privacy masks, speed traps, redaction). Arrow keys
// move a visible cursor, and the keys below send the editor the same mouse and pointer events a real
// mouse would, at that cursor. That way each editor has one set of drawing rules, not two.
//
//   Arrow keys   move the cursor (Shift for bigger steps)
//   Space        click at the cursor
//   Enter        double-click at the cursor, which finishes a shape
//   Backspace    right-click, which removes the last point
window.larisvmsCanvasKeyboard = (function () {
    'use strict';

    var SMALL_STEP = 0.01;
    var LARGE_STEP = 0.05;
    var nextId = 0;

    /// options.label names the canvas. options.step, when given, returns the fraction of the canvas
    /// one arrow press moves, for an editor that works on a grid.
    function wire(canvas, options) {
        if (!canvas || canvas.dataset.keyboardWired) return;
        canvas.dataset.keyboardWired = 'true';
        options = options || {};

        var host = canvas.parentElement;
        var cursor = document.createElement('div');
        cursor.className = 'lv-canvas-cursor d-none';
        cursor.setAttribute('aria-hidden', 'true');
        host.appendChild(cursor);

        var help = document.createElement('div');
        help.id = 'canvasKeyboardHelp' + (++nextId);
        help.className = 'small text-muted mt-1';
        help.textContent = larisvmsI18n.t('js.canvasKeyboard.help');
        host.insertAdjacentElement('afterend', help);

        canvas.tabIndex = 0;
        canvas.setAttribute('role', 'application');
        canvas.setAttribute('aria-label', options.label || larisvmsI18n.t('js.canvasKeyboard.drawingArea'));
        canvas.setAttribute('aria-describedby', help.id);

        var x = 0.5, y = 0.5;

        function point() {
            var rect = canvas.getBoundingClientRect();
            return { clientX: rect.left + x * rect.width, clientY: rect.top + y * rect.height };
        }

        function send(Type, name, button) {
            var at = point();
            canvas.dispatchEvent(new Type(name, {
                bubbles: true, cancelable: true, clientX: at.clientX, clientY: at.clientY, button: button || 0,
                pointerId: 1, pointerType: 'mouse', isPrimary: true
            }));
        }

        function placeCursor() {
            var canvasRect = canvas.getBoundingClientRect();
            var hostRect = host.getBoundingClientRect();
            cursor.style.left = (canvasRect.left - hostRect.left + x * canvasRect.width) + 'px';
            cursor.style.top = (canvasRect.top - hostRect.top + y * canvasRect.height) + 'px';
        }

        function click() {
            send(PointerEvent, 'pointerdown');
            send(MouseEvent, 'mousedown');
            send(PointerEvent, 'pointerup');
            send(MouseEvent, 'mouseup');
            send(MouseEvent, 'click');
        }

        canvas.addEventListener('focus', function () {
            cursor.classList.remove('d-none');
            placeCursor();
        });
        canvas.addEventListener('blur', function () { cursor.classList.add('d-none'); });

        canvas.addEventListener('keydown', function (e) {
            var step = e.shiftKey ? LARGE_STEP : (options.step ? options.step() : SMALL_STEP);
            var moved = true;
            switch (e.key) {
                case 'ArrowLeft': x = Math.max(0, x - step); break;
                case 'ArrowRight': x = Math.min(1, x + step); break;
                case 'ArrowUp': y = Math.max(0, y - step); break;
                case 'ArrowDown': y = Math.min(1, y + step); break;
                default: moved = false;
            }
            if (moved) {
                e.preventDefault();
                placeCursor();
                send(PointerEvent, 'pointermove');
                send(MouseEvent, 'mousemove');
            } else if (e.key === ' ') {
                e.preventDefault();
                click();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                send(MouseEvent, 'dblclick');
            } else if (e.key === 'Backspace') {
                e.preventDefault();
                send(MouseEvent, 'contextmenu', 2);
            }
        });
    }

    return { wire: wire };
})();

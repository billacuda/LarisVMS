// Scene heatmap (Activity → Scene): a translucent heat layer over a camera's picture. The server sends a
// small grid of 0-1 values (GET /api/cameras/{id}/scene-heat); it is coloured cell by cell, scaled up
// to the picture with smoothing and blurred, so the heat blends like a thermal image instead of
// showing blocks. Blue is very little, then green, yellow, orange and red for the most; a cell with
// nothing stays clear so the scene shows through.
(function () {
    'use strict';

    // Colour stops along the 0-1 scale.
    var STOPS = [
        [0.00, [40, 90, 255]],
        [0.30, [0, 200, 80]],
        [0.55, [255, 225, 0]],
        [0.78, [255, 140, 0]],
        [1.00, [230, 25, 25]]
    ];

    function colorAt(t) {
        for (var i = 1; i < STOPS.length; i++) {
            if (t <= STOPS[i][0]) {
                var a = STOPS[i - 1], b = STOPS[i];
                var f = (t - a[0]) / (b[0] - a[0]);
                return [0, 1, 2].map(function (k) { return Math.round(a[1][k] + (b[1][k] - a[1][k]) * f); });
            }
        }
        return STOPS[STOPS.length - 1][1];
    }

    // A log-like curve, so one very busy path doesn't leave everything else looking empty.
    function scale(v) { return Math.log(1 + 19 * v) / Math.log(20); }

    // Room left for the chart: from its top down to the bottom of the window, less the legend and
    // the card's padding under it — so the page fits on screen like a live view, without scrolling.
    var BELOW = 72, MIN_HEIGHT = 200;
    function availableHeight(el) {
        var top = el.getBoundingClientRect().top + window.scrollY;
        return Math.max(MIN_HEIGHT, window.innerHeight - top - BELOW);
    }

    function pictureShape(img, data) {
        return img.naturalWidth && img.naturalHeight ? img.naturalWidth / img.naturalHeight : data.cols / data.rows;
    }

    // Framed (a view's grid): the picture fitted inside the frame at its own shape, black bars
    // around it, so a portrait camera is no taller than the rest. Single camera: as wide as the
    // page allows, but never taller than the room left on screen.
    function fit(frame, img, data, framed) {
        var shape = pictureShape(img, data);
        var fw = framed ? frame.clientWidth : frame.parentElement.clientWidth;
        var fh = framed ? frame.clientHeight : availableHeight(frame);
        var w = fw, h = fw / shape;
        if (h > fh) { h = fh; w = fh * shape; }
        img.style.width = Math.round(w) + 'px';
        img.style.height = Math.round(h) + 'px';
        if (!framed) frame.style.width = Math.round(w) + 'px';
    }

    /// Sizes a view's grid (.scene-heatmap-grid of .scene-heatmap-panel) so every camera fits on
    /// screen at once: the column count that gives the largest 16:9 frames, like a live view's
    /// layout. Re-applied on resize; call after reset() and before rendering the panels.
    function layoutGrid(grid) {
        var layout = function () {
            var panels = grid.querySelectorAll('.scene-heatmap-panel');
            var n = panels.length;
            if (n === 0) return;
            var gap = parseFloat(getComputedStyle(grid).columnGap) || 16;
            var label = panels[0].firstElementChild ? panels[0].firstElementChild.offsetHeight + 4 : 24;
            var width = grid.parentElement.clientWidth;
            var height = availableHeight(grid);
            var best = 0, bestCols = 1;
            for (var cols = 1; cols <= n; cols++) {
                var rows = Math.ceil(n / cols);
                var frameW = (width - gap * (cols - 1)) / cols;
                var frameH = Math.min(frameW * 9 / 16, (height - gap * (rows - 1)) / rows - label);
                if (frameH > best) { best = frameH; bestCols = cols; }
            }
            var cellW = Math.floor(Math.max(120, best) * 16 / 9);
            grid.style.gridTemplateColumns = 'repeat(' + bestCols + ', ' + cellW + 'px)';
        };
        layout();
        redraws.push(layout);
    }

    function draw(canvas, img, data) {
        var w = img.clientWidth || img.parentElement.clientWidth;
        var h = img.clientHeight || Math.round(w * data.rows / data.cols);
        canvas.width = w;
        canvas.height = h;
        canvas.style.width = w + 'px';
        canvas.style.height = h + 'px';
        var ctx = canvas.getContext('2d');
        ctx.clearRect(0, 0, w, h);

        // The grid as a tiny image: one pixel per cell.
        var small = document.createElement('canvas');
        small.width = data.cols;
        small.height = data.rows;
        var sctx = small.getContext('2d');
        var pixels = sctx.createImageData(data.cols, data.rows);
        for (var i = 0; i < data.cells.length; i++) {
            var v = data.cells[i];
            if (!v) continue;
            var t = scale(v);
            var rgb = colorAt(t);
            pixels.data[i * 4] = rgb[0];
            pixels.data[i * 4 + 1] = rgb[1];
            pixels.data[i * 4 + 2] = rgb[2];
            // The quietest spots a little fainter, so the busy ones stand out.
            pixels.data[i * 4 + 3] = Math.round(255 * (0.55 + 0.35 * t));
        }
        sctx.putImageData(pixels, 0, 0);

        // Scaled up smoothly, then blurred by about half a cell so neighbouring cells blend.
        var cell = Math.max(w / data.cols, h / data.rows);
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = 'high';
        ctx.filter = 'blur(' + Math.max(2, Math.round(cell * 0.6)) + 'px)';
        ctx.globalAlpha = 0.6;
        ctx.drawImage(small, 0, 0, w, h);
        ctx.filter = 'none';
        ctx.globalAlpha = 1;
    }

    /// Renders into container: the camera's picture with the heat on top. options.framed fits it
    /// into a fixed-shape black frame instead of the container's full width.
    function render(container, data, imageUrl, options) {
        var framed = !!(options && options.framed);
        var inner = '<img class="scene-heatmap-image" alt="" />' +
            '<canvas class="scene-heatmap-heat" aria-hidden="true"></canvas>';
        container.innerHTML = framed
            ? '<div class="scene-heatmap scene-heatmap--framed"><div class="scene-heatmap-fit">' + inner + '</div></div>'
            : '<div class="scene-heatmap">' + inner + '</div>';
        var img = container.querySelector('img');
        var canvas = container.querySelector('canvas');
        var frame = container.querySelector('.scene-heatmap');
        var redraw = function () {
            fit(frame, img, data, framed);
            draw(canvas, img, data);
        };
        img.addEventListener('load', redraw);
        // No picture to show: a plain panel of the camera's shape, so the heat still reads.
        img.addEventListener('error', function () {
            img.style.aspectRatio = data.cols + ' / ' + data.rows;
            img.classList.add('scene-heatmap-image--missing');
            redraw();
        });
        img.src = imageUrl;
        redraws.push(redraw);
    }

    // Every heatmap on the page follows the window's size; reset() before drawing a new set.
    var redraws = [];
    window.addEventListener('resize', function () { redraws.forEach(function (r) { r(); }); });
    function reset() { redraws = []; }

    /// A legend bar: the colour scale from "Less" to "More".
    function legend(maxText) {
        var stops = STOPS.map(function (s) { return 'rgb(' + s[1].join(',') + ') ' + Math.round(s[0] * 100) + '%'; }).join(', ');
        return '<span class="small text-muted">Less</span>' +
            '<span class="scene-heatmap-legend" style="background: linear-gradient(to right, ' + stops + ');"></span>' +
            '<span class="small text-muted">More' + (maxText ? ' (' + maxText + ')' : '') + '</span>';
    }

    window.larisvmsSceneHeatmap = { render: render, legend: legend, reset: reset, layoutGrid: layoutGrid };
})();

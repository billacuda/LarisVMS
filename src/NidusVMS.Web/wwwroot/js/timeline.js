// M7 canvas coverage timeline — tape-scrubber model: the yellow playhead marker is always drawn
// at the horizontal center of the canvas and never moves; dragging slides the timeline strip
// beneath it instead, so whatever time is under the marker after a drag *is* the play position,
// continuously updated as you drag (not just a pan that leaves the play position wherever it was).
// Wheel zooms in/out around that same fixed center point (weeks down to seconds) rather than the
// cursor, so zooming never jumps the marker off-center. The playhead can never be dragged past
// "now"; the window may still extend past it, and that stretch is drawn as unreachable future.
// Renders
// recorded/gap buckets fetched from GET /api/cameras/{id}/timeline — no motion coloring yet, that's
// M8 (MotionSpans doesn't exist). Framework-free canvas drawing, matching the rest of this app's
// no-build-step JS.
window.nidusvmsTimeline = (function () {
    'use strict';

    var MIN_RANGE_MS = 5 * 1000;
    var MAX_RANGE_MS = 90 * 24 * 3600 * 1000;

    function create(canvas, options) {
        var ctx = canvas.getContext('2d');
        var rangeMs = options.initialRangeMs || (24 * 3600 * 1000);
        var centerMs = clampCenter(options.initialCenterMs || Date.now());
        var buckets = [];
        var loading = false;
        var reloadTimer = null;
        var hour24 = !!options.hour24;

        // It's the *playhead* that can't pass "now", not the visible window. An earlier version
        // clamped the window's right edge instead (centerMs <= now - range/2), which quietly pinned
        // the marker half a span into the past: at the 24h default the marker sat at now-12h with
        // twelve hours of real, still-being-recorded footage to its right, which reads as "there's
        // recorded video in the future". The window is allowed to extend past now; that region is
        // drawn as unreachable-future instead (see draw()), so "now" stays where the marker is.
        function clampCenter(ms) {
            return Math.min(ms, Date.now());
        }

        function formatLabel(ms) {
            return new Date(ms).toLocaleString(undefined, hour24 ? {
                year: 'numeric', month: 'numeric', day: 'numeric',
                hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
            } : undefined);
        }

        function visibleRange() {
            return { from: centerMs - rangeMs / 2, to: centerMs + rangeMs / 2 };
        }

        function resizeCanvas() {
            var rect = canvas.getBoundingClientRect();
            var dpr = window.devicePixelRatio || 1;
            canvas.width = Math.max(1, Math.round(rect.width * dpr));
            canvas.height = Math.max(1, Math.round(rect.height * dpr));
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        }

        function scheduleReload() {
            clearTimeout(reloadTimer);
            reloadTimer = setTimeout(reload, 150);
        }

        async function reload() {
            if (loading || !options.getBuckets) return;
            loading = true;
            var r = visibleRange();
            var bucketCount = Math.max(50, Math.min(2000, Math.round(canvas.clientWidth || 800)));
            try {
                buckets = await options.getBuckets(new Date(r.from).toISOString(), new Date(r.to).toISOString(), bucketCount);
            } catch (e) {
                buckets = [];
            }
            loading = false;
            draw();
        }

        function draw() {
            var w = canvas.clientWidth, h = canvas.clientHeight;
            if (!w || !h) return;
            ctx.clearRect(0, 0, w, h);
            ctx.fillStyle = '#2b2b2b';
            ctx.fillRect(0, 0, w, h);

            var r = visibleRange();
            var span = r.to - r.from;
            if (span <= 0) return;

            // A bucket that straddles "now" is truncated at it rather than drawn whole: an actively
            // recording camera's newest bucket legitimately covers the current instant, and drawing
            // its full width would paint coverage over time that hasn't happened yet.
            var nowMs = Date.now();
            var nowX = ((nowMs - r.from) / span) * w;

            buckets.forEach(function (b) {
                var bStart = new Date(b.startUtc).getTime();
                var bEnd = Math.min(new Date(b.endUtc).getTime(), nowMs);
                if (bEnd <= bStart) return;
                var x1 = ((bStart - r.from) / span) * w;
                var x2 = ((bEnd - r.from) / span) * w;
                ctx.fillStyle = b.hasRecording ? '#1e6fd9' : 'rgba(255,255,255,0.08)';
                ctx.fillRect(x1, 4, Math.max(1, x2 - x1), h - 8);
            });

            // Everything right of "now" is time that hasn't happened yet — flatly darker than the
            // empty-track color so it reads as "nothing can ever be here", not "nothing recorded
            // here yet". Drawn over the buckets but under the playhead marker.
            if (nowX < w) {
                ctx.fillStyle = '#141414';
                ctx.fillRect(Math.max(0, nowX), 0, w - Math.max(0, nowX), h);
                if (nowX > 0) {
                    ctx.strokeStyle = 'rgba(255,255,255,0.25)';
                    ctx.lineWidth = 1;
                    ctx.beginPath();
                    ctx.moveTo(nowX + 0.5, 0);
                    ctx.lineTo(nowX + 0.5, h);
                    ctx.stroke();
                }
            }

            // The playhead never moves from dead center — the strip above moves under it instead.
            var centerPx = w / 2;
            ctx.strokeStyle = '#ffc107';
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.moveTo(centerPx, 0);
            ctx.lineTo(centerPx, h);
            ctx.stroke();

            ctx.fillStyle = '#ccc';
            ctx.font = '11px sans-serif';
            ctx.textBaseline = 'top';
            ctx.textAlign = 'left';
            ctx.fillText(formatLabel(r.from), 4, 2);
            ctx.textAlign = 'right';
            ctx.fillText(formatLabel(r.to), w - 4, 2);
            ctx.textAlign = 'left';
        }

        function xToTime(clientX) {
            var rect = canvas.getBoundingClientRect();
            var frac = (clientX - rect.left) / rect.width;
            var r = visibleRange();
            return r.from + frac * (r.to - r.from);
        }

        // Zooms around centerMs (the playhead), never around the cursor — the marker's job is to
        // never move, so the zoom anchor has to be the same fixed point it's drawn at.
        canvas.addEventListener('wheel', function (e) {
            e.preventDefault();
            var zoomFactor = e.deltaY < 0 ? 0.8 : 1.25; // wheel up = zoom in
            rangeMs = Math.min(MAX_RANGE_MS, Math.max(MIN_RANGE_MS, rangeMs * zoomFactor));
            draw();
            scheduleReload();
            if (options.onRangeChange) options.onRangeChange(rangeMs);
            // No onScrub here on purpose: the playhead's clamp doesn't depend on the zoom level, so
            // zooming can't move the play position and must not trigger a seek per scroll notch.
        }, { passive: false });

        var dragging = false, dragStartX = 0, dragStartCenter = 0, dragMoved = false;
        canvas.addEventListener('mousedown', function (e) {
            if (e.button !== 0) return;
            dragging = true;
            dragMoved = false;
            dragStartX = e.clientX;
            dragStartCenter = centerMs;
            canvas.style.cursor = 'grabbing';
        });
        // Live-scrub while dragging is throttled, not fired on every raw mousemove — a browser
        // dispatches those at a much higher rate than any seek pipeline can usefully keep up with,
        // and un-throttled it fired 50-100+ times over a single half-second drag. With several
        // cameras in a view each seek fans out to one fetch per tile, so that flooded the browser's
        // connection pool (confirmed live: net::ERR_INSUFFICIENT_RESOURCES) and made the recorder
        // node's storage reads look "stuck" — they weren't hung, they were queued behind hundreds
        // of already-abandoned requests. ~8/sec is frequent enough to feel live without flooding.
        var SCRUB_THROTTLE_MS = 120;
        var lastScrubFiredAt = 0;

        window.addEventListener('mousemove', function (e) {
            if (!dragging) return;
            var rect = canvas.getBoundingClientRect();
            var dxFrac = (e.clientX - dragStartX) / rect.width;
            if (Math.abs(e.clientX - dragStartX) > 3) dragMoved = true;
            // Dragging the strip right (positive dx) reveals earlier content at center — same
            // motion as sliding a physical filmstrip right under a fixed viewing window — so
            // centerMs (now == the playhead) moves backward in time, not forward.
            centerMs = clampCenter(dragStartCenter - dxFrac * rangeMs);
            draw();
            var now = Date.now();
            if (options.onScrub && now - lastScrubFiredAt >= SCRUB_THROTTLE_MS) {
                lastScrubFiredAt = now;
                options.onScrub(Math.round(centerMs));
            }
        });
        window.addEventListener('mouseup', function (e) {
            if (!dragging) return;
            dragging = false;
            canvas.style.cursor = 'pointer';
            if (!dragMoved) {
                // A plain click: jump the playhead straight to the clicked point instead of
                // requiring a drag for a big seek.
                centerMs = clampCenter(xToTime(e.clientX));
                draw();
            }
            // Always fires once more here, throttle bypassed — the exact release position must be
            // committed even if it landed inside the last throttle window during a drag.
            if (options.onScrub) options.onScrub(Math.round(centerMs));
            scheduleReload();
        });

        window.addEventListener('resize', function () { resizeCanvas(); draw(); });

        resizeCanvas();
        reload();

        return {
            redraw: draw,
            // Recenters the strip on ms (e.g. following normal playback progress, or mirroring a
            // scrub that happened on a *sibling* timeline) without firing onScrub — the caller
            // already knows the video moved there (or is the one that caused it), this just keeps
            // this strip's visuals in sync with it. Still clamped — playback itself can never
            // legitimately be "in the future" either.
            setCenter: function (ms) { centerMs = clampCenter(ms); draw(); },
            getCenter: function () { return centerMs; },
            // Mirrors a zoom that happened on a sibling timeline — same no-callback-re-fire
            // reasoning as setCenter, so two synced timelines can't bounce a change back and forth.
            setRange: function (ms) {
                var clamped = Math.min(MAX_RANGE_MS, Math.max(MIN_RANGE_MS, ms));
                if (clamped === rangeMs) return;
                rangeMs = clamped;
                draw();
                scheduleReload();
            },
            getRange: function () { return rangeMs; },
            setHour24: function (on) { hour24 = !!on; draw(); },
            reload: reload
        };
    }

    return { create: create };
})();

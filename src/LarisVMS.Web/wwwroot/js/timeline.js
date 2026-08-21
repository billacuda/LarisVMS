// M7 canvas coverage timeline — tape-scrubber model: the yellow playhead marker is always drawn
// at the horizontal center of the canvas and never moves; dragging slides the timeline strip
// beneath it instead, so whatever time is under the marker after a drag *is* the play position,
// continuously updated as you drag (not just a pan that leaves the play position wherever it was).
// Wheel zooms in/out around that same fixed center point (weeks down to seconds) rather than the
// cursor, so zooming never jumps the marker off-center. The playhead can never be dragged past
// "now"; the window may still extend past it, and that stretch is drawn as unreachable future.
// Renders recorded/motion/gap buckets fetched from GET /api/cameras/{id}/timeline — green (motion)
// wins over blue (recorded) for a bucket with both, not a blend. Framework-free canvas drawing,
// matching the rest of this app's no-build-step JS.
window.larisvmsTimeline = (function () {
    'use strict';

    var MIN_RANGE_MS = 5 * 1000;
    var MAX_RANGE_MS = 90 * 24 * 3600 * 1000;

    // Admin-configurable via Admin → Event Colors. Seeded with the built-in defaults so the very
    // first paint (which happens before the fetch resolves) and any failure of that fetch both still
    // draw something sensible — these two values must stay in step with EventColors.Default* on the
    // server. Detected-object colors are NOT here: those arrive per-bucket as tagColorHex, already
    // resolved server-side, so the palette only covers what the canvas would otherwise hardcode.
    var palette = { motion: '#28e070', recording: '#1e6fd9' };
    var instances = [];

    fetch('/api/timeline/colors')
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (p) {
            if (!p) return;
            palette.motion = p.motion || palette.motion;
            palette.recording = p.recording || palette.recording;
            // Anything already on screen was painted with the defaults — repaint it now rather than
            // leaving the page inconsistent until the next reload or interaction.
            instances.forEach(function (redraw) { try { redraw(); } catch (e) {} });
        })
        .catch(function () { /* default palette stands */ });

    // "Nice" tick spacings, ascending — pickTickInterval walks these until the resulting tick count
    // for the current zoom fits the canvas width, so the axis always reads as a sensible scale
    // (seconds/minutes/hours/days) instead of some arbitrary fraction of the visible span.
    var NICE_INTERVALS_MS = [
        1000, 2000, 5000, 10000, 15000, 30000,
        60000, 2 * 60000, 5 * 60000, 10 * 60000, 15 * 60000, 30 * 60000,
        3600000, 2 * 3600000, 3 * 3600000, 6 * 3600000, 12 * 3600000,
        86400000, 2 * 86400000, 7 * 86400000, 14 * 86400000, 30 * 86400000, 90 * 86400000
    ];

    function create(canvas, options) {
        var ctx = canvas.getContext('2d');
        // Clamped like setRange does, not trusted raw: initialRangeMs now comes from a persisted user
        // preference (playback.position's rangeMs), and a corrupted or out-of-date stored value would
        // otherwise install a range the zoom controls themselves would never permit — leaving the
        // timeline stuck somewhere it can't be reached from, since every later change re-clamps.
        var rangeMs = Math.min(MAX_RANGE_MS,
            Math.max(MIN_RANGE_MS, options.initialRangeMs || (24 * 3600 * 1000)));
        var centerMs = clampCenter(options.initialCenterMs || Date.now());
        var buckets = [];
        var bookmarks = []; // M18: [{ timestampUtc }] — options.getBookmarks is optional, same as getThumbnailUrl
        var loading = false;
        var reloadTimer = null;
        var hour24 = !!options.hour24;
        // Defaults true so every existing caller (View cell/Live mini-timelines) keeps showing tag/
        // detection colors unchanged — only Pages/Playback passes this explicitly, per its own
        // per-user "Event tags" toggle (M16), off by default there specifically.
        var showEventTags = options.showEventTags !== false;

        // It's the *playhead* that can't pass "now", not the visible window. An earlier version
        // clamped the window's right edge instead (centerMs <= now - range/2), which quietly pinned
        // the marker half a span into the past: at the 24h default the marker sat at now-12h with
        // twelve hours of real, still-being-recorded footage to its right, which reads as "there's
        // recorded video in the future". The window is allowed to extend past now; that region is
        // drawn as unreachable-future instead (see draw()), so "now" stays where the marker is.
        function clampCenter(ms) {
            return Math.min(ms, Date.now());
        }

        // Axis tick granularity follows the chosen interval, not the total visible span — a 2-day
        // view with 6-hour ticks shows "3 PM" per tick, not a full date repeated at every mark; a
        // 90-day view with 1-week ticks shows just the date, since a time-of-day would be meaningless
        // noise at that scale. This is the "date, hours, minutes, seconds depending on zoom level"
        // scale cue, applied per tick rather than once for the whole bar.
        function formatTick(ms, intervalMs) {
            var d = new Date(ms);
            if (intervalMs >= 86400000) {
                return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
            }
            if (intervalMs >= 60000) {
                return d.toLocaleTimeString(undefined, hour24
                    ? { hour: '2-digit', minute: '2-digit', hour12: false }
                    : { hour: 'numeric', minute: '2-digit' });
            }
            return d.toLocaleTimeString(undefined, hour24
                ? { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false }
                : { hour: 'numeric', minute: '2-digit', second: '2-digit' });
        }

        // Local midnight at or before ms. Not `ms - (ms % 86400000)`, which would find UTC midnight
        // and land hours off in any zone but UTC — every other timestamp a user reads on this
        // timeline is rendered in their own local time (see the plan's storage-vs-display rule), so
        // the day boundaries drawn on it have to be local days too.
        function startOfLocalDay(ms) {
            var d = new Date(ms);
            d.setHours(0, 0, 0, 0);
            return d;
        }

        // Day boundaries, each with a date badge. Below day-level zoom the tick labels are
        // time-only, so a window that crosses midnight gives no indication the day changed, and one
        // sitting entirely inside a single day names no date at all — in both cases you can scroll
        // for a while with no idea which day you're looking at. Skipped at day+ zoom, where every
        // tick label is already a date and this would only duplicate them.
        function drawDayMarkers(r, span, w, h, barTop, tickInterval) {
            if (tickInterval >= 86400000) return;

            ctx.font = '9px sans-serif';
            ctx.textBaseline = 'top';
            ctx.textAlign = 'left';

            for (var cursor = startOfLocalDay(r.from); cursor.getTime() <= r.to;) {
                var dayStart = cursor.getTime();
                // Advance by calendar day, not by a fixed 86400000ms: a DST transition day is 23 or
                // 25 hours long, and fixed-millisecond stepping would walk every subsequent boundary
                // an hour off midnight for the rest of the window.
                var next = new Date(cursor);
                next.setDate(next.getDate() + 1);

                var startX = ((dayStart - r.from) / span) * w;
                var endX = ((next.getTime() - r.from) / span) * w;

                // Only draw the divider when the day actually begins inside the window — the
                // left-most day is normally already in progress when the window opens, and a line at
                // x<0 would just be clipped away anyway.
                if (startX > 0) {
                    ctx.strokeStyle = 'rgba(255,255,255,0.5)';
                    ctx.lineWidth = 1;
                    ctx.beginPath();
                    ctx.moveTo(Math.round(startX) + 0.5, 0);
                    ctx.lineTo(Math.round(startX) + 0.5, h);
                    ctx.stroke();
                }

                var label = new Date(dayStart).toLocaleDateString(undefined,
                    { weekday: 'short', month: 'short', day: 'numeric' });
                var labelWidth = ctx.measureText(label).width;
                var badgeX = Math.max(0, startX) + 3;
                var visibleWidth = Math.min(endX, w) - Math.max(startX, 0);

                // A day with only a sliver on screen can't hold its own name — a clipped or
                // overflowing badge reads as belonging to the neighbouring day, which is worse than
                // no badge at all.
                if (visibleWidth >= labelWidth + 10 && badgeX + labelWidth + 2 <= w) {
                    // Drawn over the coverage bar (the canvas is only ~30px tall — there's no spare
                    // row for this), so it needs its own backdrop to stay readable against green
                    // motion, blue recording, or any custom tag color underneath.
                    ctx.fillStyle = 'rgba(0,0,0,0.6)';
                    ctx.fillRect(badgeX - 2, barTop, labelWidth + 4, 11);
                    ctx.fillStyle = '#e8e8e8';
                    ctx.fillText(label, badgeX, barTop + 1);
                }

                cursor = next;
            }
        }

        // Smallest "nice" interval whose tick count still fits comfortably in the canvas width —
        // roughly one label per 90px, wide enough that adjacent labels never overlap even at the
        // longest ("Aug 10" + time) format.
        function pickTickInterval(spanMs, widthPx) {
            var maxTicks = Math.max(2, Math.floor(widthPx / 90));
            for (var i = 0; i < NICE_INTERVALS_MS.length; i++) {
                if (spanMs / NICE_INTERVALS_MS[i] <= maxTicks) return NICE_INTERVALS_MS[i];
            }
            return NICE_INTERVALS_MS[NICE_INTERVALS_MS.length - 1];
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

        // A reload requested while one is already in flight used to be silently dropped — confirmed
        // live as "the Playback page's timelines load with only blue (recorded) coverage, no
        // motion/event colors at all, until you zoom": create() fires its own reload() immediately for
        // whatever centerMs/rangeMs the timeline starts with (Date.now(), before the page glue has
        // resolved the real initial position — a deep link's instant, the persisted last-viewed
        // position, or "most recent recording"). The page glue's own correct reload() for the real
        // position, moments later once that resolution finishes, would arrive while the first one was
        // still awaiting its fetch and simply vanish — leaving the timeline stuck on "now"'s coverage
        // (real blue, since Continuous mode is always recording something, but often no motion) until
        // some unrelated later reload (a zoom's scheduleReload) finally got a clear run. Queuing the
        // in-flight case instead of dropping it means every requested reload eventually happens.
        var reloadPending = false;

        // The [from, to) actually covered by whatever's currently in `buckets` — set at the top of
        // reload() (the range that fetch was *for*, not whatever centerMs has drifted to by the time
        // the response lands) so setCenter below can tell whether the strip it's about to redraw is
        // still backed by real data or has drifted off the edge of it.
        var coveredRange = null;

        async function reload() {
            if (loading) { reloadPending = true; return; }
            if (!options.getBuckets) return;
            loading = true;
            var r = visibleRange();
            coveredRange = r;
            var bucketCount = Math.max(50, Math.min(2000, Math.round(canvas.clientWidth || 800)));
            var fromIso = new Date(r.from).toISOString(), toIso = new Date(r.to).toISOString();
            try {
                buckets = await options.getBuckets(fromIso, toIso, bucketCount);
            } catch (e) {
                buckets = [];
            }
            // M18: bookmark markers — only the per-camera timeline instance supplies this (a
            // bookmark belongs to one specific camera, same reasoning as getThumbnailUrl above being
            // per-camera-only). Fetched alongside buckets so both are current for the same range by
            // the time draw() runs.
            if (options.getBookmarks) {
                try {
                    bookmarks = await options.getBookmarks(fromIso, toIso);
                } catch (e) {
                    bookmarks = [];
                }
            }
            loading = false;
            draw();
            // One coalesced retry, not a queue — every reload fetches the timeline's *current*
            // visibleRange() at the moment it actually runs, so a reload requested and then
            // superseded by another before this one even starts still only needs the one retry to end
            // up showing the truly-latest range, same as scheduleReload's own debounce achieves for
            // rapid zoom/pan.
            if (reloadPending) {
                reloadPending = false;
                reload();
            }
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

            // Coverage bar occupies the top ~55% of a now-much-shorter canvas; tick marks and their
            // labels live in the remainder below it, instead of the old two-corner-label footer that
            // needed a much taller bar to have room for.
            var barTop = 1, barBottom = Math.max(barTop + 4, Math.round(h * 0.55));

            // A bucket that straddles "now" is truncated at it rather than drawn whole: an actively
            // recording camera's newest bucket legitimately covers the current instant, and drawing
            // its full width would paint coverage over time that hasn't happened yet.
            var nowMs = Date.now();
            var nowX = ((nowMs - r.from) / span) * w;

            // Adjacent buckets sharing the same color are merged into one fillRect run instead of
            // one call per bucket — at a wide zoom (up to ~2000 buckets, close to one per pixel) two
            // same-colored neighbors drawn separately could leave a hairline gap between them from
            // sub-pixel rounding (bucket N's x2 not landing on exactly the same float as bucket N+1's
            // x1), which read as thin flickering stripes through a solid run of coverage — worse
            // during playback, where the whole strip's pixel positions shift slightly on every
            // redraw (following playback), so those hairline gaps didn't just look striped, they
            // visibly pulsed as their positions jittered frame to frame. One rect per same-colored
            // run has no seams to flicker, and coordinates are rounded once per run rather than
            // independently per bucket.
            // runColors is always an array. A run of one color paints exactly as it always did; a run
            // carrying several (a camera seeing a person and a vehicle in the same instant) splits
            // the bar into equal horizontal bands so every class is visible rather than one winning
            // and the rest disappearing. Runs merge on the whole color list, not just the first
            // color, or two differently-banded neighbors would merge into whichever came first.
            var runStartX = null, runColors = null, runKey = null;
            function flushRun(endX) {
                if (runStartX === null) return;
                var x1 = Math.round(runStartX), x2 = Math.round(endX);
                var width = Math.max(1, x2 - x1);
                var top = barTop, total = barBottom - barTop;
                for (var i = 0; i < runColors.length; i++) {
                    // Last band absorbs the rounding remainder so the bands always fill the bar
                    // exactly, with no background hairline showing through at the bottom.
                    var bandTop = top + Math.round((total * i) / runColors.length);
                    var bandBottom = i === runColors.length - 1
                        ? top + total
                        : top + Math.round((total * (i + 1)) / runColors.length);
                    ctx.fillStyle = runColors[i];
                    ctx.fillRect(x1, bandTop, width, Math.max(1, bandBottom - bandTop));
                }
                runStartX = null;
            }
            buckets.forEach(function (b) {
                var bStart = new Date(b.startUtc).getTime();
                var bEnd = Math.min(new Date(b.endUtc).getTime(), nowMs);
                if (bEnd <= bStart) return;
                var x1 = ((bStart - r.from) / span) * w;
                var x2 = ((bEnd - r.from) / span) * w;
                // M8 pass 8: a custom EventTagRule's own color, and an object class's color, both win
                // outright over the built-in motion/recorded/gray scheme — see TimelineBucketDto's
                // doc comment. Empty (the default, and every bucket on a camera with no tag rules and
                // no object analytics) falls straight through to the unchanged built-in scheme.
                var colors = (showEventTags && b.tagColorHexes && b.tagColorHexes.length)
                    ? b.tagColorHexes
                    : (showEventTags && b.tagColorHex ? [b.tagColorHex]
                        : [b.hasMotion ? palette.motion : (b.hasRecording ? palette.recording : 'rgba(255,255,255,0.08)')]);
                var key = colors.join('|');
                if (runKey !== null && key !== runKey) flushRun(x1);
                if (runStartX === null) { runStartX = x1; runColors = colors; runKey = key; }
            });
            flushRun(w);

            // Everything right of "now" is time that hasn't happened yet — flatly darker than the
            // empty-track color so it reads as "nothing can ever be here", not "nothing recorded
            // here yet". Drawn over the buckets but under the playhead marker and tick labels.
            if (nowX < w) {
                ctx.fillStyle = '#141414';
                ctx.fillRect(Math.max(0, nowX), barTop, w - Math.max(0, nowX), barBottom - barTop);
                if (nowX > 0) {
                    ctx.strokeStyle = 'rgba(255,255,255,0.25)';
                    ctx.lineWidth = 1;
                    ctx.beginPath();
                    ctx.moveTo(nowX + 0.5, 0);
                    ctx.lineTo(nowX + 0.5, h);
                    ctx.stroke();
                }
            }

            // M18: bookmark markers — a small downward-pointing flag at the top edge of the bar for
            // each bookmarked instant in range, distinct in shape (not just color) from the
            // full-height playhead line so the two can never be confused even where they coincide.
            if (bookmarks.length) {
                ctx.fillStyle = '#ffca28';
                bookmarks.forEach(function (b) {
                    var bm = new Date(b.timestampUtc).getTime();
                    if (bm < r.from || bm > r.to) return;
                    var x = ((bm - r.from) / span) * w;
                    ctx.beginPath();
                    ctx.moveTo(x - 4, barTop);
                    ctx.lineTo(x + 4, barTop);
                    ctx.lineTo(x, barTop + 7);
                    ctx.closePath();
                    ctx.fill();
                });
            }

            // Tick marks + scale-appropriate labels (date/hours/minutes/seconds depending on zoom —
            // see formatTick/pickTickInterval) aligned to "nice" boundaries of the chosen interval,
            // not just evenly spaced across whatever the visible span happens to be, so the same
            // interval a user zooms into always lands on the same wall-clock marks (e.g. always the
            // top of the hour at hourly zoom) rather than drifting with the pan position.
            var tickInterval = pickTickInterval(span, w);
            var firstTick = Math.ceil(r.from / tickInterval) * tickInterval;
            ctx.fillStyle = '#aaa';
            ctx.font = '9px sans-serif';
            ctx.textBaseline = 'top';
            for (var t = firstTick; t <= r.to; t += tickInterval) {
                var x = ((t - r.from) / span) * w;
                ctx.strokeStyle = 'rgba(255,255,255,0.3)';
                ctx.lineWidth = 1;
                ctx.beginPath();
                ctx.moveTo(Math.round(x) + 0.5, barBottom);
                ctx.lineTo(Math.round(x) + 0.5, barBottom + 3);
                ctx.stroke();

                var label = formatTick(t, tickInterval);
                var labelWidth = ctx.measureText(label).width;
                // Skip a label that would clip off either edge rather than clamp its position —
                // a clamped label would visually detach from its own tick mark.
                if (x - labelWidth / 2 < 0 || x + labelWidth / 2 > w) continue;
                ctx.textAlign = 'center';
                ctx.fillText(label, x, barBottom + 4);
            }
            ctx.textAlign = 'left';

            // After the ticks so a day divider reads as the stronger boundary of the two, before the
            // playhead so the marker still sits on top of everything.
            drawDayMarkers(r, span, w, h, barTop, tickInterval);

            // The playhead never moves from dead center — the strip above moves under it instead.
            var centerPx = w / 2;
            ctx.strokeStyle = '#ffc107';
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.moveTo(centerPx, 0);
            ctx.lineTo(centerPx, h);
            ctx.stroke();
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

        // Pointer Events (not mouse events) specifically so setPointerCapture can pin move/up
        // delivery to this canvas for the duration of the drag — with plain mouse events, releasing
        // the button after the cursor has left the browser window (dragged off-screen, over the
        // taskbar, onto another monitor) never fires a mouseup the page can see at all, and the drag
        // is left permanently "stuck" to the cursor with no way to release it short of reloading.
        // Pointer capture keeps delivering pointermove/pointerup to `canvas` regardless of where the
        // pointer physically is, as long as the button is still down.
        var dragging = false, dragStartX = 0, dragStartCenter = 0, dragMoved = false, dragPointerId = null;

        // Two-finger pinch-to-zoom (phone/tablet — wheel above covers mouse/trackpad, which never
        // fires a second simultaneous pointerdown). Tracks every currently-down pointer by id so a
        // second touch landing mid-drag can cleanly take over from single-pointer panning instead of
        // corrupting its state (a naive pointerdown handler would otherwise treat the second finger's
        // own down/move as a continuation of the first finger's drag, since both share the same
        // dragStartX/dragStartCenter). Same anchor-on-centerMs philosophy as the wheel handler above,
        // not the touch midpoint — the playhead's job is to never move, so pinching zooms the same
        // fixed point wheel-zoom already does, it just reads a changing finger distance instead of a
        // scroll delta.
        var activePointers = {}; // pointerId -> {x, y}
        var pinching = false, pinchStartDist = 0, pinchStartRange = 0;

        function pointerDistance() {
            var ids = Object.keys(activePointers);
            if (ids.length < 2) return 0;
            var a = activePointers[ids[0]], b = activePointers[ids[1]];
            return Math.hypot(a.x - b.x, a.y - b.y);
        }

        canvas.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) return;
            // Without this, a mousedown-then-move over the canvas can concurrently kick off the
            // browser's own native "drag this element out" gesture — confirmed live as the drag
            // cursor turning into the no-drop/circle-slash icon and the custom pan below never
            // firing, exactly the same failure playback-player.js's wireZoom already documents and
            // fixes for <video> the same way. Canvas isn't natively draggable in every engine, which
            // is why this reproduced intermittently rather than every time.
            e.preventDefault();
            activePointers[e.pointerId] = { x: e.clientX, y: e.clientY };
            canvas.setPointerCapture(e.pointerId);

            var ids = Object.keys(activePointers);
            if (ids.length >= 2) {
                // A second (or third+, ignored below) finger just landed — hand off from panning to
                // pinching. dragging is cancelled outright rather than left running underneath: two
                // fingers means the gesture is a zoom, not a pan, even if the first finger started
                // one a moment ago. Resetting the cursor here too, not just dragging itself — the
                // first finger's own pointerdown may have already set it to 'grabbing', and neither
                // the pinch path nor (once pinching has already cleared itself) the second finger's
                // own pointerup ever routes back through the single-pointer cleanup that would
                // otherwise reset it — confirmed live as a cursor stuck reading 'grabbing' after any
                // gesture that started as a single-finger drag and then became a pinch.
                dragging = false;
                canvas.style.cursor = 'pointer';
                if (ids.length === 2) {
                    pinching = true;
                    pinchStartDist = pointerDistance();
                    pinchStartRange = rangeMs;
                }
                return;
            }

            dragging = true;
            dragMoved = false;
            dragStartX = e.clientX;
            dragStartCenter = centerMs;
            dragPointerId = e.pointerId;
            canvas.style.cursor = 'grabbing';
        });
        // Live-scrub while dragging is throttled, not fired on every raw pointermove — a browser
        // dispatches those at a much higher rate than any seek pipeline can usefully keep up with,
        // and un-throttled it fired 50-100+ times over a single half-second drag. With several
        // cameras in a view each seek fans out to one fetch per tile, so that flooded the browser's
        // connection pool (confirmed live: net::ERR_INSUFFICIENT_RESOURCES) and made the recorder
        // node's storage reads look "stuck" — they weren't hung, they were queued behind hundreds
        // of already-abandoned requests. ~8/sec is frequent enough to feel live without flooding.
        var SCRUB_THROTTLE_MS = 120;
        var lastScrubFiredAt = 0;

        function endDrag(e) {
            delete activePointers[e.pointerId];
            if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);

            if (pinching) {
                // Lifting one finger of a two-finger pinch drops back to zero or one remaining
                // pointer — either way the pinch itself is over; a single remaining finger does NOT
                // resume as a pan (it never started one, and re-basing dragStartX/dragStartCenter to
                // wherever that finger happens to be now would jump the playhead on release).
                if (Object.keys(activePointers).length < 2) pinching = false;
                return;
            }
            if (!dragging || e.pointerId !== dragPointerId) return;
            dragging = false;
            dragPointerId = null;
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
        }

        canvas.addEventListener('pointermove', function (e) {
            if (activePointers[e.pointerId]) activePointers[e.pointerId] = { x: e.clientX, y: e.clientY };

            if (pinching) {
                var dist = pointerDistance();
                if (dist > 0 && pinchStartDist > 0) {
                    // Fingers spreading apart (dist grows past pinchStartDist) should zoom IN — a
                    // smaller rangeMs, the same direction wheel's deltaY<0 case produces — so the
                    // factor is inverted relative to the raw distance ratio.
                    var zoomFactor = pinchStartDist / dist;
                    rangeMs = Math.min(MAX_RANGE_MS, Math.max(MIN_RANGE_MS, pinchStartRange * zoomFactor));
                    draw();
                    scheduleReload();
                    if (options.onRangeChange) options.onRangeChange(rangeMs);
                }
                return;
            }

            if (!dragging || e.pointerId !== dragPointerId) return;
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
        canvas.addEventListener('pointerup', endDrag);
        // Belt-and-suspenders: a pointer can also be taken away without a pointerup at all (browser
        // decides mid-gesture it's a pan/zoom gesture instead, OS-level palm rejection, a context
        // menu opening under the held button) — pointercancel is the platform's way of saying "this
        // pointer is gone, stop tracking it," and dragging must end here too or it stays stuck the
        // same way the original bug did.
        canvas.addEventListener('pointercancel', endDrag);
        // Second belt-and-suspenders, at the window level: confirmed live as "either timeline can get
        // stuck dragging, unpredictably" with a per-camera Playback page holding two of these side by
        // side. setPointerCapture is supposed to guarantee pointerup/pointercancel land back on this
        // canvas regardless of where the pointer physically ends up — but capture itself can silently
        // fail to take (a pointerId already gone stale, a reflow mid-gesture, browser-specific
        // flakiness), and when it does, the release event is delivered to whatever element is
        // actually under the pointer instead, which this canvas's own listeners never see — dragging
        // stays true forever, exactly the "grabbing cursor that never lets go" this file's pointer-
        // capture rewrite was supposed to have eliminated. A window-level listener always sees the
        // event regardless of where it lands, so it can't be defeated by a capture failure; endDrag
        // is safe to call twice for the same release (its own dragging/pinching guards make the
        // ordinary in-canvas case's second invocation a no-op).
        window.addEventListener('pointerup', endDrag);
        window.addEventListener('pointercancel', endDrag);

        // ── Hover thumbnails (M7 pass 2) ────────────────────────────────────
        // Independent of the drag-scrub pointermove handler above — this one bails while dragging
        // instead of requiring it. options.getThumbnailUrl is only ever supplied for the per-camera
        // timeline; the global (merged-cameras) instance simply never registers this behavior since
        // the option is absent, per the guard on the very first line of the handler below.
        //
        // 5-minute buckets — coarser than the drag-scrub's own granularity, deliberately: enough to
        // notice something changed while hovering, without generating/fetching/caching a new frame
        // every few pixels of mouse movement (see TimelineService.GetThumbnailInfoAsync, which
        // buckets identically server-side — this is a perf optimization, not the source of truth).
        var HOVER_BUCKET_MS = 5 * 60 * 1000;
        var HOVER_THROTTLE_MS = 150;
        var lastHoverFetchAt = 0;
        var hoverToken = 0;
        var THUMB_CACHE_MAX = 200;
        var thumbCache = new Map(); // url -> objectUrl string, or Promise<string|null> while in flight

        var previewEl = document.createElement('div');
        previewEl.style.cssText = 'position:fixed; display:none; max-width:150px; max-height:150px; ' +
            'background:#111; border:1px solid rgba(255,255,255,0.3); border-radius:4px; ' +
            'box-shadow:0 2px 8px rgba(0,0,0,0.5); z-index:2000; pointer-events:none; overflow:hidden;';
        var previewImg = document.createElement('img');
        // No fixed box/object-fit here — the node already scales each thumbnail to fit within
        // 150x150 preserving its camera's own aspect ratio (never distorted, never padded), so the
        // <img> just renders at its natural size, capped by the container's own max-width/height.
        previewImg.style.cssText = 'display:none; max-width:150px; max-height:150px;';
        // Defensive backstop: whatever the root cause of a bad/undecodable blob turns out to be, the
        // user should never see the browser's own broken-image icon — fail into the same "No preview
        // available" state a network/server error already does. Gated on imgLoadToken (set by
        // showImage below) rather than firing unconditionally: setting previewImg.src to a *newer*
        // URL while a previous one is still loading aborts that previous load, which can itself fire
        // a stale 'error' event asynchronously — sometimes arriving *after* the newer src was already
        // set. An ungated handler was confirmed live as a real regression: it blanked out perfectly
        // good, already-loading-successfully images almost every time, since interrupting a load this
        // way during normal fast hovering is the common case, not the exception.
        var imgLoadToken = 0;
        previewImg.addEventListener('error', function () {
            if (imgLoadToken !== hoverToken) return; // this failure belongs to an already-superseded load
            showError('No preview available');
        });
        var previewStatus = document.createElement('div');
        previewStatus.style.cssText = 'color:#aaa; font-size:11px; text-align:center; padding:8px 10px;';
        previewEl.appendChild(previewImg);
        previewEl.appendChild(previewStatus);
        document.body.appendChild(previewEl);

        // Positioned above the cursor using the container's own max dimensions (150x150 + an 8px
        // gap) for the clamp math rather than measuring the actual rendered size — simpler than
        // re-positioning once an image/error message settles into its final size, and the box is
        // never larger than this assumption anyway.
        function positionPreview(clientX) {
            var rect = canvas.getBoundingClientRect();
            var left = Math.max(4, Math.min(clientX - 75, window.innerWidth - 154));
            previewEl.style.left = left + 'px';
            previewEl.style.top = (rect.top - 158) + 'px';
        }
        function showLoading() { previewImg.style.display = 'none'; previewStatus.style.display = 'block'; previewStatus.textContent = 'Loading…'; }
        function showError(msg) { previewImg.style.display = 'none'; previewStatus.style.display = 'block'; previewStatus.textContent = msg; }
        // Stamps imgLoadToken with hoverToken's value *at the moment this src is actually set* — the
        // error listener above compares against hoverToken again whenever it fires, so a load that
        // was superseded before it finished (imgLoadToken now stale relative to a newer hoverToken)
        // is recognized as such even though the error event itself arrives later, asynchronously.
        function showImage(url) { imgLoadToken = hoverToken; previewImg.src = url; previewImg.style.display = 'block'; previewStatus.style.display = 'none'; }

        function evictOldestIfNeeded() {
            if (thumbCache.size <= THUMB_CACHE_MAX) return;
            var oldestKey = thumbCache.keys().next().value;
            var oldestVal = thumbCache.get(oldestKey);
            thumbCache.delete(oldestKey);
            if (typeof oldestVal === 'string') URL.revokeObjectURL(oldestVal);
        }

        // The one fetch actually in flight for a *new* (not-yet-cached) bucket — tracked so a fresh
        // hover into a different, also-uncached bucket can abort it instead of leaving it to run to
        // completion for nothing. The abort propagates all the way through: the Web proxy's own
        // client.GetAsync already passes the request's CancellationToken through to the node, and
        // the node's route passes ctx.RequestAborted into ThumbnailCapture, which kills the ffmpeg
        // process — cancelling the browser fetch stops real work on the node, not just locally.
        var currentFetchController = null;

        // Same bucketed URL in flight or already resolved reuses the in-memory cache — repeated
        // hovers near the same instant never refetch, matching the node's own on-disk cache one
        // level up. myToken guards against a superseded fetch clobbering whatever a later hover
        // already put on screen.
        function fetchThumbnail(url, myToken) {
            var cached = thumbCache.get(url);
            if (typeof cached === 'string') {
                // Bump to most-recently-used: Map.set on an *existing* key does not reorder it, so
                // without this, a bucket the user keeps hovering back to could still be sitting at
                // the "oldest" position by insertion order and get evicted (its blob URL revoked)
                // by evictOldestIfNeeded while it's the very image currently on screen — confirmed
                // as a real cause of the broken-image-icon report, not just a theoretical one.
                thumbCache.delete(url);
                thumbCache.set(url, cached);
                showImage(cached);
                return;
            }
            if (cached) {
                cached.then(function (r) {
                    if (myToken !== hoverToken) return;
                    if (r) showImage(r); else showError('No preview available');
                });
                return;
            }

            if (currentFetchController) currentFetchController.abort();
            var controller = new AbortController();
            currentFetchController = controller;

            showLoading();
            var promise = fetch(url, { signal: controller.signal })
                .then(function (resp) { return resp.ok ? resp.blob().then(function (b) { return URL.createObjectURL(b); }) : null; })
                .catch(function () { return null; }); // covers both a real failure and our own abort() above
            thumbCache.set(url, promise);
            promise.then(function (result) {
                if (currentFetchController === controller) currentFetchController = null;
                if (result) { thumbCache.set(url, result); evictOldestIfNeeded(); } else { thumbCache.delete(url); }
                if (myToken !== hoverToken) return;
                if (result) showImage(result); else showError('No preview available');
            });
        }

        canvas.addEventListener('pointermove', function (e) {
            if (!options.getThumbnailUrl || dragging) { previewEl.style.display = 'none'; return; }
            var now = Date.now();
            if (now - lastHoverFetchAt < HOVER_THROTTLE_MS) return;
            lastHoverFetchAt = now;

            var bucketedMs = Math.floor(xToTime(e.clientX) / HOVER_BUCKET_MS) * HOVER_BUCKET_MS;
            var url = options.getThumbnailUrl(bucketedMs);
            if (!url) { previewEl.style.display = 'none'; return; }

            previewEl.style.display = 'block';
            positionPreview(e.clientX);
            hoverToken++;
            fetchThumbnail(url, hoverToken);
        });
        canvas.addEventListener('pointerleave', function () { previewEl.style.display = 'none'; hoverToken++; });

        window.addEventListener('resize', function () { resizeCanvas(); draw(); });

        // Self-heals the canvas bitmap whenever its rendered box changes size — including the
        // 0 -> real transition that happens when a hidden timeline becomes visible again. Without
        // this, a timeline that was display:none during any resize (confirmed live: a View cell's
        // mini-timeline while its tile is fullscreened, since a fullscreen change fires a window
        // resize) had resizeCanvas() measure it at 0x0 and clamp its bitmap to 1x1, while draw()
        // bailed on the zero size. Nothing re-measured it once it was shown again, so it came back
        // as a 1x1 bitmap stretched across its full width — blank, and only fixable by recreating
        // the whole timeline. The window 'resize' listener above can't cover this: the element's own
        // box changes without the window's doing so.
        if (window.ResizeObserver) {
            new ResizeObserver(function () { resizeCanvas(); draw(); }).observe(canvas);
        }

        resizeCanvas();
        // Always paint once immediately, not only via reload() below — reload() itself no-ops
        // entirely when options.getBuckets isn't supplied (true for the mini-timeline used by a
        // Live-tile/View-cell's playback toggle, which is deliberately just a scrubbable ruler with
        // no coverage-bucket data), so an instance created without it never got its first paint at
        // all: the canvas existed, correctly sized, just never had draw() called on it — completely
        // invisible, confirmed live, not a CSS/positioning issue. draw() already no-ops safely on a
        // zero-size canvas, so calling it unconditionally here is safe for every caller.
        draw();
        reload();

        // Registered so a palette that arrives after this timeline's first paint repaints it. Not
        // unregistered on teardown: the fetch resolves once, early in the page's life, and the guard
        // in its handler swallows a redraw on a canvas that's since been detached.
        instances.push(draw);

        return {
            redraw: draw,
            // Recenters the strip on ms (e.g. following normal playback progress, or mirroring a
            // scrub that happened on a *sibling* timeline) without firing onScrub — the caller
            // already knows the video moved there (or is the one that caused it), this just keeps
            // this strip's visuals in sync with it. Still clamped — playback itself can never
            // legitimately be "in the future" either.
            //
            // Ignored entirely while this timeline is being actively dragged: the page-level
            // playhead-follows-playback loop (setInterval, every 500ms) calls this on every tick
            // regardless of what the user is doing, and calling it mid-drag snapped the strip back
            // to the advancing playback position on every tick — fighting the user's own drag hard
            // enough to feel like the timeline "wasn't responding" to it, since a drag rarely
            // finishes inside one 500ms window. The drag's own pointermove handler is already the
            // authority over centerMs while dragging=true; once it ends, onScrub's own report of the
            // release position is what the caller acts on anyway, so nothing is lost by ignoring
            // programmatic recenters in between.
            setCenter: function (ms) {
                if (dragging) return;
                centerMs = clampCenter(ms);
                draw();
                // Without this, ordinary playback going gray was confirmed live: this is the *only*
                // path that moves centerMs during normal playback (the page-level 500ms
                // playhead-follows-playback loop calls it on every tick), and until now it only ever
                // redrew with whatever buckets the last reload() happened to fetch — never asked for
                // more. A 10-30 minute zoom range (the common case for reviewing a specific moment)
                // walks off the edge of its own last-fetched coverage within single-digit minutes of
                // real playback, or seconds at higher speeds; the newly-scrolled-into stretch has no
                // bucket data at all and reads as unrecorded gray even though the footage is right
                // there, self-"fixing" only when some unrelated drag/zoom happened to trigger a fresh
                // reload. Each of the two sibling timelines (per-camera, all-cameras) reloads
                // independently, which is why one could show real colors while the other sat gray for
                // the same instant — confirmed live as exactly that split.
                //
                // scheduleReload's own debounce is what keeps this cheap during smooth playback (many
                // ticks arrive before it actually fires, coalescing into one fetch) without needing
                // separate throttling logic here.
                // options.getBuckets is absent for a bucket-less mini-timeline (a Live-tile/View-cell
                // playback toggle's own scrubbable ruler) — reload() would no-op there anyway, so
                // skip even scheduling one rather than churning a timer every tick for nothing.
                if (!options.getBuckets) return;
                var visible = visibleRange();
                if (!coveredRange || visible.from < coveredRange.from || visible.to > coveredRange.to) {
                    scheduleReload();
                }
            },
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
            setShowEventTags: function (on) { showEventTags = !!on; draw(); },
            reload: reload
        };
    }

    return { create: create };
})();

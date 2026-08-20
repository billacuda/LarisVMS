# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.125.0] - 2026-08-20

### Fixed

- **Landscape phones still squished some cameras — always the same ones.** Not a layout-geometry bug
  like the earlier landscape reports: the derived phone stack sizes each cell from the cell's *stored*
  aspect ratio, and the view editor defaulted every newly-added cell to `16:9` regardless of what the
  camera actually is. Any camera that isn't 16:9 therefore got a cell shaped wrong for it from the
  moment it was added, and `object-fit: contain` letterboxed the video into a band inside that cell —
  consistently the same cameras, since it follows each camera's own resolution rather than anything
  about position or count. Two changes: the derived phone stack now sizes cells from the camera's real
  probed resolution (`nearest()` in `aspect-ratio.js` maps an actual `width×height` to the closest
  listed ratio), falling back to the stored aspect only when the resolution isn't known; and the
  editor now defaults a newly-added cell to the camera's own ratio instead of a blanket 16:9, so this
  stops recurring at the source. The desktop layout still replays the saved geometry untouched — a
  deliberate editor choice there is a real choice, unlike this derived stack.

Existing views keep their stored per-cell ratios; the phone stack simply stops depending on them
being right. Correcting a cell's dropdown in the editor is still what changes the desktop layout.

Web-only, no node change.

## [0.124.0] - 2026-08-20

### Fixed

- **A Bookmark/Snapshot "▶ Play" deep link landed paused**, needing an extra click on Play — one more
  step than intended for what's supposed to be a one-click jump straight to the moment in question.
  `resolveDeepLink` now starts playback immediately; every other arrival at Playback (picking a view
  fresh, reloading) still lands paused as before.

### Changed

- **Snapshot card thumbnails are noticeably higher resolution.** The shared `/playback-thumbnail`
  path was capped at 150px on the longer edge for every caller — sized for a quick scrub-hover
  glance, but the same cap made a Snapshots card (something a viewer actually looks closely at) hard
  to make out. `exact=true` requests (Pages/Snapshots' own cards) now request 854px — ~480p on a
  16:9 source — while every hover-preview caller keeps the original 150px. The resolution travels as
  a new `maxDim` query param between the Web tier and the node (not part of the signed token — a
  quality knob, not something needing tamper-protection) and is folded into the on-disk thumbnail
  cache filename so a 150px and an 854px capture of the same instant never collide; `ThumbnailBackfillService`
  updated to match so its background-generated hover thumbnails still land where the on-demand path
  looks for them.

**Install-node.ps1 re-run needed** on every recorder node — `LarisVMS.Node`/`LarisVMS.NodeUpdater`
bumped to 0.124.0 in lockstep.

## [0.123.0] - 2026-08-20

### Fixed

- **Snapshots' "▶ Play" links (both the thumbnail and the card-footer button) went nowhere** —
  confirmed live via "copy link address" that the rendered `href` pointed back at `/Snapshots` with
  the page's own current filter state (`cameraId`/`from`/`to`/`kinds`), not at `/Playback` with a
  camera and instant. The `asp-page="/Playback"`/`asp-route-*` tag-helper form of the link was the
  common thread; Bookmarks' own "▶ Play" link does the identical job via a plain hand-written `href`
  string and has been confirmed working since M18. Switched Snapshots to the same plain-string form
  rather than continue chasing why the tag-helper resolved the way it did.

Web-only, no node change.

## [0.122.0] - 2026-08-20

### Fixed

- **Snapshots pagination silently never advanced past page 1**, confirmed live with a URL that
  plainly read `...&page=26` while the page kept showing the same first item. Root cause: Razor
  Pages' own endpoint routing sets a route value literally named `page` on every request — the
  relative page path, used internally to pick which compiled page runs — and the framework's
  composite model binder checks route values before the query string. A handler parameter also named
  `page` (both here and in Audit Logs, which this page's own pagination was ported from) finds that
  route-data entry first, fails to parse a page *path* as an `int`, and silently binds to `0`; the
  query string's own `page=26` is never even consulted. Renamed the parameter to `pageNumber` in both
  places — not a reserved name, so it now binds from the query string as intended.

Web-only, no node change.

## [0.121.0] - 2026-08-20

### Fixed

- **Either Playback timeline (per-camera or all-cameras) could get stuck mid-drag, unpredictably.**
  `setPointerCapture` is supposed to guarantee a drag's `pointerup`/`pointercancel` lands back on the
  canvas that started it regardless of where the pointer ends up — but capture can silently fail to
  take, and when it does the release event is delivered to whatever's actually under the pointer
  instead, which the canvas's own listeners never see. `dragging` stayed `true` forever in that case.
  Added a window-level `pointerup`/`pointercancel` fallback that always sees the release regardless of
  where it lands; safe to fire twice for an ordinary release since the existing dragging/pinching
  guards make the second call a no-op.
- **A new bookmark didn't appear on the timeline until something unrelated (a drag, a camera switch)
  happened to trigger a reload.** `submitBookmark()`'s success handler saved the bookmark but never
  told the per-camera timeline to re-fetch — it now reloads immediately after a successful save.

### Added

- **Snapshot cards now show the event's duration**, not just its timestamp — a new `Duration` field
  on `SnapshotDto` (`EndUtc - StartUtc` off the underlying `MotionSpan`, the same span `AtUtc`'s own
  midpoint is already derived from), formatted as `12s` / `3m 05s` / `1h 02m`.

Web-only, no node change.

## [0.120.0] - 2026-08-20

### Fixed

- **Playback still flashed a black cell at every segment boundary**, even with v0.118.0's
  one-segment-ahead prefetch already removing the network wait and v0.119.0 already removing the
  "Loading…" text on that path. The remaining gap is structural: switching segments tears the
  `<video>` element down (`removeAttribute('src')` + `load()`), which blanks it immediately, while
  the replacement `MediaSource`/`SourceBuffer` append and first decode still take real time even
  with the bytes already in hand. Each tile now snapshots its last painted frame onto a canvas
  overlaid on the video before the teardown, and only hides it once the new segment has genuinely
  produced a frame (`seeked`/`playing`, whichever lands first) — so the boundary reads as a brief
  freeze instead of a flash to black. The snapshot copies the video's current digital-zoom transform,
  so a frozen frame while zoomed in doesn't visually snap back to 1x for the gap. Every path that
  ends without a frame to reveal (missing/unreachable segment, undecodable stream, no recording at
  the target time, tile disposal) clears the overlay explicitly rather than leaving a stale frame up.

### Added

- **Play/Pause is now available while a Playback tile is fullscreen.** The page toolbar's own
  transport control is a page-level element and therefore isn't rendered at all in that state; the
  timeline gets moved into the fullscreened tile, but Play/Pause had no counterpart, so starting or
  stopping playback meant leaving fullscreen first. The fullscreen control cluster gains a button
  driving the same toggle, label-synced with the toolbar's.

Web-only, no node change.

## [0.119.0] - 2026-08-20

### Fixed

- **Every hover-thumbnail request started 400ing.** `/playback-thumbnail`'s new `exact` parameter
  (v0.118.0) had no default value — ASP.NET Core's minimal APIs treat a defaultless primitive
  parameter as *required*, and every caller except Snapshots (the scrub-hover preview, the
  Dashboard's latest-thumbnail column) omits it entirely, since it predates that parameter. Now
  defaults to `false`.
- **Exact-instant Snapshots thumbnails routinely failed with 502.** `Segment.DurationMs` is
  computed from wall-clock `EndUtc - StartUtc` (`NodeService.RecordSegmentsAsync`), not
  re-measured from the file's actual encoded length — the two can drift by a second or so. The
  bucketed hover-preview lookup almost always requests offset 0 (always safe — any valid segment
  file has content at its own start), but the exact lookup routinely targets an offset right up
  against that same drift-prone boundary; the node's ffmpeg extraction seeks past the file's real
  content and correctly reports that as 502 rather than hanging. The Web-tier proxy now retries
  once at offset 0 on the same file on a 502, trading exact-instant precision for a guaranteed hit
  only on this already-failed path.
- **A "Loading…" flash still showed on every segment transition even with prefetching in place.**
  The status text was being set unconditionally, including on a prefetch hit where the bytes are
  already in hand and there's nothing to wait for — removed for that case.
- **The timeline's mouse cursor could get stuck reading "grabbing".** A two-finger pinch that
  started as a one-finger drag correctly cancelled the drag but never reset the cursor style back;
  neither did the pinch-end path, since by then the drag state it was checking had already been
  cleared. Now reset the moment a second finger lands.
- **Snapshots pagination could show a duplicate or skip a row between pages.** `OFFSET`/`FETCH`
  has no guaranteed order among rows tied on the sort key alone, and several motion spans easily
  share the same `StartUtc` to the second. Added a deterministic secondary sort key (`Id`).

### Added

- **Per-visit event-type filter on the Snapshots page itself** — checkboxes (Motion, custom tags,
  each detected class) narrow what the current viewer sees for this browse session, on top of
  whatever the admin-level per-type setting (v0.118.0) already allows system-wide.

Web-only, no node change.

## [0.118.0] - 2026-08-20

### Fixed

- **Snapshots thumbnails were cropped and often missing.** `object-fit: cover` cropped any
  non-16:9 source frame to fill the card instead of showing it whole — switched to `contain`.
  Separately, thumbnails were resolved through the same 5-minute-bucketed lookup the Playback
  timeline's hover preview uses (deliberately coarse there, to stay cheap under rapid scrub-hover)
  — a poor fit for a motion event, which is exactly the kind of moment likely to fall in that
  bucketed lookup's own coverage gap on a Motion-mode camera. New `GetExactThumbnailInfoAsync` /
  `/playback-thumbnail?...&exact=true` resolves the precise requested instant instead; Snapshots
  now uses it. A card with no image now shows "No thumbnail available" instead of a bare gap.
- **A Bookmark/Snapshot "▶ Play" link could land at the covering segment's start instead of the
  bookmarked instant.** Resolving the deep link's target View kicked off its own
  persisted-position-or-most-recent-recording seek, unawaited, which raced the deep link's own seek
  to the actual instant — whichever one's segment lookup happened to resolve last won. The deep
  link now skips that initial seek entirely rather than trying to out-race it.
- **Faster-than-1x playback stalled with a blank "Loading…" at every ~60s segment boundary.** Each
  transition started fetching the next segment cold only once the current one ended. Segments now
  prefetch one ahead in the background once the current one starts playing, so a sequential
  transition can skip the network round trip — the same "look ahead so it's already buffered"
  fetch is skipped harmlessly on an arbitrary seek that doesn't land on the segment being prefetched.
- **Landscape phones lost their 1/2-column stack**, falling back to however many columns the
  replayed desktop layout happened to use — a real fix for an earlier "squished vertically" bug,
  but one that (over-)applied to every short viewport, phone or not. The derived phone stack now
  also handles landscape, scrolling to show cameras beyond what fits at once — exactly like it
  already does in portrait. (A first attempt instead tried to *fit* every row into the available
  height without scrolling, the same way the replayed desktop layout does; confirmed live as a
  regression of its own — dividing a short viewport across many stacked single-column cameras
  squashed each one far more than the desktop grid's own fitting ever does, since a desktop layout
  usually spreads cameras across several of its 12 columns instead of stacking all of them in one.
  Reverted in favor of the simpler unfitted, scrollable stack.)
- **Fullscreen controls (mute/volume/exit) could sit unclickable underneath the fullscreen
  timeline** on a phone, where there's no hover to reveal one and hide the other — both anchor to
  the same bottom-right corner with no clearance between them. The timeline now reserves space for
  the controls' own measured width, and the controls sit above it in stacking order regardless.

### Added

- **Snapshots: admin-configurable event-type visibility.** New checkboxes on Admin → Settings →
  Events (Motion + each detected class) control what appears in the Snapshots browser — a custom
  event tag always appears regardless, since each one already has its own enabled/disabled toggle.
  Missing = enabled, so an untouched deployment keeps showing everything it already did.
- **Snapshots use the midpoint of the event, not its start.** A span's opening instant is often
  the least representative frame of it (someone just entering the frame edge); the middle is far
  more likely to actually show whatever triggered it.
- **Bookmarks now render as markers on Playback's per-camera timeline** — a small flag at each
  bookmarked instant, fetched alongside the timeline's own coverage buckets for the selected camera.
- **Refreshing Playback now restores the last-open View**, not just the remembered scrub
  position/zoom — those persisted already, but the View selection itself didn't, so a reload used
  to always land back on a bare "(choose a view)" picker with nothing rendered.
- **Pinch-to-zoom on both Playback timelines**, for phone/tablet — wheel-zoom (mouse/trackpad) was
  the only way to zoom before this. Anchors on the fixed playhead center, same as wheel-zoom, not
  the touch midpoint.

Web-only, no node change.

## [0.117.0] - 2026-08-19

### Added

- **M18: snapshots browser.** Browse motion history as thumbnails instead of only as timeline color.
  - New **Snapshots** page (nav item beside Bookmarks) lists every motion event — zone-triggered,
    camera-pushed, custom `EventTagRule` tags, and object detections alike — as a paged grid of
    thumbnails, newest first, filterable by camera and date. Server-side paged (same shape as Audit
    Logs), since `MotionSpans` is a volume table too large to page client-side.
  - **No new table, no new capture pipeline.** This reuses `MotionSpans` directly — every motion
    event is already "tagged" with a zone, rule, or detected class — and each card's thumbnail is
    pulled live from `/playback-thumbnail`, the exact historical frame-extraction path Playback's own
    hover thumbnails already use. Nothing is captured or stored a second time; a card with no image
    just means that moment's footage has since aged out of retention.
  - Each card's label, color, and emoji resolve with the same precedence `TimelineService`'s bucket
    coloring already applies — a custom tag's own color wins, then a detected class's
    admin-configurable color, then plain motion — so a snapshot can never show a different color than
    the same instant renders as on the timeline.
  - **▶ Play** reuses Bookmarks' own deep-link scheme (`?cameraId=&atUtc=`) into Playback.
  - Shared across everyone holding `Playback.View`, same visibility model as Bookmarks/Exports — no
    per-camera `CameraAccess` narrowing.

Web-only, no node change.

## [0.116.0] - 2026-08-19

### Added

- **M18: bookmarks.** Mark a moment during Playback for later, and jump straight back to it.
  - New `Bookmark` entity — camera, instant, note, who made it — with no foreign key to `Camera`,
    matching `ExportJobItem`'s own "historical record, don't cascade or block camera deletion"
    precedent. Shared across everyone holding `Playback.View`, same visibility model as Exports.
  - Playback's toolbar gets a **Bookmark…** button beside Export: marks the *primary* (starred)
    tile's camera at the current playhead, since a bookmark is one instant on one camera's own
    timeline, not a whole view.
  - New **Bookmarks** page (nav item beside Exports) lists every bookmark, newest first, with a
    **▶ Play** link back into Playback (`?cameraId=&atUtc=`). Playback has no camera picker of its
    own, so the client resolves the deep link itself: the first View the current user can see that
    contains that camera, then seeks to the instant. A bookmark whose camera has since dropped out
    of every visible View surfaces a status message on arrival instead of silently landing on
    whatever View happened to load first.
  - New `BookmarkRetentionService` (6-hour sweep, same shape as `AuditLogRetentionService`) deletes
    a bookmark once its camera's earliest remaining `Segment` starts after the bookmark's own
    timestamp — "entries expire with the footage they point at," the feature's own explicit
    requirement, not a bolted-on cleanup.
  - Reuses `Playback.View` plus the existing per-camera `CameraAccessActions.Playback` check; no new
    permission.

Web-only, no node change.

## [0.115.0] - 2026-08-19

### Added

- **M18: basic PTZ.** A directional pad + zoom on each PTZ-capable camera's Live tile.
  - `OnvifPtzClient` (ONVIF ver20 PTZ, `ContinuousMove`/`Stop`) is called directly from
    `LarisVMS.Web` — the same `"onvif"` named `HttpClient` camera probing already uses — not routed
    through a recorder node, since a PTZ command is a quick request/response, not a long-lived
    session the way live view or event polling are.
  - `PtzService` resolves a camera's PTZ service address from `CameraCapabilities.RawProbeJson`
    (populated by the capability prober since M8, previously unused for anything) plus the Main
    stream's ONVIF profile token, and returns `false` rather than throwing when a camera has no
    usable PTZ target — lets the API tell "doesn't support PTZ" apart from "unreachable."
  - New `POST /api/cameras/{id}/ptz/move` and `/stop`, gated `Cameras.View` (the same broad
    permission the Live page itself needs) narrowed by a per-camera `CameraAccessActions.Ptz` check
    — that flag has existed in the `CameraAccess` schema since M14, listed in the admin grants page
    as "schema-ready, not yet enforced" until this pass.
  - The Live page's directional pad re-issues a held direction every 2 seconds and always sends an
    explicit `Stop` on release — a client-side dead-man's-switch alongside the device's own 5-second
    auto-stop timeout, so a dropped connection mid-press can't leave a camera panning indefinitely.

  **Not yet run against a real PTZ camera** — no PTZ-capable hardware available to test against
  during development, same caveat M17/M18's transcode work started with before real hardware access
  changed that. The ONVIF request shapes are built from the spec and this app's existing
  `OnvifMediaClient`/`OnvifDeviceClient` conventions, not verified live.

Web-only, no node change.

## [0.114.0] - 2026-08-19

### Fixed

- **8× playback speed quietly reset to 1× after a few seconds.** Every segment transition (including
  the natural end-of-segment auto-advance during ordinary playback, not just an explicit seek) calls
  `videoEl.load()`, and browsers reset `playbackRate` back to 1 when that runs — nothing re-applied
  it afterward, so a fast-forward rate silently reverted the moment playback crossed into the next
  segment (roughly every 60s ÷ 8 ≈ 7.5s of wall-clock time at 8×, matching "after a few seconds"
  exactly). `createTile`'s player now remembers the desired rate (`setPlaybackRate`) and re-applies
  it on every future segment load, not just once when the speed was chosen.
- **16× and 32× dropped the video and got stuck on "Loading…" after a few keyframes.** The stepped
  fast-forward loop (speeds above 8×, see 0.109.0) fired a new seek every fixed 200ms regardless of
  whether the previous one had actually finished loading — a real segment fetch can easily take
  longer than that, so each new seek pre-empted (`AbortController`) the one still in flight before it
  ever got to show a frame, and with ticks close enough together every seek was pre-empted in turn,
  forever. The loop now awaits each step's own seek before scheduling the next, self-pacing to
  whatever the real fetch/seek latency allows instead of piling up overlapping seeks.

Web-only, no node change.

## [0.113.0] - 2026-08-19

### Changed

- **Privacy-mask burn-in (M18) is switched off and deferred.** 0.112.0's fix addressed one real,
  confirmed bug but did not resolve the reported symptom — a camera with a Privacy zone still gets
  stuck cycling `Connecting…`/`Reconnecting…` forever on retest, and the actual root cause is still
  unknown. Rather than leave the feature enabled-but-broken (saving a Privacy zone currently breaks
  that camera's recording outright), `NodeWorker.PrivacyMaskEnabled` is now `false`: a Privacy zone
  is a safe no-op again, same as it's been since M8 and same as `CameraMotion` still is today —
  drawable and saveable, no effect on recording. The zone editor's label reverts to "not yet active"
  accordingly. Every underlying piece built for this (`PrivacyMaskFilterBuilder`, `EncoderSelection`,
  `RecordingSession`'s transcode branch, and their tests) is untouched and ready for whenever the real
  bug is found — only the one switch needs to flip back.

  **install-node.ps1 re-run: not needed** — ordinary node auto-update covers it.
  `LarisVMS.Node`/`LarisVMS.NodeUpdater` bumped to 0.113.0 in lockstep.

## [0.112.0] - 2026-08-19

### Fixed

- **Removed one confirmed-real bug in privacy-mask burn-in — pairing decode-side `-hwaccel`
  (`qsv`/`cuda`) with the plain CPU `drawbox` filter, which made ffmpeg fail immediately on every
  attempt since hardware-decoded frames stay on the GPU in a format that filter can't touch.
  `RecordingSession` no longer requests decode hwaccel for this pipeline; the encoder stays hardware
  when one was detected. `EncodePipeline.DecodeHwaccelArgs` itself is untouched, still available for
  a future consumer whose filter chain is itself hardware-native.**

  **This did not resolve the reported symptom.** Retested live on the same real hardware after this
  fix: a camera with a Privacy zone still gets stuck cycling `Connecting…`/`Reconnecting…`, no mask
  and no footage. The hwaccel/CPU-filter mismatch above was real and worth removing regardless, but
  it was not the (or not the only) cause of the original report. Root cause still unknown as of this
  entry — **privacy-mask burn-in is on hold, deferred rather than debugged further for now.** Do not
  enable a Privacy zone on a production camera; it currently breaks that camera's recording entirely
  rather than degrading gracefully. `Admin`/the zone editor still lets one be drawn and saved (M8
  behavior), which itself is worth revisiting once this is picked back up — saving one currently has
  a real, broken effect, not a no-op.

  **install-node.ps1 re-run: not needed** — ordinary node auto-update covers it.
  `LarisVMS.Node`/`LarisVMS.NodeUpdater` bumped to 0.112.0 in lockstep.

## [0.111.0] - 2026-08-19

### Added

- **M18 pass 1: static privacy-mask burn-in** — the first real feature to consume M17's
  `EncodePipeline`. `ZoneKind.Privacy` has existed since M8 (drawable and saveable in the zone editor,
  labeled "not yet active") with no effect anywhere; a camera's enabled Privacy zones now burn in as
  black boxes over both its recordings and its live feed.
  - Each zone burns in as its **bounding box**, not its exact drawn outline — a deliberate scope
    decision (over-masking a few extra pixels around an odd shape is the safe default for a privacy
    control; exact-polygon masking would need a per-pixel overlay image, real complexity for a
    difference that only matters at the mask's own edge), via ffmpeg's own `iw`/`ih` runtime
    variables so the filter needs no advance knowledge of the stream's actual resolution.
  - `RecordingSession` switches from `-c copy` to a real decode/filter/encode **only** for a camera
    with at least one enabled Privacy zone — every other camera keeps today's exact zero-transcode
    pipeline, unchanged. Both `-f tee` legs (recorded segments and live view) share the one encoded,
    already-masked stream, so live view can never show what a recording hides.
  - Encoder choice prefers hardware — NVENC, then QSV, then AMF — from this node's own M17 capability
    probe, falling back to software `libx264` (always available, and used immediately if the probe
    hasn't reported in yet or found nothing, rather than ever recording a configured zone unmasked).
  - Editing a camera's Privacy zones (add/edit/remove) now restarts that camera's recording session
    to pick up the change — briefly interrupts recording for that one camera, the same trade already
    accepted for `ServerMotion`/`Ignore` zone edits restarting the motion session.
  - **Fixes a latent hang** found while building the restart above: `RecordingSession.RunAsync` only
    killed a still-running ffmpeg process when the stall watchdog fired, never on a plain
    cancellation — harmless before this pass (the only thing that ever cancelled a session mid-run
    was whole-node shutdown, which cancels and awaits every session together), but this pass's own
    per-camera restart is the first thing that cancels one healthy session on its own, and would have
    hung waiting on a process nothing ever told to stop.

  **Not yet run against real hardware encoders** — this is the first feature that could exercise
  M17's QSV/NVENC/AMF paths for real, but that verification hasn't happened yet as of this pass.
  AMD AMF's decode-hwaccel mapping in particular is still a documented guess, not a tested one (see
  M17's own notes on why).

  **install-node.ps1 re-run: not needed** — ordinary node auto-update covers it.
  `LarisVMS.Node`/`LarisVMS.NodeUpdater` bumped to 0.111.0 in lockstep.

## [0.110.0] - 2026-08-19

### Added

- **M17 foundation: hardware-transcode capability probing.** No consumer feature uses this yet
  (privacy-mask burn-in and adaptive streaming, both M18, are what will) — this pass is the probe and
  the admin-visible result it's meant to be built on.
  - `FfmpegCapabilityProber` (`LarisVMS.Media`) runs `ffmpeg -encoders` once at node startup and
    parses which of a closed, known set (`libx264`/`libx265`, `h264_qsv`/`hevc_qsv`,
    `h264_nvenc`/`hevc_nvenc`, `h264_amf`/`hevc_amf`) this node's own ffmpeg build actually offers.
    Never throws — a failed probe just means nothing gets reported that run, the same "best-effort,
    can't block startup" pattern every other node-side probe in this app already follows.
  - `Node.DetectedEncodersJson` stores the result, refreshed on every heartbeat (like `Version`) so a
    node upgrading its ffmpeg build or GPU driver is reflected without re-registering.
    `Admin → Nodes` shows each node's detected encoders as badges.
  - `EncodePipeline` (`LarisVMS.Media`): a pure, unit-tested `-hwaccel`/`-vf`/`-c:v` argument builder
    every downstream transcode feature will share, so each one is "pick a filter chain and call this"
    rather than a new ffmpeg invocation invented per feature. `EncoderFamilies.For` maps a detected
    encoder name to which decode-side `-hwaccel` actually pairs with it (`qsv`/`cuda`; AMD AMF gets
    none — ffmpeg's own AMF decode-hwaccel support on Windows is inconsistent enough across driver
    versions that guessing here risked being wrong more often than it helped; an AMF encode still
    gets the hardware encoder, just with software decode feeding it, until this is revisited against
    real AMD hardware).

  None of this has been run against real Intel/NVIDIA/AMD hardware — the parsing is verified against
  captured real `ffmpeg -encoders` output, and the hwaccel mapping against ffmpeg's own documented
  flag names, but actual hardware-encoder behavior needs a real probe pass the same way every vendor
  camera integration in this app's history has, before M18 builds a feature on top of it.

  **install-node.ps1 re-run: not needed.** Nothing about first-time node provisioning changed;
  ordinary node auto-update (`NodeBuildService`/`deploy.ps1`) covers this. `LarisVMS.Node`/
  `LarisVMS.NodeUpdater` bumped to 0.110.0 in lockstep per this project's usual rule for any
  Node-touching pass.

## [0.109.0] - 2026-08-18

### Added

- **M16 viewing experience — partial pass.** Four of the five roadmapped items:
  - **Pinch-to-zoom in fullscreen** (`fullscreen-tile.js`, shared by Live and Playback), alongside
    the existing wheel-zoom/drag-pan. Tracks up to two active pointers by id — a touchscreen delivers
    each finger as its own Pointer Events stream, the same events this file already used for mouse
    drag-to-pan, so pinch is "the same events, tracked for two pointers" rather than a separate touch
    API. A second finger landing hands off cleanly from an in-progress single-finger pan. Needs
    `touch-action: none` on `.tile-fullscreen` (new in `site.css`) so the browser doesn't claim the
    gesture as native page pinch-zoom before JS ever sees it.
  - **Drag-select-to-zoom on a Playback grid cell.** Dragging at 1× now draws a selection rectangle
    and zooms+pans to fill it on release, instead of only panning once already zoomed — one gesture,
    two meanings depending on current zoom. Scoped to Playback's existing digital-zoom grid cells,
    not Live (which has no zoom feature to extend yet).
  - **Playback speed, 1/32×–32×**, a new toolbar selector. Native `<video>.playbackRate` covers
    1/32× through 8× (slow motion has no decode-cost ceiling; browsers handle it natively). Above 8×
    switches to a seek-driven "stepped" mode instead — every tile pauses and a timer periodically
    re-seeks the shared playhead, reusing the same per-segment seek path scrubbing already uses. Each
    seek decodes fresh from that segment's own keyframe rather than continuously decoding the frames
    in between, which is what "I-frame-only decode" means in practice for this segment-fetch player
    (there's no in-app demuxer parsing GOP structure to selectively decode I-frames from a continuous
    stream) — expect a slideshow, not smooth motion, above 8×.
  - **Playback event-tag toggle, off by default, per-user.** New `timeline.js` `showEventTags`
    option (defaults `true` so every other caller — the View cell mini-timeline — is unaffected);
    Playback's own toggle persists through the existing per-user preferences API
    (`playback.eventTags`). A real-phone walkthrough (feeding the rest of this milestone) found the
    tag-colored timeline busy for everyday review, so this flips it from always-on to opt-in.

  **Deliberately not attempted, flagged rather than silently skipped: the fifth item, "mobile UI
  polish (scoped from a real phone walkthrough)."** That walkthrough is explicit input this pass
  doesn't have — M13's own landscape-squish bug is exactly the kind of thing that only surfaced from
  someone's thumb on an actual phone, not from reading the CSS. Speculative mobile tweaks without a
  concrete walkthrough to work from would be guessing, not polish.

Web-only, no node change.

## [0.108.0] - 2026-08-18

### Added

- **M15 pass 4: alerting.** `Admin → Alerts` — create a rule that watches one camera or node for a
  condition (camera not reporting, node offline, node storage below a percentage) and fires through
  up to six delivery channels (email, webhook, ntfy, Pushover, Slack, Teams), each with its own
  enable toggle and config. `AlertEvaluatorService` (`BackgroundService`, 1-minute tick, same shape as
  `CameraReprobeService`) re-evaluates every enabled rule and reuses `DashboardService`'s own
  freshness/online windows for the first two conditions, so an alert and the Dashboard's own badges
  always agree on what "not reporting"/"offline" means. Each rule has its own cooldown so a condition
  that stays true doesn't re-alert every tick; deliberately no "resolved" notification when a
  condition clears — this pass only fires on trip. A rule is scoped to exactly one camera or node
  (not a fleet-wide wildcard) and at most one delivery per channel — both are deliberate scope calls
  to keep the cooldown model and the edit form simple, not silent limitations.

  Delivery config (webhook/Slack/Teams URLs, Pushover app/user tokens, ntfy topic) is encrypted at
  rest through the same `SecretProtection` pattern as every other integration secret in this app —
  a webhook URL is itself a bearer credential. Email delivery routes through the existing
  `IEmailService`/`EmailSettings` from the earlier M15 passes rather than needing its own provider
  config.

Nothing here does hardware transcode, motion-rate, or S.M.A.R.T./CPU/GPU conditions yet — those need
capability this app doesn't have until M17/M20. Web-only, no node change.

## [0.107.0] - 2026-08-18

### Added

- **M15 pass 3: Gmail OAuth2 email provider**, completing the email provider abstraction (SMTP, Graph,
  and Gmail are all implemented now). `GmailEmailProvider` (MailKit + `SaslMechanismOAuth2`, no Google
  SDK dependency) exchanges the stored refresh token for a fresh access token on every send rather
  than caching one. `Pages/Admin/OAuthCallback` handles the consent redirect Google sends the browser
  back to: `GoogleOAuthConnectProvider` builds the authorization URL and exchanges the returned code
  for a refresh token, guarded by a Data-Protector-protected `state` parameter that expires after 10
  minutes. `Admin → Settings → Email` gained a Gmail card (client ID/secret, Gmail address, "Save and
  connect to Google") alongside the Provider selector. `GmailRefreshToken` is written only by the
  callback — never typed into the form, so a resave of the other fields can't accidentally clear it.
  Ported from rsolva's `GoogleOAuthConnectProvider`/`GmailEmailProvider`/`OAuthCallback`, adapted for
  `EmailSettings` being a singleton row instead of rsolva's multi-account `EmailAccount`.

The README's new "Email" subsection documents the provider-side setup each of Graph and Gmail needs
(Entra app registration + `Mail.Send` permission; a Google Cloud OAuth client with
`/Admin/OAuthCallback` registered as an authorized redirect URI) — neither works out of the box the
way SMTP does. Web-only, no node change.

## [0.106.0] - 2026-08-18

### Added

- **M15 pass 2: Microsoft Graph email provider.** `GraphEmailProvider` (`Microsoft.Graph` +
  `Azure.Identity`'s `ClientSecretCredential`) sends through `Users[mailbox].SendMail` using app-only
  client-credentials auth — no per-user consent, no refresh token; the client secret itself is the
  durable credential, encrypted at rest the same way `SmtpPassword` is. Ported send-only from rsolva's
  `GraphEmailProvider` (LarisVMS only ever sends alerts — no inbound fetch/mark-seen half). `Admin →
  Settings → Email` gained a Provider selector (SMTP / Microsoft Graph) and a Graph fields card
  (tenant ID, client ID, client secret, optional shared mailbox — defaults to the from address when
  left blank).

Gmail OAuth2 is still the next M15 pass — the one that actually needs a per-user consent redirect and
refresh-token storage, unlike Graph's app-only flow. Web-only, no node change.

## [0.105.0] - 2026-08-18

### Added

- **M15 pass 1: outbound email.** `Admin → Settings → Email` configures the sender the alert
  evaluator (a later M15 pass) will send through: from address/name and SMTP host/port/SSL/username/
  password. `EmailSettings` is a singleton row; the SMTP password encrypts at rest through the same
  `SecretProtection` pattern as camera credentials and never re-populates into the form on load (blank
  means "unchanged," same convention as `Pages/Cameras/Edit`). "Save and send test" saves the form
  first, then sends through exactly what was just persisted, so a test can never pass against an
  unsaved edit. `IEmailProvider`/`EmailProviderFactory` are ported from rsolva's provider-strategy
  shape (config passed as an opaque JSON blob per provider, dictionary-resolved by `EmailProviderType`)
  so Graph and Gmail OAuth2 slot in later without reshaping this interface — only `SmtpEmailProvider`
  (MailKit) exists so far. Nothing in the app calls `IEmailService` yet outside the test-send button —
  the alert-rule evaluator that will is separate, not-yet-built work.

Web-only, no node change.

## [0.104.0] - 2026-08-18

### Added

- **Admin → Settings → Roles and → Users** — the admin UI M14.5 flagged as missing. Roles: create,
  rename, and delete roles, and edit each role's Resource×Action permission matrix against
  `PermissionCatalog` (the fixed, known set of pairs the app actually checks anywhere — a closed list
  of checkboxes rather than free text, so a grant can't be typo'd into matching nothing). Users:
  create an account (email, password, display name, role(s)), change a user's roles, enable/disable
  an account (reuses ASP.NET Core Identity's own lockout mechanism — `LockoutEnd = MaxValue` — rather
  than a new column), and reset a password. Two guard rails (`UserManagementPolicy`) block removing
  the last Administrator's Administrator role or disabling the last enabled Administrator account —
  there is no recovery path for either short of editing the database directly. `Administrator` itself
  can't be renamed, deleted, or have its matrix edited here: `PermissionService` bypasses the
  `Permission` table for it entirely by role name, so checkboxes would silently do nothing.
  `AspNetRoles` had, until now, only ever held the two roles the setup wizard seeds, and `Permission`
  rows had only ever been written by that same one-time seed — this is the first admin surface for
  either. With self-registration disabled since 0.103.0, this is also now the only way to provision a
  new account.

Web-only, no node change.

## [0.103.0] - 2026-08-18

### Security

- **Self-registration is now disabled.** ASP.NET Core Identity's default scaffolded UI ships a
  `/Identity/Account/Register` page, and nothing in this app overrode or disabled it — anyone who
  could reach the site could create an account, with no invite or approval step. A self-registered
  account got no role and so couldn't actually do anything (every permission check fails closed with
  zero roles), but that was an accident of there being nothing to grant a new account yet, not a
  deliberate access control. Found while looking into why there's no admin page to manage who has an
  account. The route now redirects to Login; real account creation is what the user-management admin
  page above is for.

Web-only, no node change.

## [0.102.0] - 2026-08-18

### Added

- **Live view and playback can now run on a port of their own**, separate from the management
  interface (`Admin → Settings → Security`). Once set, `/live`, `/playback-segment`,
  `/playback-thumbnail`, `/export-download`, and camera snapshots stop responding on the management
  port, and every other route stops responding on the new one — useful for firewalling the two
  differently, or exposing only one beyond the LAN. The setting alone doesn't open a socket: IIS's
  own site bindings do that, so this only takes effect once a matching `New-WebBinding` exists for
  the same site (documented in the README, alongside the setting's own help text). Left blank (the
  default), nothing changes — every route keeps sharing whatever port(s) IIS already binds, exactly
  as before this feature existed.

**This closes out M14** (identity, preferences, access control) — the last of five passes shipped
across this session: per-role session lifetime, server-backed user preferences, a per-user column
picker, per-camera access control enforcement, and now this.

Web-only, no node change.

## [0.101.0] - 2026-08-18

### Added

- **Per-camera access control is now enforced.** `CameraAccess` — the per-camera/group ACL layered
  on top of the global Cameras/Playback/Exports permissions — has existed in the schema since M1 but
  was never written to or read by anything. It now gates the camera list, live viewing, Playback, and
  export creation, and gets its first admin surface: `Admin → Settings → Camera Access`, granting a
  role View/Playback/Export/PTZ/Talk/Configure access to every camera, one camera group (cascading to
  its sub-groups), or a single camera.

  **A role with no grants is unrestricted and sees every camera** — this table narrows access, it
  doesn't default-deny the moment it exists unpopulated. Every existing deployment's every existing
  role has zero grants today, so this changes nothing until an admin deliberately adds one.

  Enforcement is at the page level: a camera outside a viewer's grants is removed from the list they
  see, and from the data feed a View or Playback page hands to the browser, so a restricted camera's
  stream metadata never reaches the client in the first place. Export additionally checks
  server-side on job creation, since it produces a persistent downloadable file rather than a
  read-only view. Role-only for now — an individual-user grant is schema-ready and enforced
  identically by the same service, it just has no admin picker yet, since this app has no
  user-management page to choose a user from.

  **Known scoping boundary, not silently left out:** the underlying playback data-proxy endpoints
  (segment/thumbnail streaming, the timeline API) don't yet independently re-check CameraAccess —
  once a camera is filtered out of a viewer's UI they have no path to its id through this app, but a
  client that already knew another camera's id out-of-band could still request it directly. Closing
  that fully is a larger, separate hardening pass across several endpoints, tracked on the roadmap
  alongside the REST API's own permission model rather than folded in here.

Web-only, no node change.

## [0.100.0] - 2026-08-18

### Added

- **A per-user column picker** — a "Columns" toggle on Cameras and the Dashboard lets you hide
  columns you don't care about (Cameras: Host, HTTPS, Group, Node, Manufacturer/Model, Profiles,
  Capabilities, Streams, Storage used, Retention, Last probed; Dashboard: Node, FPS, Bitrate, Audio,
  Reconnects, Last report). The choice is saved through the same per-user preference store as theme
  and table page size, so it follows you across devices.

  Built as a reusable module (`column-picker.js`), not a one-off for these two pages: any table opts
  in with `data-column-picker` on the `<table>` and `data-col="<key>"` on whichever `<th>` elements
  should be toggleable — a checkbox column, the primary name/link column, and an actions column are
  simply left without the attribute and always show. Adding it to another table needs no further
  script work, only those markup attributes.

  Hiding is done with injected CSS scoped to that table's id, not by touching individual `<td>`
  elements — the same reasoning the Dashboard's existing thumbnail toggle already used: it composes
  for free with sorting, pagination, and a page's own AJAX re-render (the Dashboard rebuilds its
  entire `<tbody>` every 60 seconds) without any of them needing to know a column picker exists.

Web-only, no node change.

## [0.99.0] - 2026-08-18

### Added

- **User preferences now persist in the database, not just `localStorage`.** Theme, the
  last-watched Live view, the dashboard thumbnail-column toggle, every table's remembered page
  size, and Playback's remembered scrub position and 24-hour-clock toggle all used to live only in
  the browser — a preference set on one device simply didn't exist on another, and reset outright on
  a fresh sign-in from a new machine. All six now follow the signed-in user everywhere, through a new
  `UserPreference` table and a small `GET/PUT /api/preferences` API.

  Theme is the one exception worth calling out: the anti-flash script that applies it before first
  paint can't wait on a network round trip, so it still reads `localStorage` first for that instant
  apply, and only adopts the server's value on a browser/device that has never stored a theme choice
  of its own — a normal reload on a device you've used before never flashes.

  The last-watched Live view's redirect moved fully server-side (`Live/Index.cshtml.cs`) rather than
  a client script reading the preference after page load — one fewer round trip, and it now also
  degrades correctly if the saved view was since deleted or unshared, falling back to the first
  visible view instead of a client script's own `indexOf` check just returning nothing.

Web-only, no node change.

## [0.98.0] - 2026-08-18

### Added

- **Session lifetime is now configurable per role** (`Admin → Settings → Security`), replacing the
  previous fixed 60-minute cookie timeout that was expiring sessions too fast. Defaults to 24 hours;
  **0 means that role never expires.** A user holding more than one role is bound by whichever role's
  own limit is shortest — a role set to "never expire" does not let a user escape a stricter role's
  own limit just by also holding it, so the setting can't be quietly bypassed by role combination.

  Enforced in the cookie authentication pipeline's `OnValidatePrincipal` handler, chained after (not
  replacing) ASP.NET Core Identity's own security-stamp revalidation — a password change still signs
  a user out everywhere immediately, unaffected by this. The cookie's own outer expiry moved from 60
  minutes to just over a year, since the real per-role cutoff is now enforced here instead; a role
  actually configured for 0 now behaves as truly unlimited rather than silently capped by the cookie
  itself.

Web-only, no node change.

## [0.97.0] - 2026-08-18

### Fixed

- **The Viewer role has never had a single working permission, for any resource, since it was first
  seeded.** Every `Permission` row `SetupService.SeedViewerPermissionsAsync` writes stored the
  literal string `"Viewer"` as `RoleId` — but every place this app actually checks permissions
  (`PermissionService.HasPermissionAsync`, and `GetGrantedAsync` added in 0.95.0 for the nav) resolves
  a role *name* to its real database Id first, then compares `Permission.RoleId` against that Id, not
  against the name. `IdentityRole.Id` is always a freshly generated GUID, never equal to the role's
  own name, so a seeded row reading `RoleId = "Viewer"` could never match anything, for any resource,
  from the day this method was first written. Invisible in practice because the only role this
  deployment (or most single-operator deployments) ever really exercises is Administrator, which
  bypasses the `Permission` table entirely via its own implicit-everything short-circuit — and this
  app has no role-management UI yet to even assign a second user to Viewer and notice.

  A previous pass (0.94.0) fixed a resource-naming mismatch in the same method and reported it as
  resolved; that fix was real but sat directly on top of this deeper bug, so it made no observable
  difference — corrected in place in 0.94.0's own entry above rather than left standing. Fixed by
  seeding the role's real `Id` instead of its name. A migration repairs any rows a completed setup
  already seeded with the broken value, rather than leaving them dead in place.

  Caught while building 0.95.0's permission-aware nav, by re-deriving this from the exact lookup code
  the nav now reads through rather than trusting this method's own prior doc comment — the same
  discipline that found the 0.94.0 issue in the first place. Two new tests exercise the real
  seed-then-resolve pipeline end to end (not just "is the stored value the right type"), and were
  confirmed to fail against the pre-fix code before confirming they pass against the fix.

Web-only — no node change.

## [0.96.0] - 2026-08-18

### Added

- **A new HTTPS column on the Cameras list** — 🔒 when a camera's device service URL uses
  `https://`, blank otherwise. Read directly from the stored URL rather than a separate flag: the
  scheme is already the single source of truth for whether a camera is reached over HTTPS (both
  `AddAsync` and `UpdateAsync` validate `DeviceServiceUri` as an absolute URI before saving), so a
  second, independently-settable indicator could only ever drift from what the URL actually says.

Web-only, no node change.

## [0.95.0] - 2026-08-18

### Changed

- **Nodes moved out of the Admin dropdown into its own top-level nav button**, next to Cameras —
  fleet monitoring is something an operator checks routinely, not an occasional administrative task,
  matching why Logs got the same promotion a release ago. Still gated by `Nodes.Edit`, unchanged.
- **The navbar is now permission-aware: every button is hidden unless the signed-in user actually
  holds the permission the page behind it requires**, rather than always showing the full menu and
  relying on the destination page to redirect or 403. Each button's check mirrors its target page's
  own `[Authorize]` policy exactly (Live/Cameras → `Cameras.View`, Playback → `Playback.View`, and so
  on), so the nav can never promise access a click would then refuse. The Logs and Settings buttons
  each check an OR of their tabs' two distinct policies (`Logs.View`/`SystemLogs.View`,
  `Settings.Edit`/`Backups.Edit`), so a user holding only one still sees the button rather than losing
  it because they lack the other tab's permission; the Admin dropdown itself now only appears when at
  least one item inside it would.

### Added

- **`IPermissionService.GetGrantedAsync`** — every `(Resource, Action)` pair a signed-in user's roles
  grant, resolved in at most one database round trip regardless of how many of them get checked
  afterward. This is what makes the permission-aware navbar affordable: it renders on every single
  page in the app, including the anonymous Login page, and a naive per-button
  `IAuthorizationService.AuthorizeAsync` call would have meant several extra database round trips
  *per button* on every request (`HasPermissionAsync` alone does up to four). Role names are read
  straight off the signed-in user's own claims — already on the authentication cookie via
  `AddRoles<IdentityRole>()` — rather than re-queried, so an Administrator (the common case) costs
  nothing at all beyond that claims read.

### Known limitations

- **The Viewer role's seeded permissions cover only a handful of resources** — most nav buttons
  (Live, Cameras, Playback, Exports, Views, Nodes) have no seeded Viewer permission at all yet, a gap
  this pass surfaced rather than caused (nothing here changes what Viewer is seeded with). A Viewer
  will see a much shorter navbar than an Administrator until RBAC's remaining gap (per-camera
  `CameraAccess` enforcement and a real permission-management UI, both already on the roadmap) closes.

Web-only, no node change.

## [0.94.0] - 2026-08-18

### Changed

- **Logs moved out of the Admin dropdown into their own top-level nav button**, with Audit Logs and
  System Logs as two tabs of one `Logs` page rather than two separate, easy-to-lose entries. Each tab
  is still a real page with its own route and its own permission gate (`Logs.View` /
  `SystemLogs.View`) — the tab strip is plain navigation between them, not a client-side panel swap,
  so neither page's existing filters or pagination needed to change.
- **Settings is now one page with tabs, sorted alphabetically: Backups, Branding, Cameras, Events,
  Logs, Nodes, Recording, Storage and Retention.** This replaces five separate Admin dropdown entries
  (Settings, Branding, Event Colors, Backups, plus the settings half of Node Builds' own page) with
  one place, and reorganizes every existing global setting into the category it actually belongs to
  rather than the order it happened to be added in. Each tab is still its own page with its own
  `[Authorize]` — Backups keeps its own `Backups.Edit` gate rather than being folded into
  `Settings.Edit`, and Node Builds' approve/reject queue stays at its own `Admin/NodeBuilds` route
  (gated by `Nodes.Edit`) rather than being merged in; the Nodes tab shows a live pending-count
  summary and a link to it instead. The Admin dropdown itself shrinks from nine items to three
  (Nodes, Settings, Plugins).
- Old routes (`Admin/Branding`, `Admin/EventColors`, `Admin/Backup`, `Admin/Logs`,
  `Admin/SystemLogs`) now redirect to their new home, same pattern this app already used for
  `Admin/Retention`'s own stub — an old bookmark still lands somewhere real.

### Added

- **Audit log retention** (`Admin → Settings → Logs`), new — the audit trail had no sweep at all
  before this; every row was kept forever. The default stays **0 (forever)**, not the application
  log's 14-day default: an upgrade must never start silently deleting a compliance record, so a
  bounded window is opt-in only.
- **System log path is now shown** on the same tab (read-only) alongside its retention. It can't be
  a normal editable field: the application log is written before this app's settings database is
  reachable, specifically so a startup failure still gets recorded to disk. Change it via the new
  `Logs:Path` configuration key (or the `LarisVMS__Logs__Path` environment variable) and restart.

### Fixed

- **The Viewer role's seeded audit-log permission never actually granted audit-log access.** Initial
  setup seeded `AuditLog.View`, but the audit log page has always required the policy
  `Logs.View` — a resource-name mismatch that predates this pass.

  **Correction (see 0.97.0): this fix was real but incomplete** — a deeper bug directly underneath it
  meant the resource-name fix alone still didn't grant Viewer anything. Every seeded row's `RoleId`
  held the literal string `"Viewer"` rather than the role's actual database Id, so no seeded
  permission — for *any* resource, not just this one — has ever matched what
  `PermissionService.HasPermissionAsync`/`GetGrantedAsync` actually check it against. The real fix
  landed two releases later; see 0.97.0 for the full explanation and the migration that repairs
  already-seeded rows.

Web-only — no node change.

## [0.93.0] - 2026-08-17

### Added

- **A camera's device service URL is now editable after it's added** (`Cameras → Edit`), not just at
  creation. There was previously no path to it at all — the field rendered disabled with no
  corresponding parameter on the update call — so switching a camera between `http://` and
  `https://`, or following an IP change, meant deleting and re-adding the camera and losing its
  history association. Saving a real change re-derives `Host`/port from the new URL exactly as adding
  a camera already does, and triggers an automatic re-probe, since the device's own capability report
  can otherwise go stale against the new address; a probe failure is reported the same way an
  ordinary failed probe already is, without blocking the address change itself from saving. Leaving
  the field as submitted (the normal case) touches nothing, matching how credential fields already
  behave on this form.

### Fixed

- **The Dahua/Amcrest plugin's CGI event connection still validated the camera's TLS certificate**,
  the one camera-facing HTTP client in this app that did. Both ONVIF clients (web and node) and the
  node's own reporting connection already disable validation deliberately, since a LAN camera reached
  over HTTPS almost universally presents a self-signed certificate with no CA behind it — so an
  HTTPS camera's ONVIF traffic worked while its event stream silently never connected. Brought in
  line with every other camera-facing client in the app.

**LarisVMS.Node change — `install-node.ps1` re-run needed on every recorder.**

## [0.92.0] - 2026-08-17

### Added

- **Cameras are re-probed automatically once a day** at a time you choose
  (`Admin → Settings → Cameras`, default 03:00 server local time, and switchable off). This picks up
  a camera that has gained, lost, or re-encoded a stream without anyone remembering to press
  Re-probe. Per-stream enable/disable and custom names are already carried across a probe and streams
  are matched by profile token, so re-probing an unchanged camera changes nothing.

  Cameras are probed **one at a time, not in parallel** — probing opens real ONVIF conversations with
  a device, and a fleet-wide burst of them is exactly what makes inexpensive cameras drop their other
  connections, including the RTSP session being recorded. One unreachable camera doesn't end the pass
  for the cameras after it; every run writes a single audit entry with the successes and any
  failures, attributed to the system rather than a user.

  The schedule is evaluated on a one-minute tick rather than by sleeping until the next occurrence,
  so changing the time takes effect immediately instead of after a restart. A run missed because the
  application was down is skipped rather than queued — the next day's run does the same work.

## [0.91.0] - 2026-08-17

### Added

- **Plugins page** (`Admin → Plugins`) listing every camera integration this build ships, each with
  its own version, and — more usefully — which cameras are actually using it. Providers are compiled
  in and matched automatically from the make and model a camera reports while probing, so there is
  nothing to install or enable; the page exists because answering "what's running, at what version,
  on which cameras" previously meant reading the source. It also surfaces a case that was silent
  until now: a camera whose stored integration key matches no provider in this build (a downgrade, or
  a provider removed later) keeps recording but gets no vendor events, which the registry tolerates
  deliberately rather than failing node config generation over.
- **Each integration provider now carries its own version**, starting at 1.0.0, bumped when that
  provider's own behavior changes. Deliberately independent of the application version: a provider
  changes when its vendor's API or event-code table does, on nobody else's schedule.
- **Event tag position is configurable** (`Admin → Settings → Live view`). The motion badge and
  object-detection badges can sit in any corner of a live tile, defaulting to top left as before.
  The bottom corners already hold other controls — hover controls bottom right, and a playback-mode
  cell's mini timeline bottom left — so badges placed there are lifted clear of that row rather than
  overlapping it. The stored value is allowlisted on the way in and out, since it drives positioning
  classes in the browser.

## [0.90.0] - 2026-08-17

### Fixed

- **Every deploy deleted the entire application log history**, which is the actual reason
  `Admin → System Logs` only ever offered the current day. Retention was never the problem: the
  sweep has always kept 14 days and the viewer has always listed every file it finds. `deploy.ps1`
  mirrors the publish output onto the site with `robocopy /MIR`, which deletes anything at the
  destination that isn't in the source, and while `data-protection-keys`, `recordings`, `spool`, and
  `exports` were excluded, `logs` was not — so every deploy mirrored the log directory away, and on a
  day with several deploys nothing older than the last one could survive. `logs` is now excluded too.
  Making retention configurable (below) would not have fixed this on its own.

### Added

- **Application log retention is now configurable** (`Admin → Settings → Logs`), replacing the
  hardcoded 14 days. **0 keeps logs forever.** The value is re-read on every sweep rather than
  captured at startup, so a change takes effect on the next cycle without an app pool recycle. A
  recorder node still sweeps its own logs on its own fixed schedule — reaching those needs a
  `NodeConfig` field and a node release, so it is deliberately not part of this change.
- **View pickers now show each view's camera count** — `House (6)` rather than `House` — on both
  Live (`Views/Play`) and Playback. Counts cells, matching the existing Cameras column on
  `Views/Index` so the two can't disagree for a view that places one camera in several cells.

## [0.89.2] - 2026-08-17

### Fixed

- **Playback squeezed its video grid to a sliver on a phone in landscape** — the same symptom
  0.89.1 fixed on Views/Play, but a different cause, which is why that fix didn't reach here.
  `#pbLayout` is a fixed-height flex column: the toolbar and the timeline strip take their natural
  height and the video grid takes whatever remains. At desktop height that leaves plenty; at roughly
  390px it does not, because the strip's own chrome — two labels, the current-time readout, two 30px
  canvases, and a usage hint that wraps to four lines on a narrow screen — plus a toolbar that wraps
  claimed nearly the whole column. On a short viewport the hint and the two labels are now hidden and
  the vertical padding tightened, which hands the grid back roughly 100px without touching the grid
  itself. Both timeline canvases and the clickable current-time readout stay, since those are
  controls rather than orientation text; the hint was already hidden by the same reasoning when a
  timeline is moved into a fullscreened tile.

The Views editor is deliberately unchanged: GridStack's fixed 60px `cellHeight` there defines what a
saved layout's row units mean, so scaling rows on a short viewport would make the authoring surface
disagree with what it produces.

## [0.89.1] - 2026-08-17

### Fixed

- **A phone held sideways squeezed every camera into a thin horizontal band.** Landscape makes a
  phone wider than the 768px phone breakpoint, so the saved view rendered through the desktop grid —
  12 columns compressed into roughly 840px (about 70px each) while row height stayed pinned at the
  editor's fixed 60px. Column width tracks the viewport; row height did not, so every cell came out
  far taller and narrower than the box its layout was designed in, and `object-fit: contain`
  letterboxed the video into a strip with black above and below it. A short viewport now scales row
  height so the entire view fits the window at once — measured from the grid's own live position, so
  it stays correct in kiosk mode where the nav and toolbar are hidden. Resizing and rotating re-fit
  without rebuilding the tiles, since a rebuild would drop and restart every camera's stream.
  `hideOnPhone` deliberately still applies only to the derived portrait stack: landscape replays the
  real saved layout rather than inventing one, so there is nothing for a phone-specific flag to mean
  there.
- **Hovering a View cell's mini-timeline showed no preview thumbnail**, though the Playback page's
  timeline had shown them since 0.60.0. Both draw from the same `timeline.js`, which only registers
  the hover-preview behavior when a `getThumbnailUrl` option is supplied — the mini-timeline passed
  bucket and scrub callbacks but never that one, so the feature was simply never switched on. A View
  cell is bound to a single camera for its whole life, so it needs none of the primary-selection
  indirection the Playback page's version carries.

## [0.89.0] - 2026-08-16

### Added

- **A volume slider on every tile that has audio, live and playback alike.** Both surfaces had only a
  mute button — all-or-nothing at whatever level the camera happens to send, which on a wall of tiles
  means the loudest camera wins. Each tile now carries its own slider beside that button, independent
  of every other tile. The slider shows *effective* volume: dragging it above zero unmutes, dragging
  it to zero mutes, and the mute button restores the last level that was set, which is how every
  video player on the web behaves and so needs no explanation. Tiles still **always start muted** and
  an unmute is never persisted across a page load — reopening Live or Playback starts silent. A
  camera with no audio track gets neither control rather than a slider that can do nothing.
- **Audio codec and sample rate on the health dashboard** (new `Audio` column, e.g. `aac 16 kHz`),
  alongside the fps and bitrate already there. Read from ffmpeg's own stream summary when the
  recorder opens the stream — the same source, and for the same reason, as the video resolution and
  codec already reported there: it is what actually arrives on the wire, rather than ONVIF's
  advertised `AudioEncoderConfiguration`. (`CameraStream.AudioCodec` had existed as a column since
  the first migration and was never populated by anything; this is what fills it, plus a new
  `AudioSampleRateHz`.) Unlike fps/bitrate the column is not blanked when a node stops reporting:
  the codec a camera sends is a property of the stream, not a live measurement, so the last known
  value stays true while a node is down. Sortable by sample rate.

### Fixed

- **Unmuting a live View cell was silently undone moments later.** A live tile reloads the same
  `<video>` element on every reconnect — and again when toggled into playback mode — and the HTML
  load algorithm resets `muted` back to the element's `muted` attribute, which is set. Playback's
  tiles had reapplied the user's choice since 0.59.0, but the View cells never did: the tile went
  quiet again on the next reconnect while its own button kept showing 🔊, claiming otherwise. Both
  surfaces now share one implementation (`audio-controls.js`), which reapplies mute state and volume
  on every source load.

**LarisVMS.Node change — `install-node.ps1` re-run needed on every recorder** (the audio stream
summary is parsed on the node). Older nodes keep reporting normally; their reports simply carry no
audio fields, so the dashboard's Audio column stays blank for their cameras until they update.

## [0.88.1] - 2026-08-16

### Changed

- **The recorder's live fan-out no longer allocates when nobody is watching.** ffmpeg's stdout is now
  read through `System.IO.Pipelines` instead of `Stream.ReadAsync` into an array of our own. The pipe
  owns pooled buffers and its `AdvanceTo(consumed, examined)` contract expresses precisely the problem
  here — "I examined all of this but could only consume through the last whole fragment" — which is
  the buffering and compaction the drain loop had been doing by hand, and doing by hand meant copying
  every byte ffmpeg produced into a second buffer first. Fragment boundaries are now found in the
  pipe's own buffers, and nothing is copied unless a fragment is actually being handed to a viewer.
  Measured over one camera-hour (108,000 fragments, 4.1 GB): with a viewer, 18% less CPU and unchanged
  allocation (handing a viewer an array has to allocate one); with no viewer, **83% less CPU and
  allocation down from 4.1 GB to zero**. A 24/7 recorder has no viewer on most cameras most of the
  time, so that is the case that dominates.
- **Box-type parsing no longer allocates a string per box.** The scanner decoded each four-byte box
  type to a `string` to compare it, on a path that sees every byte ffmpeg emits, for every camera,
  forever. Types are now compared as big-endian `uint32`. A test asserts the scan allocates exactly
  zero bytes.
- The scanner also gained `ReadOnlySequence<byte>` overloads, since pooled pipe buffers can split a
  box header across two segments. Covered by tests that split at every offset through a header,
  including the 64-bit `largesize` form, and assert the sequence and span paths agree exactly — a
  disagreement would move a fragment boundary depending on how the pipe happened to segment.

**Node change — `install-node.ps1` re-run needed on every recorder.**

## [0.88.0] - 2026-08-16

### Added

- **Event colors are now admin-configurable (`Admin → Event Colors`).** Every color the timelines and
  live badges draw with — plain motion, recorded coverage, and each detected object class — can be
  set from one page, with a swatch picker and a hex field per entry. Leaving a field blank stores
  nothing rather than storing today's default, so an untouched deployment keeps tracking the built-in
  palette including any later change to it. A user-configured event tag rule's own color still
  outranks everything, unchanged.
- **Three more object classes, wired through both detection sources** so they work on any camera that
  reports them: **Animal** 🐾, **Object appeared** 🧳 (something left behind — the abandoned-baggage
  case), and **Object missing** ❓ (something that was there and is gone). Recognized over ONVIF
  (`AnimalDetector`, `PetDetector`, `AbandonedObject`, `ObjectAppearance`, `MissingObject`,
  `ObjectRemoval`) and over the Dahua/Amcrest integration (`AnimalDetection`, `SmartMotionAnimal`,
  `PetDetection`, `LeftDetection`, `AbandonedObjectDetection`, `TakenAwayDetection`,
  `MissingObjectDetection`). None of this deployment's cameras are known to emit them — they cost
  nothing until one does.

- **Several object classes seen at once are all shown, on both surfaces.** A camera watching a person
  walk a dog past a parked car is reporting three classes on one stream. The live tile already
  rendered a badge per class, but the badge strip couldn't wrap — on a small cell in a dense grid the
  third and later badges ran off the edge. It now wraps within the cell. On the timeline, a bucket
  overlapping several classes previously painted only the earliest-starting one and silently dropped
  the rest; it now splits into equal horizontal bands, one per class. A bucket with a single color
  draws exactly as before.
- **Detection badge text now picks black or white by the badge's own luminance.** Badge colors became
  admin-configurable in this release, so the previously hardcoded black text could no longer be
  assumed legible — a dark custom color would have produced an unreadable badge.

### Changed

- **Dahua's `LeftDetection` and `TakenAwayDetection` now map to Object appeared / Object missing
  rather than both collapsing into the generic Object class**, which had been throwing away the
  distinction between something being left behind and something being taken.
- Plain motion — movement the camera could not classify — is now marked 🌀 on live tiles, rather than
  a bare dot. The swirl is deliberately not an object glyph, since the whole point of that badge is
  that no object class was attached.

## [0.87.5] - 2026-08-16

### Fixed

- **Live tiles failed to decode with `CHUNK_DEMUXER_ERROR_APPEND_FAILED` ("Failed to prepare video
  sample for decode"), most often right after a reconnect.** The node fanned raw 64 KB reads of
  ffmpeg's pipe out to live viewers rather than complete fMP4 fragments, so a viewer joining
  mid-stream got the cached init segment followed by bytes starting partway through a `moof` or
  `mdat`. The decoder rejects a truncated box outright, which is why a fresh session died on its
  first media chunk. Whether it happened at all depended on where the next read boundary landed,
  making it look intermittent. The same flaw made back-pressure destructive: the per-viewer queue
  drops its oldest entry when a slow client falls behind, punching a hole through the middle of a box
  instead of skipping cleanly. Viewers are now fed whole moof+mdat fragments, so both a late join and
  a dropped fragment land on a boundary MSE accepts. **Node change — `install-node.ps1` re-run needed
  on every recorder.**
- **Live-view sessions leaked their `<video>` event listeners.** The element outlives every session
  attached to it (each reconnect builds a fresh `MediaSource` on the same element), so every
  reconnect added another full set of `error`/`playing`/`stalled`/`waiting` handlers, none removed.
  All of them kept firing, each holding its dead session's closure alive — one decode error printed
  once per leaked listener, each reporting its own stale fragment count, which read like several
  concurrent sessions and buried the real fault. This got steadily worse the longer a page stayed
  open, since the count grows with every reconnect.

### Changed

- Cameras using a vendor integration are now marked 🧩 rather than 🔌.
- Navbar icons: Playback is ▶️ (was ⏪) and Exports is 🎬 (was ⬇️). Playback's toolbar "Export…"
  button picked up 🎬 to match, and the Exports page's Download button now carries ⬇️ — freed up by
  the navbar change, and now meaning specifically "download" rather than "exports". The per-cell
  playback toggle keeps ⏱ — ▶ is already the play/pause control inside those same cells, so reusing
  it would sit two near-identical buttons side by side.

## [0.87.4] - 2026-08-16

### Changed

- The **Other** detection class now shows 📦 instead of 🔎 on live-tile badges. The badge names what
  the camera saw, and a magnifying glass reads as an action (search) rather than a thing. Display
  only — no node re-run needed, since nodes report the detection class and never the emoji.

## [0.87.3] - 2026-08-16

### Fixed

- **A dropped smart-event connection could leave a detection span open forever.** Dahua's `attach`
  endpoint only delivers events from the moment it subscribes, so a `Stop` sent while the feed was
  down is gone for good — and the 15-second checkpoint loop kept extending the still-open span's end
  time indefinitely. A camera reboot or network blip while someone was in frame would read on the
  timeline as a person standing there for hours. Open spans are now closed at the point the feed died,
  the same contract shutdown already had; a fresh `Start` after reconnect opens a new span, leaving an
  honest gap where the feed was down. **Node change — `install-node.ps1` re-run needed on every
  recorder.**

## [0.87.2] - 2026-08-16

### Added

- **`probe-dahua-events.ps1 -UsePluginCodes`**, which subscribes with the exact filtered code list the
  plugin sends instead of `[All]`. Probing with `[All]` proves which codes a camera *can* emit, but the
  plugin asks for a narrow `codes=[...]` list — firmware that mishandles a long filter would go silent
  in production while an `[All]` probe still looked perfect. Verified against an Amcrest
  `IP8M-DLB2998EW-AI`: `SmartMotionHuman` arrives as clean Start/Stop pairs and maps to Person.

## [0.87.1] - 2026-08-16

### Fixed

- **`probe-dahua-events.ps1` couldn't run at all.** Its camera-address parameter was named `-Host`,
  but `$Host` is a reserved PowerShell automatic variable (the console host object), so parameter
  binding failed immediately with *"Cannot overwrite variable Host because it is read-only or
  constant."* Renamed to `-CameraHost`, with `-Address`/`-IP` aliases. Script only — no application
  change.

## [0.87.0] - 2026-08-16

### Added

- **Camera integration plugins** — an extensibility point for everything ONVIF can't express. A
  provider declares which makes and models it handles; probing matches each camera automatically
  from the make/model it already reports, stores the result, and the recorder node starts that
  vendor's session alongside its ONVIF one. Cameras using one are marked 🧩 on the Cameras list and
  explain themselves on their Edit page. Nothing to configure.
  - **Providers are compiled in and listed in one registry, not loaded from external assemblies.**
    Recorder nodes ship as a single self-contained executable that auto-updates by file swap, so a
    drop-in plugin folder would need a second distribution and version-matching channel, and would
    mean loading arbitrary code onto recorder machines. Adding a vendor is one descriptor plus one
    session, each registered in exactly one place.
  - Detection re-runs on every probe, so a camera picks up (or loses) an integration when its
    reported identity changes — including hardware already in the fleet that a later release starts
    recognizing. An unknown key (config from a newer server, or a provider since removed) degrades to
    "no integration" rather than failing config generation or stopping a camera recording.
- **First provider: Dahua / Amcrest smart events.** Reads person and vehicle detections from the
  camera's own Smart Motion Detection over Dahua's CGI event API, feeding the exact same detection
  spans, badges, timeline colors and Motion-mode retention that 0.85.0 built for ONVIF.
  - **This is what makes 0.85.0 actually work on this fleet.** Those cameras classify objects onboard
    with SMD enabled, but publish nothing object-shaped over ONVIF — confirmed against 21,747
    recorded events containing only motion/tamper/monitoring topics, and a metadata track carrying a
    motion-cell grid rather than object geometry. The classification was always happening; it just
    had no route into the app until now.
  - Subscribes only to codes the app can act on, not `[All]` — which would also stream every
    heartbeat, storage and config-change event the camera produces. Plain motion is deliberately
    excluded, since ONVIF already delivers it and taking both would double-report one event.

### Known limitations

- **The vendor event codes are unverified against real hardware.** The code table comes from Dahua's
  documentation and covers several firmware generations' spellings, but this deployment's exact
  firmware hasn't been observed emitting them. An unrecognized code is ignored safely (no misbehavior,
  just no badge), and adding a spelling is a one-line change. `probe-dahua-events.ps1` prints exactly
  what a camera really sends.

**LarisVMS.Node change — `install-node.ps1` re-run needed on every recorder.**

## [0.86.1] - 2026-08-16

### Fixed

- **`probe-metadata-track.ps1` reported a false positive.** Its check accepted a match on
  `MetadataStream|VideoAnalytics` — elements present in *any* ONVIF metadata stream — and on that
  basis declared bounding boxes feasible. Run against a real camera it did exactly that, while the
  captured payload contained no object geometry at all. It now looks for the elements that actually
  carry geometry (`tt:Object` / `BoundingBox`) across the whole capture rather than the printed
  preview, distinguishes "has geometry but no class labels" from "has both", calls out a
  motion-cell-only stream for what it is, and prints an element census of what the camera really
  sent.

### Research

- **Bounding-box spike result: not achievable on the current camera fleet.** Probing a Driveway
  camera (Dahua/Amcrest family, the same RTSP path all six units use) established:
  - A metadata track **does** exist (stream index 2) and ffmpeg **can** demux it cleanly with
    `-map 0:d` — 6963 bytes of valid ONVIF XML in 15 seconds, no repeat of the muxer failure that
    made the recording pipeline stop mapping data streams in the first place.
  - But it carries a **22×18 `MotionInCells` grid** plus the same `CellMotionDetector`/`MotionAlarm`
    events already ingested over PullPoint — **zero** `tt:Object`, `BoundingBox`, or `ClassCandidate`
    elements. The `Transformation` confirms it (`Scale x=0.090909` = 2/22, `y=-0.111111` = 2/18):
    the coordinate system maps to that cell grid, not to object rectangles.
  - So the blocker is the hardware, not the plumbing: these cameras do cell-motion analytics, not
    onboard object detection. The object-detection *events* shipped in 0.85.0 remain the available
    object signal, and per-object boxes would need a camera model with real onboard object analytics.

## [0.86.0] - 2026-08-16

### Fixed

- **The Playback page's timelines disappeared while a tile was fullscreened**, leaving no way to
  scrub the very footage being watched full-screen. This wasn't a CSS bug: the timelines are
  page-level elements outside the fullscreened tile, and the browser's Fullscreen API genuinely
  renders only the fullscreened element's own subtree. The real timeline element is now moved into
  the tile while fullscreen is active and moved back on exit — one canvas, one timeline instance, no
  second copy to keep in sync — overlaid across the bottom of the video on a translucent backdrop.
  (This is what the earlier View-cell fullscreen fix in 0.81.2 explicitly did *not* cover, since a
  View cell's mini-timeline already lives inside the element being fullscreened.)

### Added

- **`probe-metadata-track.ps1`** — a read-only research script that answers the one open question
  gating bounding-box overlays: does a camera actually expose an ONVIF metadata track over RTSP, and
  can ffmpeg demux it? It opens its own short-lived connection, entirely separate from the recording
  pipeline, and redacts credentials from everything it prints. Run it against a real camera before
  any bounding-box work is designed — the recording pipeline has a confirmed production failure on
  record from mapping a data stream, which is why it maps only video and audio today.

## [0.85.0] - 2026-08-16

### Added

- **Object-detection events.** Cameras whose own onboard analytics classify what they see
  (person / vehicle / face) now surface that instead of only generic motion: a labelled badge on the
  live tile (🚶 Person, 🚗 Vehicle, 🙂 Face), and a distinctly coloured span on the Playback timeline,
  clearly separate from motion green and recorded blue.
  - Runs entirely on the ONVIF PullPoint channel, `MotionSpan` storage and reporting path already in
    place — no new wire format, no new polling loop on the node, and unrecognized topics keep flowing
    to the raw event log exactly as before.
  - Vendor topic names vary a lot (Hikvision, Dahua, Amcrest and Axis all differ, and firmware
    revisions differ within a vendor), so matching is by substring across a table of the known
    spellings. Anything unmatched is simply not classified.
  - Each detected class tracks its own span independently, so a person leaving doesn't close a
    vehicle's span, and neither disturbs plain motion detection.

### Changed

- **A detection now counts toward Motion-mode recording**, as one more term in the existing OR chain
  alongside motion zones, camera motion events and driving event-tag rules. Because it's an OR term
  it can only ever *keep* a segment that would otherwise have been discarded — never discard one that
  would have been kept. That direction also fixes a real pre-existing gap: a camera whose firmware
  emits object-detection topics but no motion ones had nothing to satisfy Motion mode's keep
  condition, and so discarded everything it recorded.

### Known limitations

- **These are not bounding boxes.** ONVIF's rule-engine topics report *that* an object class was
  seen, not where it was in frame. Per-frame coordinates travel on a separate metadata RTP track this
  app doesn't consume — see the earlier note on why that needs verifying against real hardware before
  it can be designed, since the recording pipeline's own attempt to map that track has previously
  failed outright on a real camera.
- **Unverified against a camera that actually emits these topics.** The topic table is built from
  vendor documentation and this codebase's existing ONVIF findings; a real device may well use a
  spelling not listed yet, in which case it simply won't classify (no misbehavior, just no badge).

**LarisVMS.Node change — `install-node.ps1` re-run needed on every recorder.**

## [0.84.0] - 2026-08-16

### Added

- **Branding page (`Admin → Branding`)** — set the application name, a primary color (buttons and
  links), a navbar color, a logo, and a font, applied across every page.
  - **The sign-in page is branded too.** It previously rendered in the Identity package's own
    standalone layout; it now uses this app's layout, so the logo, name and colors appear there
    without scaffolding a local copy of every Identity page.
  - **The logo is stored as a data URI in the setting row**, not as an uploaded file — no upload
    plumbing, no filesystem write permissions, and nothing that can be orphaned by a redeploy. The
    image is converted client-side and size-capped both there and on save.
  - Anything left blank falls back to the built-in default, so a fresh install looks exactly as it
    did before this existed, and you can brand only the parts you care about.
- **Emoji on the main action buttons** (💾 Save, 🗑️ Delete, 🔍 Re-probe, ➕ Add), continuing the
  convention the navbar foralready uses. Each is `aria-hidden` so screen readers announce the label
  alone.

### Changed

- **Branding moved from the one-time Setup wizard into ordinary editable settings.** It used to be
  written once to `setup-generated.json` and never editable again from the UI. It now lives in the
  same `Setting` store every other admin-editable value uses — no schema change needed. The wizard's
  original value still applies as the fallback until something is saved on the new page, so existing
  installs keep their current branding with nothing to migrate.

### Security

- **Branding values are allowlist-validated before they can be stored**, because they're interpolated
  into a `<style>` block and an `<img src>` on every page: colors must be hex literals (no
  `rgb()`/`var()`/named colors, which would let arbitrary CSS ride along), fonts are chosen by key
  from a fixed table so the stylesheet only ever receives this codebase's own strings, and a logo must
  be a base64 image data URI that actually decodes. SVG is rejected specifically because an SVG file
  can carry script.

## [0.83.0] - 2026-08-16

### Added

- **Multi-sensor camera support** — a quad-lens (or any multi-sensor) device can now be split into
  one camera per lens, each recording independently and placeable in a view on its own.
  - ONVIF probing now reads each profile's `VideoSourceToken` — which sensor it draws from. It's the
    only thing in a `GetProfiles` response that tells one lens apart from another, and it wasn't
    being captured at all before.
  - When a probe finds more than one sensor, the camera's Edit page offers to split it. The existing
    camera becomes channel 1 and keeps its recordings; a sibling camera is created for each remaining
    channel, sharing the device's address and credentials. From there they're ordinary independent
    cameras — which is why recording, views, live, playback and export needed no changes at all to
    support this.
  - Per-channel cameras are marked with 🔀 on the Cameras list and on their own Edit page, so a
    channel camera is recognizable among its siblings.
  - Splitting is safe to run again: it matches existing rows by channel rather than creating
    duplicates, and it won't re-suffix a name that's already been split.

### Fixed

- **On a multi-lens device, a camera could end up recording the wrong lens.** The profile ranker
  picks Main/Sub/Third by name and resolution and knows nothing about channels, so given a device
  reporting every lens's profiles at once it could hand a camera another lens's stream as its "Main".
  A camera pinned to a channel now only ever considers that channel's own profiles. Cameras with no
  channel pinned — every single-sensor camera, and everything that existed before this release —
  behave exactly as they did before.

## [0.82.0] - 2026-08-16

### Added

- **Comprehensive audit logging** — every entry captures the acting user's IP address (already
  correct without proxy-header handling, since this app is deployed behind IIS in-process, which
  passes the real client connection straight through). New entries:
  - **`Camera.View`** — one per live stream actually opened, logged at the `/live/{cameraId}`
    WebSocket handshake. Since the flat all-cameras grid was removed in 0.80.0, that endpoint is the
    single chokepoint every live-viewing path goes through, so this covers all of them from one place.
  - **`View.Watch`** and **`Playback.View`** — each names the view and every camera in it, resolved
    server-side from the view's own saved layout rather than trusting anything the client sends.
    Playback needed a small new endpoint (`POST /api/playback/view-opened`): that page resolves the
    selected view entirely in the browser, so there was no existing server request that knew which
    cameras were being reviewed. It's fire-and-forget — an audit write can never block playback from
    starting.
  - **`Export.Download`** — previously untracked despite being the most compliance-sensitive step in
    the whole export flow (completed data leaving the system, not just viewing). Logged when the
    download starts, not when it finishes, since an abandoned transfer still means footage moved.
  - **`NodeBuild.Approve`** / **`NodeBuild.Reject`** — a fleet-wide action (every node picks the
    build up on its next heartbeat) that had no audit coverage at all.

### Changed

- **Change entries now record what actually changed, as `old → new` values**, instead of only that a
  save happened. Applies to global settings (which previously logged no detail whatsoever, so the
  entry couldn't answer "which of these eight fields did they touch?"), camera edits, node edits, and
  backup settings. Unchanged fields are omitted, so an entry lists only real edits.
  - Two gaps closed along the way: a camera's and a node's per-scope **retention override** used to
    ride along silently inside the generic `Camera.Update`/`Node.Update` entry with nothing to
    indicate it had been changed at all.
  - **Secrets are never written to the log.** Camera credentials and the node registration key report
    only that they changed, never a value — the audit log is readable by anyone with `Logs.View`, so
    an audit trail that leaked the credential it was recording would be worse than none. Camera
    credentials specifically are reported as changed-or-not without any before/after comparison,
    because the camera read path deliberately never loads them in the first place.

## [0.81.2] - 2026-08-16

### Fixed

- **A View cell in playback mode lost its mini-timeline when fullscreened, and didn't get it back
  after exiting** (only toggling back to live and into playback again restored it). Two separate
  causes, both fixed:
  - CSS explicitly hid the mini-timeline and playback toggle whenever a tile was fullscreened. That
    rule was ported from the removed flat Live grid, where it made sense ("fullscreen shows only mute
    + exit") — but it doesn't here: a View cell's mini-timeline lives *inside* the element being
    fullscreened, so it can render there. Scrubbing recorded footage on a single fullscreened camera
    now works, which is arguably the most useful thing to do in fullscreen. (This is unrelated to the
    Playback page's own timelines, which are page-level elements genuinely outside the fullscreened
    tile — still a separate, known limitation.)
  - The canvas bitmap was being destroyed while hidden. Entering/exiting fullscreen fires a window
    `resize`, which measured the then-hidden canvas at 0×0 and clamped its bitmap to 1×1 while the
    draw call bailed on the zero size — and nothing re-measured it once it became visible again, so
    it returned as a 1×1 bitmap stretched across its full width. Timelines now self-heal on any size
    change (including hidden → visible) via a `ResizeObserver`, which the window `resize` listener
    alone could never cover since the element's own box changes without the window's doing so.

Web-only, no node change.

## [0.81.1] - 2026-08-15

### Fixed

- **0.81.0's hard-resync fallback made the stutter/reconnect loop worse instead of fixing it, and
  only a full page refresh cleared it.** The new "drift too large to catch up" branch corrected by
  seeking to `buffered.start()` — which is *behind* `currentTime` whenever `currentTime` is already
  inside the buffered range, exactly the situation it fires in. Confirmed from real logs: a tile
  74.8s behind seeked from 2724.8 back to 2721.8, leaving it 77.7s behind, then re-fired every tick
  forever. This is the third seek-based correction to regress here, so the fallback no longer seeks
  at all: a session that far behind is unrecoverable, so it now ends and lets the existing retry
  wrapper build a fresh MediaSource that starts cleanly at the live edge — the one recovery path
  already proven correct.
- **Leaked live-view drift timers could seek a `<video>` element that no longer belonged to them.**
  Toggling a cell into playback mode attaches a *different* player's MediaSource to the same element,
  but a surviving live-session timer kept seeking it — the actual trigger behind "the loop is back
  after enabling playback on the one cell," visible in the logs as several different playback
  positions interleaving from what should have been a single tile. Timers now bail immediately if the
  element is no longer theirs, and can no longer be created at all after their own session has ended
  (previously possible when `sourceopen` fired *after* an early teardown, orphaning an interval
  nothing would ever clear).
- **A View cell's playback mini-timeline was cut off at the bottom and showed no coverage colors.**
  It was 18px tall, but `timeline.js` draws its coverage bar across the top ~55% and the tick marks
  and labels *below* that — so the bottom half was clipped off the canvas (now 30px, matching the
  Playback page's own timelines). It also had no bucket-coverage data wired up at all, so it drew an
  empty gray track: no blue recorded coverage, no green motion. It now uses the same per-camera
  coverage endpoint the Playback page's timeline already uses.

Web-only, no node change.

## [0.81.0] - 2026-08-15

### Fixed

- **Live tiles could repeatedly stutter and reconnect ("the loop is back"), confirmed via a real
  decode error in the browser console.** 0.78.0's drift-correction fix corrected the *direction* of
  the periodic catch-up but still corrected via a hard seek to a point near (not exactly at) the live
  edge — not guaranteed to land on a keyframe boundary. When drift grew unusually large (confirmed
  cause: browsers throttle `setInterval` on a backgrounded/unfocused tab, letting drift build up for
  tens of seconds between checks with nothing actually wrong on the wire), that seek could land off a
  keyframe and throw a real `MediaError` ("Failed to prepare video sample for decode"), tearing the
  session down and reconnecting — the same visible symptom as before, from a different cause. Catch-up
  now speeds up playback (1.5x) instead of seeking at all, so the decoder catches up through real,
  already-decodable frames with zero discontinuity risk; only drift beyond 15 seconds (too large for a
  speed-up to close in reasonable time) still falls back to a hard seek, reusing the same
  already-proven-safe target the stall-recovery path has always used.
- **A View cell's playback-mode mini-timeline (added this session) never actually painted anything** —
  not a CSS/positioning issue, `timeline.js`'s `draw()` was only ever triggered from inside `reload()`,
  which no-ops entirely when no `getBuckets` option is supplied. The mini-timeline is deliberately just
  a scrubbable ruler with no coverage-bucket data, so it never had `getBuckets` — meaning its canvas
  existed, was correctly sized, and simply never received its first paint. `create()` now always
  paints once immediately regardless of whether bucket data is involved.

Web-only, no node change.

## [0.80.0] - 2026-08-15

### Changed

- **Saved Views are now the only live-viewing surface — the flat all-cameras Live grid is gone.**
  `/Live` always redirects to the last-watched view (or the first one), or shows a "No views exist
  yet, click here to create one!" prompt when there are none — there's no more `?all=1` escape hatch
  to a separate grid. If you want to see every camera at once, create a View containing all of them;
  that's the supported way to get the old "all cameras" layout now.
- **The per-tile playback toggle moved to View cells, and now actually works there.** The ⏱ button
  (scrub the last few minutes of a *live* tile without leaving the grid, via a small mini-timeline)
  only ever existed on the flat grid being removed above — Views/Play never had it, single-camera or
  multi-camera view alike. It's been ported over verbatim (same 30-seconds-back default, same
  one-tile-in-playback-mode-at-a-time behavior across the view), so this is a net capability gain for
  every saved view, not a loss from removing the grid.

### Added

- **Dashboard now auto-refreshes every 60 seconds** via a new `GET /api/dashboard` endpoint, instead
  of needing a manual page reload to see updated fps/bitrate/reconnect/status data. Both the
  server-rendered initial page and the AJAX refresh are backed by the same `IDashboardService`, so
  they can never independently drift out of sync with each other.
- **Optional per-camera thumbnail column** (toggle, off by default so it costs nothing until asked
  for), placed left of the camera name. Shows the most recent *completed* segment's own last frame —
  a new lightweight lookup (`GetLatestThumbnailInfoAsync`), deliberately not the existing live-RTSP
  snapshot endpoint (a fresh grab straight from the camera every call — fine for its actual occasional
  zone-editor use, too heavy to fire once per camera on every 60s dashboard poll) or the existing
  hover-preview lookup (bucketed to 5 minutes for a different purpose, and can land inside the
  still-recording segment that has no row yet, returning nothing).
- **Every dashboard column is now sortable**, and pagination was added with a 10/20/50/100/all
  rows-per-page choice, reusing the same click-to-sort/pagination widgets already used on Cameras and
  Admin/Nodes. Your current sort and page stay put across each 60s refresh — only a real click on a
  header or the page-size dropdown resets back to page 1, not the background refresh redrawing the
  same data.

Web-only, no node change.

## [0.78.0] - 2026-08-15

### Fixed

- **Regression from 0.76.0's live-tile drift resync: a lagging Live tile would repeat a few seconds
  of already-played video every couple of minutes before catching back up, and cameras in the same
  grid could drift up to several minutes apart from each other.** The periodic check reused
  `jumpToLiveEdge`, which targets the *start* of the newest buffered range — the right anchor for its
  original job (recovering when `currentTime` has fallen completely outside every buffered range,
  where a safe spot with decode cushion ahead of it matters more than exact position). For an
  already-playing-but-merely-lagging tile, jumping to the range's start landed *further* from the live
  edge than before, not closer, so the same drift check fired again on the next tick — a repeating
  rewind rather than a one-time correction. The periodic check now nudges to just behind the true live
  edge instead, converging without a hard rewind. Web-only, no node change.

## [0.77.0] - 2026-08-15

### Fixed

- **Dashboard per-camera fps/bitrate/last-report/reconnect-count stayed blank forever, even for a
  camera actively recording.** Root cause: this app's ffmpeg pipeline always uses `-f tee` (one RTSP
  session feeds both recording and live view), and the tee muxer never reports a real bitrate on its
  periodic progress line — confirmed against a real captured run of the app's own tee spec, and
  permanent for the entire session, not a brief startup blip the way an ordinary single-output mux's
  "bitrate=N/A" is. The parser required a numeric bitrate for the whole line to match at all, so it
  silently rejected every progress line under this app's pipeline — starving fps (and, since
  `HealthReportedAt` only advances when fps is present, the dashboard's freshness signal) along with
  bitrate, even though `ReconnectCount` reported fine since it isn't derived from this regex. Fps now
  parses independently of bitrate; bitrate is derived instead from each completed segment's actual
  byte size over its actual duration — the real source of truth for `-c copy` recording, where the
  bytes written to disk *are* the camera's own encoder output. Node change — `install-node.ps1`
  re-run needed on every recorder.

## [0.76.0] - 2026-08-15

### Changed

- **Live now opens the saved view you watched last, instead of always showing the flat all-cameras
  grid.** Falls back to the first view when there's no remembered one (or it has since been deleted),
  and to a "No views exist yet, click here to create one!" prompt when there are no views at all.
  The remembered view is stored per-browser in `localStorage`, the same mechanism the theme toggle
  and Playback's timeline position already use — no server-side per-user state was added for it. The
  all-cameras grid is still one click away via the view picker's "All cameras" option (`/Live?all=1`),
  which also now appears on Views/Play so the grid stays reachable from there too.
- **Navbar links carry emoji icons** (📊 Dashboard, 📡 Live, ⏪ Playback, ⬇️ Exports, 🔲 Views,
  📷 Cameras, ⚙️ Admin), matching the emoji-as-icons convention the app already uses elsewhere
  (🟢/🔴 camera state, ⚠️ warnings, 🌙/☀️ theme). Each is `aria-hidden` so screen readers announce the
  link text alone.
- **Accessibility labels** on icon-only controls across the Live grid, view cells, and view editor:
  `aria-label` mirroring each control's existing tooltip, the Bootstrap-standard
  `aria-controls`/`aria-expanded`/`aria-label` on the navbar toggler, `aria-label="Close"` on
  dismissible alerts, and `role="status"`/`aria-live="polite"` on the tile and export status text so
  asynchronous updates are announced rather than silently replaced.
- **Toggling a Live tile into playback now starts 30 seconds back**, rather than at the start of the
  camera's most recent segment — on an actively-recording camera that could be minutes stale
  depending on where the segment rotation happened to be. The Playback page's own initial-position
  logic is unchanged.

### Fixed

- **Changing a view cell's aspect ratio never resized the cell**, so a portrait cell kept whatever
  height it was first placed with and its video overflowed the space available — squeezing the camera
  name underneath until it was clipped. The aspect dropdown was wired for `click` (to stop GridStack
  treating it as a drag) but never for `change`, so the resize the placement path already does was
  simply never triggered. The name label is also now `flex-shrink-0`, so any future height mismatch
  crops the video slightly instead of hiding the camera's name.
- **Motion badges never appeared on a saved view's cells**, only on the flat Live grid. Views/Play
  built its cells without the badge element or the `data-camera-tile` attribute the poller looks for,
  and never started the poll — all three are now in place, reusing the existing polling function
  unchanged.
- **The export camera picker only ever offered the cameras in the currently selected view**, so with
  no view chosen it showed nothing and with a single-camera view it showed exactly one checkbox —
  indistinguishable from "you can only export one camera at a time." It now lists every camera, with
  the current view's cameras pre-checked. Failure and success messages render as a proper alert
  rather than small inline text (a failed validation previously read as "the button does nothing"),
  and the button disables itself while a request is in flight so a slow response can't produce
  duplicate export jobs. The backend already accepted multiple cameras — this was UI-only.
- **An intermittent stuck drag could hijack later mouse gestures**, most visibly making timeline
  scrubbing stop responding after zooming or fullscreening a tile. Both video zoom/pan handlers
  tracked their drag with window-level `mousemove`/`mouseup`, so a `mouseup` that never reached the
  page — released outside the window, or swallowed by the fullscreen transition when Esc was pressed
  mid-drag — left the drag permanently active, panning on every subsequent mouse move. Both now use
  pointer capture with `pointercancel` handling, the same approach `timeline.js` already documents
  and uses for exactly this failure mode, and exiting fullscreen or resetting zoom explicitly ends
  any drag in progress.
- **A Live tile that fell behind its own live edge was never pulled forward again.** The existing
  resync only ran on a stall, which misses a tile that is playing fine but simply behind — the common
  state right after a reconnect, since a fresh session starts at whatever it first buffers rather
  than at the newest available frame. In a multi-camera grid one reconnected tile could sit seconds
  behind its neighbors indefinitely. Each tile now checks every 3 seconds and re-seeks to its own live
  edge when more than 3 seconds behind; because every tile chases its own edge, the grid converges
  without any cross-tile clock.

## [0.75.0] - 2026-08-14

### Fixed

- **Exporting a range that spans a camera reassignment always failed on its very first dispatch
  attempt.** 0.68.0 added splitting such an export into one item per node instead of failing
  outright, but `SplitItemAcrossNodesAsync` doesn't narrow each new item's time range — every split
  item still queried the whole original range's segments across every node involved, so the
  dispatcher's multi-node check immediately failed each pinned item with "this export's segments no
  longer match the node it was pinned to," the exact all-or-nothing failure the split was meant to
  avoid. Pinned items now filter their segments down to their own node before dispatching. Web-only,
  no node change.

## [0.74.0] - 2026-08-14

### Added

- **Health dashboard** (the Dashboard page, previously a bare stub) — per-camera real-time fps,
  bitrate, and cumulative reconnect count, plus each owning node's own online/offline status, with
  summary counts (recording / not reporting / disabled / nodes online) at the top.
- **Real-time stream health reporting.** `RecordingSession` now parses ffmpeg's own periodic progress
  line (the same "frame=... fps=... bitrate=..." stats line ffmpeg prints throughout any run) for
  live fps/bitrate, and tracks a cumulative reconnect count separate from the existing backoff-timing
  counter (which resets on success). Reported every ~15s per active camera, stored on the existing
  (previously unused) `CameraStream.Fps`/`BitrateKbps` columns plus new `ReconnectCount`/
  `HealthReportedAt`. No "dropped frames" metric — this pipeline is `-c copy` throughout (no decode
  ever happens), so there's no meaningful signal for ffmpeg to report there.

M7/M8/M11 finish-up plan, pass 7 — ordered before Alerting specifically so alerting has real signals
to trigger on. **LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**

## [0.73.0] - 2026-08-14

### Added

- **Application log capture on both tiers.** Generic Host's default console-only logging is invisible
  once a process isn't attached to a terminal — true for IIS's in-process hosting and especially true
  for `LarisVMS.Node`, a Windows Service with no console at all. A new minimal `FileLoggerProvider`
  (shared via Core) writes daily-rolling files: Web to `.\logs\app-*.log` (alongside IIS's own
  `stdout_*.log`, already proven writable by this app pool), Node to
  `%ProgramData%\LarisVMS\logs\node-*.log` (sibling to `node.config`). Each tier sweeps its own files
  past a fixed 14-day window on its own schedule.
- **System Logs viewer** (`Admin > System Logs`) — tails the Web tier's own log file with a date
  picker and text filter. Distinct from the existing Audit Log (who did what) and from a node's own
  logs, which stay on that node's local disk for now (no remote viewer yet — see this pass's backlog
  note in the M7/M8/M11 finish-up plan).

**LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**

## [0.72.0] - 2026-08-14

### Added

- **Database backups** (`Admin > Backups`) — scheduled daily (server-local time) or on-demand,
  `BACKUP DATABASE ... WITH INIT`, with retention-by-count cleanup and a 20-row history. Built for
  SQL Express installs, which have no SQL Agent to schedule their own. Ported near-verbatim from
  rsolva's `BackupService`/`BackupHostedService`. Restoring is deliberately not part of this page —
  restore a `.bak` by other means (SSMS, `sqlcmd`). M7/M8/M11 finish-up plan, pass 5. Web-only, no
  node change.

## [0.71.0] - 2026-08-14

### Fixed

- **Several admin pages displayed raw UTC timestamps formatted as if they were local time, with no
  "UTC" indicator.** `DateTime.ToString("g")` never converts timezone regardless of the value's Kind
  — since every DateTime read from the DB is UTC (see `ApplicationDbContext`'s converter), these
  displayed the bare UTC clock reading. The playback timeline has always converted correctly via JS
  (`toLocaleString()`), but these server-rendered pages did not: Exports (job requested time, from/to
  range), Cameras (last probed), Admin > Nodes (last seen), Admin > Node Builds (uploaded), and the
  new Admin > Audit Log page above. All seven now convert via `.ToLocalTime()` before formatting
  (using the web server's own local timezone — consistent with Schedule mode's existing
  node-local-clock design decision); the UTC tooltip on hover is unchanged.

### Added

- **Recorder nodes now report their own clock skew.** Each heartbeat carries the node's own
  `DateTime.UtcNow`; the server compares that against its own receive time and stores the difference.
  Admin > Nodes shows a ⚠️ badge when a node's clock disagrees with the server's by more than 60
  seconds (same threshold precedent as 0.44.0's camera-side DST-bug anchor) — a lightweight way to
  catch "NTP isn't running on this recorder" without a real NTP client. M7/M8/M11 finish-up plan,
  pass 4. **LarisVMS.Node change — install-node.ps1 re-run needed on every recorder** (older nodes
  keep working — the new heartbeat field is optional and just skips the skew measurement until
  updated).

## [0.70.0] - 2026-08-14

### Added

- **Audit log viewer** (`Admin > Audit Log`) — filter by action/details text, actor, and date range,
  paginated 50 rows at a time. Ported from rsolva's `Pages/Admin/Logs.cshtml` filter/pagination
  shape, adapted to this app's narrower `AuditLog` entity (`Action`/`Details`, not rsolva's
  `EventType`/`EntityType` split).
- **New audit log entries**: camera create/update/delete, node update/delete, global settings
  changes, and export creation now write to the audit trail — previously only login/logout did.
  M7/M8/M11 finish-up plan, pass 3.

## [0.69.0] - 2026-08-14

### Changed

- **Live view no longer blanks a tile to "Reconnecting…" text on a decode-error reconnect.** These
  reconnects are routine self-healing (a slow client — confirmed cause: WiFi roaming — hits a gap in
  the node's live fragment stream and the MSE session becomes unrecoverable, per this file's own
  `startSession` design notes) but used to read as broken since the tile went dark for however long
  the retry took. Once a tile has ever shown a frame, a reconnect now instead freezes that last frame
  (captured onto a canvas overlay, matching the video's own aspect-ratio letterboxing) with a small
  spinner on top, until the new session has a real frame to show again. Not a fix for the underlying
  fragment gap itself — that's a deliberate node-side tradeoff (protecting the recording pipeline from
  a slow live-view client) that would need its own separate change to address. Web-only, no node
  change.

## [0.68.0] - 2026-08-14

### Added

- **Exporting a range that spans a camera reassignment now produces multiple downloadable files
  instead of failing.** 0.66.0 gave this a clear error message but still refused the export outright;
  now the item is split into one export per node actually involved (e.g. a "Front Door" export
  covering a move from NVR1 to WINSERV1 becomes two items, each dispatched to the node that actually
  holds its slice of footage) — each reuses the exact same node-side ffmpeg/concat path as a normal
  export, just scoped to that node's own segments. Output filenames now always include the node name
  (e.g. `FrontDoor_NVR1_...mp4`) so the two parts of a split never collide, and the Exports page shows
  which node each item's footage came from. Retrying an item that failed under 0.66.0's old
  all-or-nothing check will now go through this split path instead. Web-only, no node change.

## [0.67.0] - 2026-08-14

### Added

- **Exports page now auto-refreshes.** While any export job is Queued or Running, the page polls its
  status every 4 seconds and redraws the table in place — no more manually reloading to see whether
  a running export finished.
- **Trash button to delete a finished export.** Available once every item in a job is Done or
  Failed. Best-effort tells each item's owning node to remove its output file immediately (falling
  back to the node's own 7-day export-retention sweep if that node is unreachable), then removes the
  job from the list.
- **Retry button on a failed export item.** Re-queues just that camera's item for
  ExportJobDispatcher's next poll cycle, without having to re-submit the whole export from Playback.

**LarisVMS.Node change — install-node.ps1 re-run needed on every recorder** (new
`DELETE /export-file/{exportItemId}` route for the trash button).

## [0.66.0] - 2026-08-14

### Fixed

- **Exporting a camera whose footage range crossed a node reassignment failed with an opaque "Node
  rejected the export request (HTTP 400)."** `ExportJobDispatcher` sent every segment path for the
  requested range to the camera's *current* node, but a segment recorded before a reassignment still
  lives on the old node's disk (same split `OrphanedCameras`/0.62.0 already accounts for on the
  retention side) — the current node correctly refused any path outside its own recording directory.
  `GetSegmentFilePathsAsync` now also returns each segment's owning `NodeId`, so a cross-node split is
  caught before dispatch and the item fails with a specific, actionable message (which node(s) still
  hold the older footage) instead of a bare HTTP status. Web-only, no node change.

## [0.65.0] - 2026-08-14

### Fixed

- **Hover thumbnails were blocked by the app's own Content-Security-Policy, regardless of the
  0.60.0–0.64.0 fixes.** Thumbnails are fetched as a blob and shown via
  `URL.createObjectURL()` on an `<img>`, but the CSP's `img-src` directive only allowed
  `'self' data: https:` — no `blob:` — so the browser refused to load every thumbnail image
  outright and fired the `<img>`'s `error` event, which is exactly what "No preview available"
  looks like. `media-src` already allowed `blob:` for MSE video playback; `img-src` needed the
  same widening for images. Confirmed via a live browser console CSP violation report. Web-only,
  no node change.

## [0.64.0] - 2026-08-14

### Fixed

- **0.63.0's broken-image backstop was itself a regression — every hover started showing "No
  preview available" almost regardless of whether the fetch actually succeeded.** Setting
  `previewImg.src` to a new URL while a previous load is still in flight aborts that previous load,
  which can fire the `<img>`'s `error` event asynchronously — sometimes *after* a newer, successful
  `showImage()` call had already moved on. The 0.63.0 handler had no way to tell a stale error
  (belonging to an already-superseded load) from a real one, and during normal fast hovering across
  the timeline, interrupting a load this way is the common case, not the exception — so it was
  blanking out perfectly good images almost every time. Now gated on the same hoverToken pattern
  already used elsewhere in this file: `showImage` stamps the token valid at the moment it sets
  `src`, and the error handler ignores anything that doesn't still match. Web-only, no node change.

## [0.63.0] - 2026-08-14

### Fixed

- **Hover thumbnails could still render as a broken image even on a node with every prior fix
  installed.** `timeline.js`'s in-memory cache eviction (`evictOldestIfNeeded`) picked the
  first-inserted key as "oldest," but `Map.set()` on an already-existing key doesn't move it —so a
  bucket the user kept hovering back to could still sit at the oldest position by original insertion
  order and get evicted (its blob URL revoked via `URL.revokeObjectURL`) while it was the very image
  on screen. Cache hits now bump the entry to the most-recently-used position first. Also added an
  `<img>` `error` handler as a backstop regardless of cause — a bad/undecodable image now falls back
  to "No preview available" instead of ever showing the browser's own broken-image icon. Web-only,
  no node change.

## [0.62.0] - 2026-08-14

### Fixed

- **The "footage exists on another node" stale-segment warning could never clear on its own.**
  `StorageManager.SweepAsync` only ever walked `config.Cameras` — the cameras currently assigned to
  that node — so once a camera got reassigned to a different node (or deleted entirely), its leftover
  `cam-{id}/main/` folder on the old node became permanently invisible to retention and quota: no
  `RetentionDays`/`QuotaBytes` value exists for a camera that's no longer in the node's own config, so
  nothing ever aged it out. Confirmed live: two cameras reassigned away from NVR1 left 1,158 real,
  still-existing segment files each sitting untouched on its disk since 08-08, permanently driving
  `Admin`'s stale-segment warning with no way to clear short of manual cleanup. Now swept using the
  camera's own actual retention policy — a new `NodeConfigResponse.OrphanedCameras` list lets
  `NodeService.GetConfigAsync` hand back `Retention.Days` for any camera this node has leftover
  Segments for but no longer records, resolved the same global→per-node→per-camera way an assigned
  camera's retention always was, just scoped to this node specifically. Falls back to a flat 30-day
  default only if the server has no answer for a given camera at all. Footage still stays exactly as
  long as its configured retention says — this only makes sure that clock keeps running once a camera
  moves away, instead of stopping forever. **LarisVMS.Node change — install-node.ps1 re-run needed on
  every recorder.**

## [0.61.0] - 2026-08-14

### Added

- **Concurrency limits + low-priority background backfill for hover thumbnails.** Follow-up to
  0.59.0/0.60.0: nothing previously capped how many `ffmpeg` extractions could run at once, so a
  fast sweep across an uncached stretch of timeline could spawn dozens concurrently, competing with
  the node's own live recording for CPU/disk. On-demand (hover) requests are now capped at 2
  concurrent extractions (a request that can't get a slot within 3s gives up rather than piling up
  behind an unbounded queue); a new `ThumbnailBackfillService` walks each camera's 5-minute-aligned
  segments in the background, filling in whatever the cache is missing one at a time (its own
  smaller cap, `BelowNormal` OS process priority, a pause between each) so a long-uncached camera
  isn't permanently slow on its first hover. Also wired `AbortController` into the browser-side
  hover fetch so a superseded request (cursor moved to a different, still-uncached bucket) actually
  cancels — the abort propagates through the Web proxy to the node, stopping that `ffmpeg` process
  too, not just the local `fetch()`. **LarisVMS.Node change — install-node.ps1 re-run needed on
  every recorder.**

## [0.60.0] - 2026-08-14

### Fixed

- **Hover thumbnails sometimes rendered as a broken image after "Loading…".** The node cached a
  newly-extracted thumbnail by writing bytes straight to its final `thumbs/...jpg` path — a second
  concurrent request for the same bucket (another hover, another browser tab, a page refresh) could
  see the file via `File.Exists` and start streaming it back before the first write had finished,
  serving a truncated JPEG the browser can't decode. Now writes to a per-request temp file and
  atomically renames it into place, so a concurrent reader only ever sees the fully-written file or
  nothing at all. **LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**

## [0.59.0] - 2026-08-14

### Added

- **Timeline hover thumbnails (M7 pass 2).** Hovering the Playback page's per-camera timeline shows
  a small preview frame from that point in the recording — enough to spot that something changed
  without scrubbing to it. Thumbnails are generated one per 5-minute bucket (not continuously — the
  goal is "did anything change," not frame-accurate scrubbing), capped at 150x150px with the
  camera's own aspect ratio preserved, and compressed for minimal storage (`-q:v 8`). Follows this
  codebase's existing signed-proxy pattern exactly: a new `MediaToken.IssueForThumbnail`/
  `TryValidateThumbnail` pair (binding camera + segment path + offset) authorizes a new
  `/playback-thumbnail` route on both Web (proxies to the owning node) and Node (extracts the frame
  via a new `ThumbnailCapture` class, mirroring `SnapshotCapture`'s process-supervision shape but
  reading an existing file with `-ss` seeking instead of a live RTSP grab). Generated thumbnails are
  cached on the node's disk (`cam-{id}/thumbs/...`, mirroring the `main/` folder shape) and deleted
  automatically whenever their source segment is — no separate retention setting, no independent age
  sweep. The merged "all cameras" timeline has no hover behavior; there's no single camera to
  preview there. **LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**

## [0.58.0] - 2026-08-13

### Fixed

- **Double-click-to-fullscreen didn't work on a saved view's own playback page** (`/Views/Play/{id}`,
  reached by picking a view from either Live's or Playback's picker) — that page has its own
  hand-rolled tile implementation (`view-play.js`) that was never wired up to the shared
  `fullscreen-tile.js` module Live and Playback both use; it was missing the `<script>` include
  entirely, so only the small hover-only fullscreen button worked, and even that fullscreened the
  bare `<video>` instead of the tile's frame, dropping the mute/exit buttons out of the fullscreened
  render subtree. Now wired the same way as the other two pages.

## [0.57.0] - 2026-08-13

### Fixed

- **Playback timeline/current-time readout never advanced during playback.** `wireExportPanel` (and
  its `populateExportPanel`/`submitExport` helpers) referenced a bare `o` instead of the module-level
  `opts`, throwing an uncaught `ReferenceError` partway through `init()` — since that happened before
  `setInterval(updatePlayhead, 500)` was ever reached, the timeline strip and time readout silently
  never started updating, even though the video itself played normally. Pre-existing bug, not
  introduced by the LarisVMS rename; the Export panel button/panel wiring was also broken by the same
  typo and is fixed alongside it.

## [0.56.0] - 2026-08-13

### Changed

- **Renamed NidusVMS to LarisVMS project-wide**: solution/project names, namespaces, database name,
  IIS site/pool, and docs/scripts all now say LarisVMS. The recorder node Windows service was also
  renamed (`NidusVMSNode` -> `LarisVMSNode`), along with its `%ProgramData%` folder and the
  `NIDUSVMS_*` environment variables it reads (`--server-url`/`--registration-key`/etc. CLI args are
  unaffected). **LarisVMS.Node change — install-node.ps1 re-run needed on every recorder**, and this
  one isn't a simple in-place update: because the service name itself changed, the auto-updater can't
  rename the existing `NidusVMSNode` service in place, so the old service must be manually stopped and
  removed (`sc.exe delete NidusVMSNode` after `Stop-Service`) before running `install-node.ps1` fresh.
  Historical changelog entries and release notes below were also rewritten from NidusVMS to LarisVMS
  to match. Two internal identifiers were deliberately left untouched — the Data Protection
  `SetApplicationName` and the `SecretProtection` crypto purpose string both still say `"Rcordr"` (an
  even earlier product name), since changing either would make every existing encrypted value
  permanently undecryptable; see the comments at their definitions for the full rationale.

## [0.55.0] - 2026-08-13

### Added

- **Schedule and Event recording modes**, completing M8's original four-mode design (only
  Continuous/Motion existed before this). **Schedule mode** keeps a segment only if it starts
  inside one of the camera's configured time windows (new `Cameras -> Schedule` page — days of the
  week + a start/end time, windows can cross midnight), evaluated against the recorder node's own
  local system clock rather than a stored timezone. **Event mode** keeps a segment only if a
  specific "Drives recording" event tag rule fired nearby — unlike Motion mode, it ignores Motion
  zones and the built-in ONVIF motion classifier entirely, so it's for "record only when this exact
  tagged trigger fires," not "record on any activity." Both fail open (keep everything, warn once in
  the logs) when nothing is configured yet, the same philosophy Motion mode already uses for a
  camera with no zone. Internally, `Recording.Mode` gained a proper `RecordingMode` enum on the node
  side (previously raw string comparisons scattered across the gating code) and the Motion-only
  deferred-decision machinery was generalized to cover Event mode's narrower single-signal gating too.
  **LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**
- Also fixes a version-bookkeeping gap: `LarisVMS.Node`'s and `LarisVMS.NodeUpdater`'s own csproj
  `Version` had drifted behind `LarisVMS.Web`'s (0.48.0 and 0.46.0 respectively, vs. Web's 0.54.0) —
  both now match Web's version on every release going forward, not just ones that touch their code.

## [0.54.0] - 2026-08-13

### Added

- **Admin dropdown in the main nav** (Nodes, Node Builds, Settings), replacing the two flat
  top-level "Nodes"/"Settings" links. Node Builds previously had no nav entry at all — only
  reachable via a help-text link from Nodes or Settings — the exact "can't find it" pattern this
  groups against for every admin page added from here on.

## [0.53.0] - 2026-08-13

### Fixed

- **`install-node.ps1` had no UTF-8 BOM, breaking it under Windows PowerShell 5.1** — confirmed live
  on nvr1 as "Missing closing ')'"/"Missing closing '}'" parse errors that made no sense against the
  actual source. Root cause: PowerShell 7 (used to verify the file was fine) defaults to UTF-8 for a
  BOM-less script; Windows PowerShell 5.1 (the Windows default `powershell.exe`) instead falls back
  to the system ANSI codepage, which mangles the em dashes and curly apostrophes this codebase's
  comments use throughout into byte sequences that break the tokenizer — reproduced exactly by
  decoding the file as Windows-1252 and reparsing it. All four first-party scripts
  (`install-node.ps1`, `build-node.ps1`, `deploy.ps1`, `fix-legacy-segments.ps1`) now carry a UTF-8
  BOM, which both PowerShell versions honor correctly. The share copy at
  `\\files1\install\LarisVMS\node\win\install-node.ps1` was updated directly so nvr1 doesn't need to
  wait for a redeploy to retry.

## [0.52.0] - 2026-08-13

### Fixed

- **Admin -> Nodes pointed at Setup -> Node for the recorder registration key**, a leftover from
  before that control moved to Admin -> Settings — leaving two different pages able to show/rotate
  the same key, with the stale help-text link being the one people actually got sent to. Now links
  straight to Admin -> Settings' own Node registration section. Also refreshed the Node Builds copy
  on both Admin -> Nodes and Admin -> Settings to describe the current `deploy.ps1`-registers /
  admin-approves flow instead of the old "upload a build" wording it still had.

## [0.51.0] - 2026-08-13

### Fixed

- **`deploy.ps1`'s new node-build-registration step (0.50.0) ran before EF migrations applied**, so
  on the very first deploy after that same release added the `Status`/`ApprovedAt`/`ApprovedBy`
  columns, the INSERT hit a database that didn't have them yet, failed, and was swallowed by the
  step's own best-effort `catch` — leaving the just-built exe copied to disk with no matching queue
  row and no visible error. Registration now runs after the migrations step instead of before the
  app pool is even stopped. The 0.48.0 build stranded by this on the live deploy has been registered
  by hand and is sitting on Admin -> Node Builds waiting on approval.

## [0.50.0] - 2026-08-13

### Changed

- **Recorder-node build uploads on Admin -> Node Builds are gone, replaced by a `deploy.ps1`-driven
  approval queue.** Uploading a several-hundred-MB self-contained `LarisVMS.Node.exe` through the
  browser hit IIS's own `requestFiltering` `maxAllowedContentLength` as a 413 — enforced ahead of
  Kestrel/ASP.NET Core, so the page's own `RequestSizeLimit`/`RequestFormLimits` attributes never
  even got a chance to apply. `deploy.ps1` already runs locally on the server as Administrator, so
  it now registers whatever `build-node.ps1` just built directly against the server's own
  `%ProgramData%\LarisVMS\node-builds` folder and database — no HTTP upload, no IIS limit to hit,
  and no-op on a redeploy that didn't bump `LarisVMS.Node`'s own version. Every registered build
  lands as **Pending**; Admin -> Node Builds is now a Pending/Approved/Rejected review queue (with
  an audit trail of who approved/rejected what) instead of an upload form, and only an Approved
  build is ever offered to a node's heartbeat. Skippable with `-SkipNodeBuildRegistration`.

### Fixed

- **Dragging a zoomed Playback video tile could also kick off the browser's own native
  drag-the-video-out gesture at the same time as the tile's own pan-drag**, since the mousedown
  handler never called `preventDefault()`. Once that native drag started, the browser owned the
  rest of the mouse gesture — showing the no-drop/circle-slash cursor over the page (including the
  timelines below, not valid drop targets) and starving the tile's own `mousemove` handler of real
  deltas. Reported as dragging feeling "random" between the per-camera and all-cameras timelines,
  and unable to drag at all without the no-drop cursor appearing. Same fix applied to the
  fullscreen-tile pan-drag (Live and Playback both use it).

## [0.49.0] - 2026-08-13

### Fixed

- **Picking a saved view from Live's view picker left no way to switch to another view without
  clicking "Back to views" and picking again.** The picker itself only ever existed on Live/Index;
  Views/Play (where you land after picking) had no counterpart, just "Back to views" and Fullscreen.
  Views/Play's toolbar now carries the same picker, pre-selected to the view you're on, so switching
  views works from either page.

## [0.48.0] - 2026-08-13

### Added

- **Recorder node auto-update.** Upload a new `LarisVMS.Node.exe` build once on a new
  Admin -> Node Builds page, and every recorder node whose reported version is older picks it up on
  its own next heartbeat, downloads it, verifies its SHA-256, and swaps its own running binary — no
  more manually re-running `build-node.ps1` / `install-node.ps1` on each recorder machine. Direct
  port of the existing dploid.Agent/dploid.AgentUpdater pattern: LarisVMS.NodeUpdater.exe (previously
  a stub) is a small detached helper that waits for the Windows Service to actually stop, backs up
  and swaps the binary, restarts the service, and re-applies the same failure-recovery config
  install-node.ps1 sets at install time. `build-node.ps1` now publishes and bundles
  LarisVMS.NodeUpdater.exe alongside LarisVMS.Node.exe; `install-node.ps1` installs it into
  `C:\Program Files\LarisVMS\Node` on both fresh installs and upgrades. Gated by a new global
  "Auto-update recorder nodes" setting on Admin -> Settings (on by default) — no per-node override
  in this first pass. Uploaded builds are stored under `%ProgramData%\LarisVMS\node-builds`,
  deliberately outside the IIS site directory so `deploy.ps1`'s mirrored publish never touches them.

## [0.47.0] - 2026-08-12

### Added

- **Multi-camera video export.** Select several cameras and a timeframe from the Playback page's
  toolbar ("Export…") and get one MP4 per camera, delivered asynchronously through a new Exports
  page rather than a synchronous per-camera download — a job can span far more footage than a
  request should hold a browser connection open for. Creating an export writes an ExportJob plus one
  Queued ExportJobItem per camera; a new Web-tier background service (ExportJobDispatcher, the first
  BackgroundService/async job in LarisVMS.Web) polls for Queued items, resolves each camera's
  recorded segments for the range, and dispatches to that camera's node over a new signed,
  short-lived export token (mirroring the existing playback-segment token, just binding
  cameraId+exportItemId instead of a specific file). The node writes an ffmpeg concat-demuxer list
  file and runs a pure `-c copy` remux (no transcode — one camera's own segments already share
  codec/resolution) into a new `exports/` folder alongside its `cam-*/` recording folders, then
  reports success/failure back. Finished files are downloaded through the same signed-proxy shape
  `/playback-segment` already uses, with `Content-Disposition: attachment` added for the first time
  since this is the first proxied route meant to be saved rather than played inline. Export output is
  swept from a node's disk after 7 days by the existing StorageManager sweep, on the same age-check
  pattern as normal recording retention. Gated behind a new `Exports.View` permission, resolved
  dynamically the same way every other permission string in this app already is — no catalog update
  needed.
- **Double-click focused/fullscreen view on Live and Playback camera tiles.** Double-click a tile to
  fullscreen it via the real browser Fullscreen API on the tile's own container (not the bare
  `<video>`), so its own overlay controls stay reachable while fullscreened instead of disappearing
  along with every other sibling element; double-click again, or Esc, exits. Every other tile on the
  page keeps streaming/playing untouched the whole time, since nothing outside the fullscreened
  element is torn down — it's simply not painted. While a tile is fullscreened, scroll to zoom in
  (down to the normal fill size, no lower) and drag to pan once zoomed — a separate zoom/pan
  implementation from Playback's existing non-fullscreen digital zoom buttons, which are unchanged
  and hidden while a tile is fullscreened (along with the primary-select badge and, on Live, the
  playback-toggle button) so fullscreen shows only mute and exit.
- **Live View: toggle a single camera into playback mode** without leaving the page — adds a mini
  timeline and play/pause to just that one tile, reusing the same MSE/segment-fetch player Playback
  already uses rather than a separate implementation. Every other tile keeps showing pure live video
  with no timeline. Only one tile can be in playback mode at a time; toggling a different tile
  reverts whichever one was previously toggled back to live first. To play back several cameras
  together, use the Playback page.
- **Pagination for the Cameras and Nodes tables**, with a rows-per-page selector (10/25/50/100/All,
  remembered per table), composing with their existing client-side filter and sort — filtering or
  re-sorting re-paginates the result instead of the two stepping on each other.
- **Playback: exact-time seeking.** Click the current-time readout below the timeline to jump to a
  specific second directly, or (while paused) use the arrow keys to nudge the playhead by a second —
  Shift+arrow for ten. The timeline drag itself was already millisecond-precise; at the default
  24-hour zoom a single pixel of drag covers 100+ seconds, which read as "can only seek to the
  minute" even though nothing was actually snapping — these give an exact-seek path that doesn't
  depend on zoom level.

### Fixed

- **Live View fullscreen now shows only mute and exit-fullscreen.** Previously fullscreening a tile's
  bare `<video>` element dropped every sibling control (including the fullscreen button itself) out
  of the render tree, leaving no way back out except Esc. Fullscreening the tile's container instead
  keeps its controls reachable, and only mute + exit stay visible while fullscreened.
- **Playback's current-time readout is now centered** under the timeline's yellow playhead line
  instead of left-aligned block text.

## [0.46.0] - 2026-08-12

### Added

- **Day boundaries and date labels on the timeline.** Below day-level zoom every tick label was
  time-only, so scrolling across midnight gave no sign the day had changed, and a window sitting
  inside a single day named no date at all — you could scroll a long way with no idea which day you
  were looking at. Each local midnight in view now draws a full-height divider, and every visible day
  carries a small date badge ("Wed, Aug 12") at the left edge of its own span, including the
  partial day already in progress when the window opens. Suppressed at day-level zoom and wider,
  where the tick labels are already dates and this would only duplicate them; a day with too little
  width on screen to hold its badge is skipped rather than clipped. Day boundaries are local, not
  UTC, matching every other timestamp the UI renders.

## [0.45.0] - 2026-08-12

### Fixed

- **Corrected 0.44.0's stated cause: the cameras' clocks are fine — their ONVIF `UtcTime` field is
  not.** 0.44.0 attributed the timestamp problem to drifting camera clocks, on the strength of a
  per-camera measurement comparing each camera's newest event against its newest recorded segment.
  That measurement was confounded: a camera with a recording gap has a stale newest-segment, which
  inflates the apparent offset, so it read as "every camera is off by a different, drifting amount."
  The camera's displayed time and NTP sync were both verified correct on the actual hardware, which
  ruled that explanation out. Re-measured properly using a signal internal to the cameras themselves:
  a `LastClockSynchronization` notification carries a timestamp in its payload *and* one in the
  message's `UtcTime` attribute, so comparing those two isolates how the camera formats a timestamp
  from what any other clock says. On four of six cameras they disagree by **exactly 60.00 minutes,
  with zero variance across 35+ samples each**. Drift is never exactly an hour with no variance —
  that signature is a daylight-saving conversion bug in the camera's ONVIF layer, converting correct
  local time to "UTC" with the standard offset instead of the current DST one. It lands differently
  per unit (firmware version, most likely): on two of the six the `UtcTime` attribute carries the
  hour error and therefore got ingested, on the rest it doesn't. The 0.44.0 code fix was already the
  right one and is unchanged; only its explanation, the logged warning, and the code comments were
  wrong. The warning no longer tells operators to go fix NTP — it now names the one-hour DST
  signature and states plainly that the camera's own clock can be correct while this field isn't.

## [0.44.0] - 2026-08-12

### Fixed

- **Untrustworthy ONVIF notification timestamps corrupted the timeline and defeated Motion-mode
  gating entirely.** MotionSpans were stamped with the `UtcTime` the camera put on its notification,
  while Segments are stamped by ffmpeg on the node — two different time sources in one timeline, and
  on some cameras they disagree by exactly one hour. (**The cause originally stated here — drifting
  camera clocks — was wrong; see 0.45.0 for the corrected diagnosis.** The fix below is unaffected.)
  Two consequences that looked like separate bugs: motion was drawn an hour to the right of the
  footage that actually contained it (green with no recording under it in one place, recording with
  no green on it an hour earlier), and Motion-mode recording never discarded anything, because
  `DecideMotionSegment` compares motion timestamps against a window built from the segment's
  node-clock time — a timestamp an hour in the future satisfies any such window, permanently.
  Notification timestamps are now anchored to the node's own receive time whenever the reported time
  is more than 60 seconds away from it (a camera whose `UtcTime` is actually correct still keeps its
  own more precise instant), with a one-time warning logged per session naming the measured offset.
- **The merged "all cameras" timeline on Playback now only covers the cameras in the selected view.**
  It previously aggregated every camera in the system, so an overview strip under a two-camera view
  showed activity from four cameras that weren't on screen.

### Changed

- `ITimelineService.GetGlobalBucketsAsync` takes an optional camera-id scope; omitting it keeps the
  previous merge-across-everything behavior for any caller that genuinely has no camera scope.

## [0.43.0] - 2026-08-11

### Fixed

- **ONVIF-event Motion-mode recording never stopped, confirmed via live production data.**
  `CameraEventSession`'s built-in classifier used a 2-second `endAfter` debounce on its closing edge,
  intended to "absorb a quick flicker" the same way continuously-polled frame-diff motion needs.
  `MotionHysteresis.Observe`'s close check only evaluates elapsed time against a *later* call — the
  falling notification that sets `quietSince` always computes zero elapsed against itself, so a second
  qualifying call is required to actually close. A continuously-ticked source supplies that call
  automatically within a few ticks; ONVIF PullPoint notifications don't, since `Observe` only runs when
  a real notification arrives. A real camera's own true/false pairs recurred every 5-120 seconds with
  nothing else landing in between, so the closing edge — present and correctly formatted in the event
  log every single time — was silently lost, and `IsMotionActive` stayed true for hours. Every
  `MotionHysteresis` `CameraEventSession` constructs (the built-in classifier, and every EventTagRule,
  two-topic or toggle) now uses `endAfter: TimeSpan.Zero` — real ONVIF devices already debounce their
  own state, so there's nothing left to absorb, and a falling edge now closes the span in the same
  notification that reported it. Two new tests reproduce the exact starvation scenario against real
  timing and prove the fix closes it.

## [0.42.0] - 2026-08-11

### Fixed

- **The v0.41.0 "Event tags" page had no direct way to reach it — user reported not being able to
  find it anywhere on the Cameras page.** The only entry point was a small button on `Cameras/Edit`'s
  header, alongside "Zones" (which had the exact same gap since it shipped in M8 pass 1, just never
  reported). `Cameras/Index`'s per-row action column now links directly to both Zones and Event tags
  for every camera, not only reachable after first opening Edit.

## [0.41.0] - 2026-08-11

### Added

- **User-configurable ONVIF event tag rules, with an admin editor.** Camera Edit now has an "Event
  tags" link alongside Zones. A rule matches incoming ONVIF PullPoint notifications by topic — give it
  a Start topic alone if the topic's own payload carries a true/false state, or a matching Stop topic
  too if the camera fires two distinct topics for the rising/falling edge — and tags the timeline with
  a color you pick, independent of the built-in green motion coloring. Covers anything a camera's
  firmware pushes, not just motion: object/person detection, tamper, digital inputs, whatever topics
  the camera actually advertises. The editor's Start/Stop topic fields are populated from this
  camera's own observed ONVIF event history (a new `/observed-topics` endpoint), not typed blind.
- **"Drives recording" per rule.** A rule can optionally gate a Motion-mode camera's segment
  keep/discard decision the same way built-in motion and ServerMotion zones already do — rising edge
  starts the keep window, falling edge ends it, no timeout, matching the existing no-timeout gating
  guarantee. A rule with this off only ever affects the timeline's color, never recording.
- New `MotionSource.CustomTag` and `MotionSpans.EventTagRuleId` — a custom-tag span is now
  distinguishable from the built-in camera-pushed classifier's own span in the data, not just on
  screen.

### Changed

- Node-side ONVIF event polling (`CameraEventSession`) now restarts a camera's event session when its
  configured rule set changes, the same way a changed zone configuration already restarts the motion
  session — a rule added, edited, or deleted through the admin UI takes effect on the next reconcile
  rather than only after the camera is reassigned.

## [0.40.0] - 2026-08-11

### Fixed

- **Dragging the per-camera Playback timeline could feel unresponsive while dragging the "all
  cameras" one worked fine.** The page-level playhead-follows-playback loop calls each timeline's
  `setCenter` every 500ms regardless of what the user is doing — mid-drag, that snapped the strip
  back to the actual playback position on the very next tick, fighting the user's own drag since a
  drag gesture rarely finishes inside one 500ms window. `setCenter` now ignores programmatic recenter
  calls entirely while that timeline is being actively dragged — the drag's own pointer handling is
  already the authority over its position until release.

## [0.39.0] - 2026-08-11

### Changed

- **Live tiles (`Pages/Live` and `Views/Play`) no longer use native browser video controls.**
  v0.33.0 switched to native `<video controls>` shown on hover to replace an always-visible custom
  mute button — but native controls include a click-anywhere-on-the-video-to-pause behavior in most
  browsers, confirmed live as unwanted: there's nothing to meaningfully "resume" from on a continuous
  live MSE stream, so an accidental click just interrupted viewing for no reason. Replaced with a
  minimal custom overlay of exactly two buttons — mute/unmute and fullscreen — shown on hover, same
  as before, with no click-on-the-video-body behavior bound at all.

## [0.38.0] - 2026-08-11

### Fixed

- **Playback video tiles could grow past their allocated space and cover the timeline below them,**
  most visibly with a portrait (9:16) camera in the view. CSS Grid items default to
  `min-height: auto` — "never shrink below my content's own intrinsic size" — so a tall portrait
  video's own aspect ratio could force its whole grid row taller than the space actually available,
  growing the grid container (and the timeline pinned below it) right along with it. Each tile now
  gets `min-width: 0; min-height: 0; overflow: hidden`, and the grid container no longer sets an
  explicit `height: 100%` that was fighting its own flex sizing — the flex layout (`flex: 1 1 auto`)
  already correctly fills exactly the space left after the toolbar and timeline claim theirs.
- **A non-16:9 camera's video was stretched/distorted on `Pages/Live`** — its `<video>` element had
  no `object-fit`, so the browser's default (`fill`) squashed anything that wasn't already 16:9 to
  match the tile's fixed aspect box. Now `object-fit: contain`, matching how every other video tile
  in the app already handles this (`Views/Play`, `Pages/Playback`).
- **Timeline coverage bars showed as thin alternating stripes instead of solid blocks, and visibly
  pulsed while a shared position was advancing during playback.** Adjacent buckets sharing the same
  color were drawn as separate `fillRect` calls; at a wide zoom (up to ~2000 buckets, close to one
  per pixel) two same-colored neighbors could leave a hairline gap between them from sub-pixel
  rounding, which read as thin flickering stripes through what should have been one solid run of
  coverage — and during playback, since the whole strip's pixel positions shift slightly on every
  redraw following the advancing position, those hairline gaps didn't just look striped, they visibly
  jittered. Adjacent same-colored buckets are now merged into a single fill run with coordinates
  rounded once per run instead of independently per bucket, which has no internal seams left to
  flicker.

## [0.37.0] - 2026-08-11

### Fixed

- **Critical: every node process restart could silently delete real, already-recorded footage that
  already had valid database rows.** Confirmed live: `Segments` rows dropped from ~16,600 to ~235
  after this session's several version-bump deploys. Root cause: `RecordingSession.RunAsync` rescans
  its camera's *entire* on-disk history from scratch every time it starts, with zero memory of what
  the server already has rows for — this in-session tracking (`reportedPaths`) already correctly
  survives an *ffmpeg* reconnect within one run, but a full *node process* restart creates a brand
  new `RecordingSession` with an empty one. For a Motion-mode camera, that rescan re-fires
  `SegmentCompleted` for every old file exactly as if it were brand new — and at that exact moment, a
  freshly-restarted `MotionSession`/`CameraEventSession` has observed no motion yet, so nearly all of
  that re-fired history looked like "no motion" to `NodeWorker.DecideMotionSegment` and was silently
  discarded — deleting files that already had valid, previously-reported `Segments` rows, with no
  error anywhere (a discard logs at Debug level and never touches the database, so there was nothing
  to fail loudly). This happened on **every** node restart, which made it look like a single
  catastrophic event but was really compounding damage across each of the day's several deploys.
  Fixed: `NodeWorker` now fetches every path the server already knows about for this node
  (`GET /api/nodes/segments/paths`, the same endpoint the v0.30.0 reconciliation sweep already uses)
  once before any `RecordingSession` starts, and passes the relevant subset in as a pre-seed for that
  camera's `reportedPaths` — a file the server already has a row for is now recognized as already
  known and never re-evaluated, regardless of how many times the node process restarts. This is also
  the fix for a separate, related concern raised directly: switching a camera between Motion and
  Continuous no longer carries any risk of a later restart retroactively re-deciding footage recorded
  under the old mode — a segment's keep/discard decision is now made exactly once, at the moment it
  first completes, full stop. Three new tests exercise the actual rescan-skip behavior against a real
  temp directory. **This is a LarisVMS.Node change — install-node.ps1 re-run needed on both WINSERV1
  and NVR1 as soon as possible.**

### Known limitations

- This fixes the bug going forward — footage already lost to this bug earlier today cannot be
  recovered; the files are gone. The `Segments` rows for that footage should already have been
  cleaned up by the v0.30.0 reconciliation sweep (or will be on its next hourly pass), so the
  timeline should stop showing playable-looking gaps for it once that catches up.
- The known-paths fetch happens once, at node process startup — a camera reassigned to this node
  *after* that point starts with whatever was fetched at startup, not a fresh lookup. Not a gap for
  the bug this fixes (a genuinely new camera has no prior on-disk history to protect), just noted as
  a boundary of this pass's scope.

## [0.36.0] - 2026-08-11

### Fixed

- **A camera-pushed ONVIF motion event could stop keeping recorded segments partway through a long
  motion event, before the camera ever reported motion had stopped.** The v0.35.0 gating check
  (`HasMotionSince`) is keyed off how recently the camera's *last* notification arrived — fine for
  server-side detection, which re-confirms motion every ~200ms while it's genuinely ongoing, but many
  real ONVIF implementations send exactly one notification per edge (rising, then nothing again until
  falling), not a periodic "still active" heartbeat. For an event like that, the gating window could
  go stale relative to a segment decided well into the gap, even though the camera never said motion
  had ended. `CameraEventSession` now also exposes `IsMotionActive` — true for as long as a span is
  open, with no timeout of its own; it only goes false once an actual falling-edge notification
  closes it (or the node shuts down) — and `NodeWorker.DecideMotionSegment` keeps a segment if
  *either* the recency check or this passes. Recording now correctly keeps being retained
  continuously from the rising event straight through to the falling event, regardless of how sparse
  the camera's own notifications are in between. Same fix applied to the timeline checkpoint path
  (`EnqueueMotionCheckpoints`), which had the identical staleness gap for showing a long-running
  camera-event span before it closes. Two new tests lock in the exact scenario: `LastMotionAtUtc`
  frozen at the rising timestamp across a 10-minute gap while `IsActive` stays true throughout, only
  going false once a real falling-edge tick arrives. **This is a LarisVMS.Node change —
  install-node.ps1 re-run needed on both WINSERV1 and NVR1.**

## [0.35.0] - 2026-08-11

**M8 pass 6: ONVIF PullPoint event ingestion — closes the "camera-side event starts a recording"
half of M8's original verify checklist.** Previously deferred, confirmed genuinely greenfield before
starting (no ONVIF Events client existed anywhere in the codebase). Zone-side push
(`SetVideoAnalyticsConfiguration`) and privacy-mask burn-in remain deferred — the latter has a real
architectural blocker: `RecordingSession`'s tee pipeline is pure `-c copy`, and ffmpeg cannot apply a
video filter under stream copy, so burn-in needs its own design pass, not a drop-in addition.

### Added

- **A recorder node now subscribes to a camera's ONVIF PullPoint events and polls for notifications**,
  for any camera whose capability probe found an Events service. This is the node's first ONVIF SOAP
  conversation of its own (`LarisVMS.Onvif` was deliberately not referenced by `LarisVMS.Node` until
  now) — event polling is a continuous, long-lived exchange the web tier can't pre-resolve into a
  one-shot value the way `GetStreamUri` already is, so it has to happen from wherever the RTSP
  connections already do: the node, on the camera's own LAN. The web tier still resolves and hands
  over the Events service's own address (from the capability prober's raw XAddr map) — the node never
  needs its own `GetCapabilities` round trip just to find where to subscribe.
- **Motion-classified events now drive the exact same Motion-mode recording gate and MotionSpans
  timeline coloring a ServerMotion zone already does** — reusing `MotionHysteresis` (the same
  open/close-span primitive `MotionSession` uses per zone) rather than a new state machine, one
  instance per camera since an ONVIF event has no concept of one of our own drawn zones. A camera
  relying only on its onboard motion detection (no ServerMotion zone configured at all) now gates
  Motion-mode recording correctly too, not just a zone-detected one — `NodeWorker`'s keep/discard
  decision checks both signal sources and keeps a segment if either saw activity in the window.
- **Every raw notification (motion or not) is logged to a new `CameraEvents` table** — tamper,
  digital input, audio, and any other ONVIF event topic a camera's firmware reports, not just motion.
  No dedicated viewer page yet; that and wiring `TriggeredRecording` for non-motion events (a
  standalone "Event" recording mode) are a follow-up polish pass, not attempted here.
- `Cameras/Edit`'s Motion-mode warning now accounts for ONVIF event capability, not just a
  ServerMotion zone — a camera with only onboard detection no longer sees a misleading "won't discard
  anything" warning.

### Known limitations

- **Unverified against a real camera** — same caveat every M8 pass has shipped with. ONVIF PullPoint
  support and topic naming vary significantly by vendor/firmware; `CameraEventClassifier`'s topic
  markers cover the common ONVIF-standard motion topics but may need adjusting once watched against
  real notifications from this deployment's actual cameras.
- No restart-on-config-change for an event session the way zone changes restart a motion session —
  credentials/EventsServiceUri essentially never change for an existing camera in practice, and a
  session that starts failing its subscribe attempt recovers on its own retry regardless.
- **This is a LarisVMS.Node change — install-node.ps1 re-run needed on both WINSERV1 and NVR1.**

## [0.34.0] - 2026-08-10

User-directed Playback redesign, three explicit requests: move the timeline to the bottom of the
page, fill the rest of the window with video sized to camera count, and make the timeline itself
more compact and scale-aware. **Unverified in a real browser** — no browser tool available this
pass; build, `dotnet test`, and `node --check` all pass, but the actual rendered layout, grid math,
and tick-label spacing haven't been watched happen on screen. Flag for extra scrutiny next real
browser session, same as any UI-only pass shipped that way in this project's history.

### Changed

- **Playback layout: timeline pinned to the bottom of the viewport, video grid auto-sized to camera
  count and window size.** `Pages/Playback` now fills the space below the nav bar exactly (measured
  via JS, recomputed on `resize`) with a flex column: toolbar, then a video grid that claims all
  remaining room, then the timeline strip pinned at the bottom. The video grid no longer reproduces
  the selected view's own saved x/y/w/h arrangement (that's still what Live and Views/Play do) —
  Playback now auto-computes an N-camera grid sized purely by count (`ceil(sqrt(N))` columns, enough
  rows for the rest): one camera fills the whole area, two sit side by side, six form a 3x2 grid, and
  so on, growing/shrinking on window resize via plain CSS Grid `1fr` tracks. Deliberate divergence
  from the live-viewing pages, since review is about maximizing each tile's video size, not
  preserving a curated layout. Each tile's camera-name label moved from a below-video bar into a
  corner overlay badge to avoid eating into that space.
- **Timeline bar redesign.** Height halved (60px → 30px). Tick marks and scale-appropriate labels
  (date-only at day+ zoom, hour:minute at hour+ zoom, full time with seconds below a minute) now run
  along the bar at "nice" intervals (1s up to 90 days) chosen to fit the canvas width, replacing the
  old two-corner-label footer — this is the "scale is obvious based on zoom level" piece. The exact
  playhead time moved out of the canvas into its own line directly below the per-camera timeline,
  separate from the interval tick labels (which mark scale, not the precise instant).

## [0.33.0] - 2026-08-10

### Changed

- **Live tiles (`Pages/Live` and `Views/Play`) now show native browser video controls on hover,
  hidden otherwise.** Replaces the always-visible custom mute button on `Pages/Live`, which only
  duplicated what the browser's own control bar already provides (and `Views/Play` had no volume
  control at all before this). Toggles the `controls` attribute itself on mouseenter/mouseleave
  rather than hiding it with CSS, which is what actually shows/hides the native bar cross-browser.

### Fixed

- **A Live camera could intermittently go blank with no error, then resume on its own.** The live
  MSE buffer is a sliding window — the browser evicts old data as new fragments arrive, and the
  node's own slow-client handling drops the *oldest* buffered fragment when a viewer falls behind
  (an intentional tradeoff so a slow client can't back-pressure the recording pipeline — see
  `live-view.js`'s header comment). `currentTime` could drift into that now-evicted territory later
  in a long-running session, not just at startup — MSE genuinely has nothing buffered there, so
  playback stalls with no error and no frame until the browser's own gap-jump heuristics eventually
  (or, on some browsers, never) recover it. The existing one-time "seek to the live edge on first
  buffered data" logic is now also applied on every `waiting` event where `currentTime` has actually
  fallen outside all buffered ranges — ordinary "caught up to live edge, waiting for the next
  fragment" stalls are left alone so this doesn't turn normal buffering into a visible jump.

### Investigated, not changed

- **The intermittent "video decode error 3/4" / "SourceBuffer error" that self-recovers is expected
  behavior, not a bug** — it's the visible side effect of the same slow-client fragment-drop
  described above: a dropped fragment mid-stream corrupts everything after it for that
  `SourceBuffer`, which is unrecoverable for the current session by design (MSE byte streams need
  continuous data), so `live-view.js` tears the session down and opens a fresh one automatically.
  The "briefly, then resumes" pattern already reported is exactly that retry succeeding. Worth
  revisiting only if it turns out to happen often enough to be disruptive — the real fix would be a
  larger server-side buffer with backpressure instead of dropping, a bigger design change than a
  client-side tweak.
- **"View menu disappears after selecting a view in Live mode"** — reviewed `Views/Play`'s kiosk-mode
  code (`view-play.js`'s `wireKiosk`): the top nav only hides on an actual browser
  `fullscreenchange` event, tied to the page's own Fullscreen button, and nothing in the flow from
  Live's view picker touches that. Couldn't identify a concrete bug from code review alone — needs a
  more specific description of what's actually disappearing (the site's top nav bar? the Live page's
  own view-picker dropdown, which is expected to go away since picking a view navigates to a
  different page entirely?) to chase further.

## [0.32.0] - 2026-08-10

### Fixed

- **Motion-mode segments with real, detected motion in them were still being discarded.** Confirmed
  live: a camera with motion clearly logged in `MotionSpans` was still losing nearly every segment.
  Root cause: the deferred keep/discard decision checked for motion no earlier than
  `segment.EndUtc - PostRollSeconds` — a window anchored to the segment's *end*, not the segment
  itself. Motion happening more than `PostRollSeconds` before a segment finished (e.g. near its start
  or middle) was invisible to that check. This was masked by the original 30s `PostRollSeconds`
  default, which covers close to half of a 60s segment, but became a real, confirmed data-loss bug as
  soon as a shorter post-roll was configured (e.g. via the new `Admin/Settings` page). The lookback
  window is now anchored to the segment's *start* instead, so motion anywhere in the segment itself,
  not just near its tail, correctly keeps it — one comparison still covers pre-roll, post-roll, and
  "motion happened during the segment" together, same as before. New test proves both the bug (the
  old anchor misses it) and the fix (the new one catches it) side by side. **This is a LarisVMS.Node
  change — install-node.ps1 re-run needed on both WINSERV1 and NVR1.**

## [0.31.0] - 2026-08-10

### Added

- **`Admin/Settings`: one page for every global setting that can be overridden per node or per
  camera.** Retention (`Retention.Days`, `Storage.WatermarkPercent`) had its own dedicated page since
  M4; M8's `Recording.Mode`, `MotionPreRollSeconds`, `MotionPostRollSeconds` never got the equivalent
  — only reachable as a per-camera override on Cameras/Edit, with no way to see or change the base
  default they were overriding. Also added: `Storage.RootPath` (previously set once by the Setup
  wizard with no way to view or change it afterward) and `Node.RegistrationKey` (previously shown
  exactly once during Setup, with no way to retrieve it again when onboarding a later node, or to
  rotate it if it leaked — now viewable, editable, and has a "Generate new key" button). Replaces
  `Admin/Retention` rather than duplicating it — the old URL redirects here so nothing bookmarked
  breaks. Uses the existing `Settings` RBAC resource (already seeded for Viewer's read-only access)
  rather than the old page's `Retention`-specific one.

## [0.30.0] - 2026-08-10

Three real reports from the same live-testing session, investigated with actual database and
filesystem access rather than inference.

### Fixed

- **Timeline drag could get permanently stuck to the cursor.** The tape-scrubber drag handler used
  plain `mousemove`/`mouseup` on `window`; releasing the mouse button after the cursor had left the
  browser window (dragged past the edge of the screen, onto another monitor, over the taskbar) never
  fired a `mouseup` the page could see, leaving the drag latched to the cursor with no way to release
  it short of reloading. Switched to Pointer Events with `setPointerCapture`, which keeps delivering
  `pointermove`/`pointerup` to the timeline canvas for the duration of the drag regardless of where
  the pointer physically is, plus a `pointercancel` handler for the same class of "pointer taken away
  mid-gesture" case. As a side effect this also makes the timeline draggable on touch devices for the
  first time (added `touch-action: none` on both Playback canvases so a touch drag doesn't fight the
  browser's own scroll gesture) — not the point of the fix, but free from switching event families.
- **Cameras/Edit had no way to reach the Zones editor once a zone already existed.** The Recording
  Mode section's inline link to Zones only appeared in the "no zone configured yet" warning; once a
  zone existed, that branch had descriptive text but no link at all (the page-level "Zones" button up
  top was still there, but this was still a real inconsistency worth closing). Added a matching
  "manage zones" link to the has-a-zone case too.

### Added

- **StorageManager now reconciles its own Segments rows against disk, on an hourly cadence.**
  Investigating a "recording is unavailable (missing on the recorder)" report on motion-timeline
  playback led to a direct database + filesystem check: roughly a third of all `Segments` rows across
  both nodes point at files that no longer exist on disk. This is the orphaned-row gap flagged as a
  known limitation in 0.27.0 (a deletion succeeds, the report of it to the web tier fails, and before
  0.27.0 that failure was never retried) — accumulated over the app's full history, not a new
  regression; the current live rate since 0.27.0's fix actually reached these nodes is much lower.
  Fixed properly instead of a one-off DB cleanup: the node now periodically fetches every FilePath the
  web tier believes it still owns (`GET /api/nodes/segments/paths`, node-authenticated) and reports
  any that are missing on its own disk through the exact same path a normal eviction already uses
  (`POST /api/nodes/segments/delete`) — self-healing going forward, not just a one-time fix. Runs far
  less often than the 5-minute eviction sweep since it's a full fetch-and-diff of potentially
  thousands of rows against a check that only ever finds something in the wake of an actual reporting
  failure.

### Known limitations

- The reconciliation sweep only clears rows going forward from whenever each node's binary is next
  updated to this version — it runs on the node's own hourly cadence, so a large existing backlog
  (like the ~1/3 figure found this session) clears out gradually over the following hour(s), not
  instantly on deploy.

## [0.29.0] - 2026-08-09

Found while double-checking the v0.28.0 sensitivity investigation: three cameras explicitly set to
Motion recording mode had 35+ hours of unbroken, gap-free segment retention in the database — which
is impossible if motion gating were actually running against them, regardless of sensitivity. The
sensitivity fix alone would not have fixed these cameras' behavior.

### Fixed

- **Motion-mode recording gate never re-read config after a camera's session started (stale closure
  bug).** `RecordingSession.SegmentCompleted` was wired up once, at the moment a camera started
  recording, capturing that moment's `NodeConfigCameraDto`/`NodeConfigStreamDto` by reference in the
  event handler's closure. Because already-recording cameras never get this handler re-wired on
  later reconciles, any settings change made to Recording.Mode, MotionPreRollSeconds, or
  MotionPostRollSeconds on an already-running camera had **zero effect** until its session happened
  to restart for an unrelated reason (e.g. a stream error or node restart). This is why the three
  Motion-mode cameras never discarded a single segment — the code path that would have judged and
  discarded them was never actually being reached with current config.
  `NodeWorker` now keeps a `_latestCameraConfig` cache refreshed on every reconcile, the
  `SegmentCompleted` closure captures only the camera's immutable `CameraId`, and
  `HandleSegmentCompleted` looks up fresh config on every single invocation. This takes effect
  immediately for already-running sessions — no node restart or session restart is required.

## [0.28.0] - 2026-08-09

Investigated why the timeline had shown zero motion since M8 shipped, with direct database and
process-level access to the live deployment (not inference from code review). The motion pipeline
turned out not to be broken — it was correctly configured with a threshold that real-world motion
essentially never reaches.

### Changed

- **Default zone sensitivity lowered from 15% to 3%.** Verified against real captured frames from a
  real outdoor camera (a zone covering ~73% of frame): the highest score observed across dozens of
  real frame comparisons, including visible compression-artifact spikes, was 0.04% — nowhere close
  to 15%. A zone covering most of the frame needs well over 10,000 pixels to change simultaneously in
  one ~200ms interval to reach that bar, a much higher threshold than a person or vehicle crossing a
  wide outdoor scene actually produces. This wasn't a detection bug: the running system's `ffmpeg`
  motion pipeline was confirmed live and running correctly, connected to each camera's Sub stream
  exactly as designed, and the `MotionDetector`/`ZoneRasterizer` math was confirmed correct by running
  it directly against real captured camera bytes. The only thing wrong was the number.

### Known limitations

- **This does not retroactively update zones already saved with the old 15% default** — the value
  was copied into each row at creation time, not resolved live from the compiled-in default.
  Existing zones need their sensitivity lowered manually via `Pages/Cameras/Zones`; 3% is a
  reasonable starting point given the measurement above, but hasn't been confirmed to correctly
  catch real motion without excessive false positives — that still needs a few days of real-world
  observation once applied.
- `PixelDeltaThreshold` (currently a fixed 25 out of 255, not user-configurable) was not implicated
  by this investigation and was left unchanged — the measured noise ceiling stayed comfortably under
  it. Worth revisiting only if 3% sensitivity turns out to still be too strict.

## [0.27.0] - 2026-08-09

First real-browser test of 0.26.0's streaming/sync playback rework confirmed it worked (faster
first frame, tiles within a couple seconds of each other instead of 10-30). It also surfaced a real
bug of its own, and a genuine pre-existing gap unrelated to any of this session's motion work.

### Fixed

- **A missing segment (404) triggered a burst of repeated identical requests, not one.** The
  tape-scrubber drag fires throttled scrub ticks roughly every 120ms, and a failed fetch never sets
  `currentSegmentId` — so a slow drag through one bad segment's time range re-triggered the exact
  same doomed fetch on every tick (confirmed live: one missing file produced 13+ repeated 404s for
  the same segment id in a couple of seconds). A segment id that 404s is now remembered for the rest
  of the page load; later seeks into its range go straight to an "unavailable" status without
  re-hitting the network. Scoped to 404 specifically — a 5xx (a transient node/proxy hiccup) is still
  retried on the next seek rather than permanently blacklisted.
- **`StorageManager` could permanently orphan a `Segments` row.** Pre-existing, unrelated to any of
  this session's Motion-mode work — found while investigating the 404 reports above. Every sweep
  deletes evicted files from disk, then reports the deletions to the web tier in a single best-effort
  call; every *other* node→web report in this codebase (segments, stream info, motion spans) already
  re-queues on failure, but this one didn't. If that report call ever failed — a network blip, the
  web tier restarting mid-sweep — the file was already gone from disk by the time the report was
  attempted, and the next sweep's directory scan can never rediscover a file that no longer exists to
  try reporting it again. The `Segments` row then lives forever pointing at nothing, and playback
  404s on it whenever a client tries. Deletion reports are now retried on the next sweep until they
  succeed; the file deletion itself is unchanged (still immediate, never delayed).

### Known limitations

- **This does not clean up rows already orphaned before this fix** — only prevents new ones. The
  404s reported today are very likely pre-existing orphans; there's no DB access from this
  environment to identify or clean them up directly. A future pass could add a one-time reconciliation
  sweep (compare `Segments` rows against what's actually on disk) if this turns out to be widespread.
- Two other reports from this same session — the timeline still not showing green, and the current-time
  readout not advancing when the selected camera has no video — have no code changes in this release.
  Both were reported before confirming this specific version was deployed, and 0.26.0's fixes for
  exactly these symptoms shipped only one release earlier; re-verify against this version before
  concluding either is still broken.

## [0.26.0] - 2026-08-09

M8 pass 5 — fixes why the timeline was *still* blue-only after 0.25.0's checkpoint reporting fix,
plus two Playback fixes reported directly against real footage: the timeline not following
playback, and cross-camera sync/startup latency.

### Fixed

- **0.25.0's checkpoint reporting fix wasn't enough.** It made a long-running *confirmed* span
  visible before it closed, but confirmation itself (`startAfter`, default 1s) was still required
  before anything reported at all — and segment retention never required confirmation, only that
  `LastMotionAtUtc` (updated on every raw motion tick, confirmed or not) stay recent. A scene with
  frequent short bursts, each too brief to individually hold for a full second, was already being
  correctly retained (blue) while never producing a single confirmed span to report — the timeline
  stayed blue-only even though Motion mode was visibly doing its job. `MotionHysteresis
  .CurrentInProgressSpan` now reports on raw recent activity (using whichever run-start is
  available, confirmed or not), matching what retention already treats as real. Still bounded to
  one checkpoint per ~15s tick, not per frame — a flapping zone produces at most one new row per
  tick it's still flapping, not one per burst.
- **The timeline stopped following playback entirely when the ★ primary camera happened to stall**
  (no segment found, still loading) even though every other tile in the view kept playing normally
  — confirmed live. The shared clock now falls back to any other actively-playing tile rather than
  freezing whenever the specific starred camera has trouble.

### Changed

- **Playback segments now stream into the decoder incrementally instead of waiting for the whole
  file.** The player previously fetched an entire segment (several MB) before handing any of it to
  MSE — confirmed live as a real, meaningful delay before the first frame appeared, worse on a
  larger segment or slower link. Chunks are now appended to the SourceBuffer as they arrive off the
  network, and playback starts (seek + play) as soon as the *first* chunk is buffered rather than
  waiting for the last one; the browser's native "wait for more data, then resume" behavior handles
  a seek target that hasn't streamed in yet, the same way any progressively-downloaded video works.
- **Tiles ending up 10-30 seconds apart in wall-clock content position** was traced mainly to this
  same whole-file wait varying per tile (a smaller/faster segment finished long before a
  larger/slower one on a different camera or node). The streaming change above should tighten
  startup timing on its own; on top of it, every actively-playing tile is now checked each 500ms
  tick against the shared playhead clock and nudged back in line (a same-segment `currentTime`
  correction, not a reload) if it's drifted more than 2 seconds.

### Known limitations

- All three fixes here are unverified against real footage — this is the deepest, riskiest change
  to the MSE segment-loading path since the original decode-restart-at-boundaries design, and
  streaming/incremental `appendBuffer` has its own set of real-world MSE quirks history in this
  project (HEVC codec-string mismatches, buffered-range origin offsets) that a synthetic test can't
  catch. Syntax-checked with `node --check`; no unit-test harness exists for this file.
- The drift-correction nudge is a blunt instrument — a same-segment `currentTime` jump, not a
  smoothed resync. A tile that drifts often (rather than once) will visibly jump periodically. If
  that turns out to be more distracting than the drift itself, worth revisiting.

## [0.25.0] - 2026-08-09

M8 pass 4 — fixes why the timeline showed no motion at all on cameras the user had confirmed were
working: a long-running span was invisible in `MotionSpans` until it eventually closed. Also adds
the requested Live-view motion indicator.

### Fixed

- **A span that stays open for a long time (a genuinely active scene, or an oversensitive zone)
  never appeared in `MotionSpans` until it eventually closed.** `MotionSpanCompleted` only fires on
  close — a span that's been open for minutes reported nothing the whole time, so the timeline
  looked exactly like "no motion ever happened" even though detection was working correctly. This
  is also the most likely explanation for recording looking continuous under Motion mode: if a zone
  is active often or continuously, the keep/discard window (which reads live in-progress state, not
  the `MotionSpans` table) is satisfied almost all the time, so almost nothing gets discarded —
  correct behavior for a genuinely active scene, but easy to mistake for the discard mechanism not
  working when there was no visible confirmation motion was ever being detected at all.
- Fixed by having `NodeWorker` checkpoint every currently-open span into `MotionSpans` roughly every
  15s (`MotionSession.GetInProgressSpans`/`MotionHysteresis.CurrentInProgressSpan`, both pure
  snapshot queries that don't close anything), and having `NodeService.RecordMotionSpansAsync`
  upsert by `(CameraId, ZoneId, StartUtc)` — since that triple never changes for one span once
  confirmed, repeated checkpoints extend one row's `EndUtc` instead of piling up a new row every
  15s a span stays open.

### Added

- **Live-view motion indicator** — a red "● Motion" badge on a camera's tile in `Pages/Live` while
  any of its zones has an open span, polled from `GET /api/cameras/motion-state` every 5s (not
  pushed over the live WebSocket — motion state changes on the order of seconds, a lightweight poll
  fits better). Deliberately derived from the same `MotionSpans` table the timeline reads (via the
  checkpoint fix above), not a new proxy round-trip to each camera's node — no new node endpoint,
  no new media-token family.

### Known limitations

- Still unverified against a real camera — this pass in particular has never been watched trip
  against an actual motion event; the checkpoint timing (does the badge/timeline update promptly
  and clear promptly once motion actually stops?) needs a camera to confirm.

## [0.24.0] - 2026-08-09

M8 pass 3 — corrects a real gap in 0.23.0: pre-roll never actually worked, and pre-roll/post-roll
are now two separate settings instead of one, both configurable globally and per camera.

### Fixed

- **0.23.0's "pre-record falls out for free" claim was wrong.** The single `MotionPaddingSeconds`
  window was checked *immediately* when a segment completed, using only motion observed *before*
  that instant — it could never know about motion that hadn't happened yet. In practice this meant
  a segment finishing just before motion started was already deleted by the time that motion
  arrived, the opposite of pre-roll. Continuous recording writing the bytes to disk was never the
  missing piece; *deciding to keep them before knowing the future* was, and 0.23.0's immediate
  decision couldn't do that. Real pre-roll requires deferring the decision — see Changed below.

### Changed

- **`Recording.MotionPaddingSeconds` split into `Recording.MotionPreRollSeconds` (default 10) and
  `Recording.MotionPostRollSeconds` (default 30)** — both resolved the same Camera → Node → Global
  chain as every other recording setting, both editable on `Cameras/Edit` next to Recording Mode.
  Pre-roll and post-roll answer genuinely different questions (how much lead-up before an event vs.
  how much tail after it) and there's no reason an operator would want them equal.
- **A Motion-mode segment's keep/discard decision is now deferred**, not made the instant the
  segment completes: `NodeWorker` holds it until `PreRollSeconds` after the segment ends, then
  checks whether any zone had activity anywhere from `PostRollSeconds` *before* the segment ended
  through `PreRollSeconds` *after* it ended. Both cases — "this segment is the tail of an event that
  already happened" and "this segment turned out to be the lead-up to an event that hadn't started
  yet" — collapse into one timestamp comparison, since `MotionHysteresis.LastMotionAtUtc` only ever
  moves forward: `MotionSession.HasMotionSince(threshold)`. This is what makes correct pre-roll
  possible without literally buffering frames in memory — see `MotionSession.HasMotionSince`'s and
  `PendingMotionSegmentDecision`'s doc comments for the full reasoning.
- `NodeWorker.ShouldDiscardSegment` — the actual three-boolean keep/discard rule — is unchanged; only
  how its `hadMotionInWindow` argument gets computed changed (deferred `HasMotionSince` check instead
  of an immediate one). Its existing unit tests needed no changes.

### Known limitations

- Still unverified against a real camera — same standing caveat as pass 2. This fix specifically
  needed a genuinely new test (`SegmentThatCompletesBeforeMotionStartsIsKeptOnceMotionArrivesWithin
  PreRoll`) to prove the pre-roll math is sound in isolation; whether the deferral timing behaves
  correctly against a real 5fps motion feed and real segment rotation still needs a camera to watch.

## [0.23.0] - 2026-08-09

M8 pass 2 — motion actually controls recording. 0.22.0 shipped motion *detection*; this closes the
gap it explicitly left open ("motion is detected and shown on the timeline; it does not yet control
recording").

### Added

- **`Recording.Mode` setting** (`Continuous` | `Motion`), resolved per camera through
  `ISettingsResolver` (Camera → Node → Global → `Continuous` default) — set on `Cameras/Edit`
  alongside the existing Retention override, same "own override + effective resolved value" pattern.
  Only these two modes exist; `Schedule`/`Event` from the plan's original four-mode design remain
  unbuilt (no `Schedules` table, no ONVIF event ingestion — same gap 0.22.0 already flagged).
- **`Recording.MotionPaddingSeconds`** (default 30), same resolution chain — how long after a
  camera's Motion zones go quiet a segment is still kept as context.
- **Segment-level gating on the node.** A Motion-mode camera still records continuously to disk
  (`-c copy`, unchanged, no new process-lifecycle risk to the already-verified M3 recording engine)
  — what's new is what happens to a segment once it's finished: `NodeWorker` asks the camera's
  already-running `MotionSession` whether any zone had activity within the padding window: kept and
  reported as normal if so, deleted immediately (never reported, so no `Segments` row is ever
  created) if not. This is a deliberate deviation from the plan's original design (an in-memory
  pre-record ring buffer with process start/stop on trigger) — same spirit, much less new risk: it
  reuses the entire continuous-recording pipeline instead of adding ffmpeg start/stop lifecycle
  management. The cost is the (cheap, I/O-only) continuous write itself, which every camera already
  pays today.
  **Correction (0.24.0):** the line that stood here originally claimed the pre-record effect "falls
  out for free since recording never actually stopped" — that's wrong. The decision above was made
  *immediately* on completion, using only motion observed so far, so it could never credit a segment
  for motion that hadn't happened yet; a segment finishing just before an event started was already
  gone by the time that event arrived. Continuous writing was never the missing piece — deferring the
  keep/discard decision was. See 0.24.0.
- **Fails safe.** `Recording.Mode` defaults to `Continuous`, so no existing camera's behavior changes
  without an explicit opt-in. A Motion-mode camera with no enabled Motion zone (or no Sub stream)
  never discards anything — it behaves exactly like Continuous and logs one warning, rather than
  silently deleting everything it records. `Cameras/Edit` surfaces this directly: selecting Motion
  mode without a zone configured shows an explicit warning with a link to set one up.

### Known limitations

- **Unverified against a real camera.** This is the piece of M8 most worth distrusting until it's
  been watched happen on real footage — it decides what gets *permanently deleted*. The core
  decision rule (`NodeWorker.ShouldDiscardSegment`) is unit tested and biased hard toward
  over-keeping on any doubt, but the padding-window timing itself (does a segment near a motion
  boundary actually get kept the way it's supposed to?) has not been checked against a real motion
  event. Recommend verifying on one low-stakes camera before trusting this broadly.
- **No global admin page for `Recording.Mode`/`Recording.MotionPaddingSeconds` defaults** —
  `Admin/Settings` (a generic key-value editor) doesn't exist yet, same as it didn't for any other
  setting; the global default is whatever `Continuous`/30s the compiled-in fallback provides unless
  a camera or node overrides it. Retention has its own dedicated `Admin/Retention` page for this
  reason; Recording.Mode doesn't have an equivalent yet.
- **Segment-boundary precision isn't frame-accurate.** The keep/discard decision happens once per
  60s segment, checked shortly after that segment completes — motion starting right at a segment's
  tail end is more likely to be caught by the *next* segment's own decision (motion still active
  then) than to retroactively save the one already decided. In practice this means the segment where
  motion visibly starts is very likely kept, but exact frame-level boundaries aren't guaranteed.



M8 pass 1 (Motion, events, metadata) — same "ship a real, working pass, flag what's deferred"
approach M6 and M7 both used. This pass covers server-side motion detection, zones, and timeline
motion coloring. Camera-side motion push, ONVIF PullPoint event ingestion, and bounding-box metadata
are explicitly **not** in this pass — see Known limitations below.

**Fixed post-release, before this ever deployed successfully:** the initial migration failed —
SQL Server rejected `MotionSpans.ZoneId`'s `SET NULL` foreign key with "may cause cycles or multiple
cascade paths." `Zones.CameraId` and `MotionSpans.CameraId` both cascade from `Cameras`, so a
`SET NULL` on `MotionSpans.ZoneId` (reachable via `Zones`) created a second path SQL Server won't
allow alongside the direct `CameraId` cascade, regardless of the second path being `SET NULL` rather
than `CASCADE`. Fixed by changing that FK to `Restrict` (`NO ACTION`) and moving the null-out into
`ZoneService.DeleteAsync` — same end state (deleting a zone keeps its motion history, just clears the
attribution), just not expressed as a DB-level cascade. Verified by generating the actual migration
SQL and confirming `ON DELETE NO ACTION` in the DDL — no live SQL Server reachable from this
environment to test the constraint directly.

### Added

- **Zones** (`Pages/Cameras/Zones`) — one polygon editor for all four zone kinds (Motion-server,
  Ignore, Motion-camera, Privacy), drawn on a live snapshot from the camera. Click to place vertices,
  click the first point again (or double-click) to close the shape. Editing an existing zone's
  name/kind/sensitivity/enabled works in place; reshaping its polygon does not — delete and redraw
  (a full vertex-drag editor is its own scope, not blocking the rest of this pass). `IZoneService`/
  `Zone` entity, `Zones`/`MotionSpans` tables.
- **Snapshot capture** — `GET /api/cameras/{id}/snapshot`, a one-shot still-frame grab from a
  camera's Main stream, proxied through the web tier the same way `/live` and `/playback-segment`
  are. Built as a Zones prerequisite but also closes an M5 backlog item ("snapshot/still capture")
  that was never implemented.
- **Server-side motion detection** — a node opens a second, independent RTSP session against a
  camera's Sub stream (the first real consumer of Sub in this codebase; Main is recording-only and
  Live's auto-switch-to-Sub was never implemented), reads it as fixed-size 320x240 grayscale frames
  at 5fps, and diffs consecutive frames per enabled ServerMotion zone (`MotionDetector.Score`).
  Per-pixel changes are masked to each zone's polygon, with any Ignore-zone polygons on the same
  camera subtracted from every ServerMotion zone's mask first — a pixel inside an Ignore zone never
  counts toward motion for any zone, not just the one it happens to overlap. `MotionHysteresis`
  debounces per-zone (default: 1s to confirm a span has started, 3s of quiet to close it) so a single
  noisy frame can't open and close a span on its own. Completed spans are batch-reported to the web
  tier and land in the `MotionSpans` table, the same shape `Segments` reporting already uses — a
  span is only written once it closes, so real write volume looks like Segments' (a handful of rows
  per interesting event), not the per-frame volume the original plan flagged `SqlBulkCopy` for.
- **Timeline motion coloring** — buckets with motion now render bright green, taking priority over
  blue (recorded) for a bucket with both, matching the plan's original color spec. `HasMotion` is
  independent of `HasRecording` in `TimelineBucketDto`; both the per-camera and merged-across-cameras
  timelines pick it up automatically since they already share `TimelineService.Bucket`.

### Known limitations

- **Motion is detected and shown on the timeline; it does not yet control recording.** A camera set
  to a future "Motion" recording mode still records continuously — wiring motion to actually
  start/stop the Main recording is a separate, riskier change to `RecordingSession`'s lifecycle that
  deserves its own real-camera verification pass, deliberately not bundled into this one.
- **CameraMotion zones do nothing yet** — pushing a polygon to the camera itself over ONVIF
  `SetVideoAnalyticsConfiguration` is real per-vendor work, not started. The editor lets you draw and
  save one regardless (`PushedToCameraAt` stays null), so the one UI is ready for it later.
- **Privacy zones do nothing yet** — no server-side burn-in on the transcode path. Stored only.
- **No ONVIF PullPoint event ingestion** — `CameraEvents`/`Detections` tables don't exist yet;
  bounding boxes and camera-triggered recording are still out of reach.
- **Unverified against a real camera and browser.** Sub-stream RTSP is genuinely new ground in this
  codebase (see "Added" above), and the zone editor's canvas drawing has had no hands-on testing —
  same caveat M7 pass 1 shipped with, flagged the same way, for the same reason (no camera/browser
  available in this environment). 25 new unit tests cover the parts that don't need one:
  `MotionDetector`'s frame-diff arithmetic, `MotionHysteresis`'s debounce state machine (hand-traced
  against the implementation, not just written alongside it), `ZoneRasterizer`'s polygon-to-mask
  conversion, and the new motion-bucketing behavior in `TimelineService`.

## [0.21.2] - 2026-08-09

### Added

- `fix-legacy-segments.ps1` — repairs footage recorded before the 0.21.1 recorder fix so it plays
  back in a browser. Rewrites each affected segment's container in place with `-c copy` (no decode,
  no re-encode, no bitstream or quality change), adding the `default_base_moof` flag that MSE
  requires. Dry run unless `-Apply` is passed; detects and skips files that already carry the flag,
  so it's idempotent; skips the segment the recorder is currently writing (both a recency window and
  a write-lock check) so it can run against a live node without stopping the service; verifies each
  remuxed file actually carries the flag before it replaces anything, leaving the original untouched
  on any failure; and preserves original file timestamps, which matter because segment start/end
  times are derived from file metadata rather than the filename. Database rows are unaffected since
  every file is rewritten at its existing path.

  Resolves ffmpeg itself rather than requiring a path: an explicit `-FfmpegPath`, then the recorder
  node's own bundled copy, then `PATH`, then `-InstallFfmpeg` to download the LGPL shared build.
  That download goes straight to BtbN's GitHub release rather than through winget, which
  `install-node.ps1` uses but which is frequently unavailable on a file server (App Installer isn't
  present on Windows Server by default and the Store isn't an option). A dry run needs no ffmpeg at
  all, and `-StorageRoot` takes a UNC path — so the least-setup option is running it from a recorder
  node against the share, with nothing installed on the file server.

### Changed

- `deploy.ps1` now builds the recorder node package (`build-node.ps1`) as part of every web
  deploy, so `publish\LarisVMS.Node\win` stays current instead of depending on someone remembering
  to run `build-node.ps1` separately. New `-ExtraNodePublishPath` passes straight through to
  `build-node.ps1 -ExtraPublishPath` for mirroring the package to a second location (e.g. a network
  share a recorder machine reads directly); left blank by default since the path is inherently
  environment-specific. New `-SkipNodeBuild` opts back out for a web-only deploy.

### Fixed

- `TimelineService.NormalizeToUtc`'s doc comment asserted that ASP.NET binds a Z-suffixed
  query-string `DateTime` as `Kind=Local` converted to the server's zone, and that this was the
  cause of playback never finding a segment. Both claims were wrong — the original finding came from
  a scratch `DateTime.TryParse` call, which is not what minimal APIs use; re-checked against a real
  running minimal API, a Z-suffixed value binds as `Kind=Utc` with no shift, and playback's actual
  cause was the recorder's muxer flags (see 0.21.1). The method itself is unchanged and still
  correct — it does real work for any caller omitting the trailing Z — but it's no longer documented
  as load-bearing for a bug it never fixed.

- `install-node.ps1`'s network-storage warning claimed LocalSystem "CANNOT access a network
  (\\server\share) storage root", which overstates it — LocalSystem authenticates to the network as
  the machine's own computer account (`DOMAIN\COMPUTERNAME$`), and that account can read/write an
  SMB share perfectly well once granted share + NTFS permissions on it, no `-ServiceCredential`
  needed. The warning (and the doc comment above it) now says permissions need to be set up for the
  computer account when running as LocalSystem, instead of implying it's simply impossible.

## [0.21.1] - 2026-08-09

### Fixed

- Recorded segments could not be decoded by a browser at all, which is why Playback never showed
  video. The recorder's ffmpeg `-f tee` writes two legs — a pipe leg feeding live view and a segment
  leg writing the files kept on disk — and `default_base_moof` was set only on the pipe leg. It was
  correct when written (live view was the only MSE consumer; the recorded files were just files),
  but M7 then started feeding those same files to MSE and inherited the gap. Without that flag a
  fragment's sample offsets are file-relative rather than relative to its own `moof`, which the MSE
  byte-stream format does not accept: Chrome takes the append, fires `updateend` normally, and
  produces no buffered range at all — no error, no event, nothing in the console. Verified by
  parsing the `tfhd` flags out of both legs' real output (pipe leg `0x020038` with
  default-base-is-moof set, segment leg `0x000039` with base-data-offset-present instead) and
  confirming the flag makes the segment leg byte-structurally identical to the pipe leg. Both legs
  now share one flag constant so they cannot drift apart again, covered by a unit test.

  **Only newly recorded segments are affected.** Footage already on disk was written without the
  flag and stays unplayable in the browser; it is still valid MP4 and plays fine in VLC, ffplay, or
  any normal player. Playback in the browser will work for anything recorded after the recorder
  nodes are updated.

  **This is a `LarisVMS.Node` change** — `deploy.ps1` does not push it. Re-run `install-node.ps1`
  on every recorder node for it to take effect.

## [0.21.0] - 2026-08-09

### Fixed

- Playback tiles showed "Loading…", then went blank and stayed blank, with nothing in the browser
  console. The MSE append was actually succeeding — the seek afterwards was the problem. A tile
  seeked to a raw offset from the segment's wall-clock start, which assumes the recorded fMP4's
  internal timeline begins at zero; these segments carry the `baseMediaDecodeTime` they were written
  with, so the buffered range can start at an arbitrary large value instead. The seek then landed
  outside the buffered range entirely and the element rendered nothing — with no error, no event,
  and the status text clearing normally, which is why it looked identical to a tile that had simply
  given up. Tiles now seek relative to the actual buffered start and clamp into the buffered range.
- Playback never auto-advanced to a camera's next segment when one finished. `MediaSource.endOfStream()`
  was never called after appending a segment, so the MediaSource stayed `open`, the media element
  kept waiting for more data that would never arrive, and the `ended` event the auto-advance depends
  on could not fire. The full segment is appended in one shot and is self-contained, so the stream is
  now explicitly ended once the append completes.
- Timeline drew recorded (blue) coverage in the future when sitting at "now". The future-clamp added
  in 0.19.0 capped the wrong thing: it clamped the visible window's right edge rather than the
  playhead, which quietly pinned the marker half a span into the past — at the 24h default the marker
  sat at now-12h with twelve hours of real, still-being-recorded footage drawn to its right. The
  clamp now applies to the playhead itself, so the marker is "now" when scrubbed fully forward.

### Changed

- Timeline now renders the stretch after "now" as unreachable future (flatly darker than the
  empty-track color, with a divider at the current instant) instead of leaving it looking like time
  that merely hasn't been recorded yet. A bucket straddling "now" is truncated at it, so an actively
  recording camera's newest bucket no longer paints coverage over time that hasn't happened.
- Zooming a timeline no longer triggers a seek. The playhead's clamp no longer depends on the zoom
  level, so zooming can't move the play position and shouldn't re-seek every camera per scroll notch.

## [0.20.0] - 2026-08-09

**No `LarisVMS.Node` changes in this release — no recorder-node update needed.**

### Fixed

- A Playback tile's status text had a blind spot: it's only ever set once a segment is actually
  found ("Loading…", while its bytes download) or once the lookup conclusively finds nothing ("No
  recording…"). The lookup itself — fetching the segment list to work out which file covers the
  requested instant — set nothing at all while in flight, so a tile looked identically blank
  whether that resolved in 50ms or never resolved at all. Now shows "Looking for a recording…"
  immediately when a seek starts. A genuine fetch failure during that lookup was also silently
  swallowed with no console output; both the non-OK-response and thrown-exception paths now log to
  `console.error` and set a status message instead of leaving the tile blank with no trace of why.

## [0.19.0] - 2026-08-09

**No `LarisVMS.Node` changes in this release — no recorder-node update needed.**

### Fixed

- Chased a reported "timeline shows 12 hours in the future" as a suspected timezone/data bug
  through several dead ends (query-string `DateTime` binding — verified with a real minimal API
  test harness, not just a scratch simulation, and it's already correct: `Kind=Utc`, exact value;
  recorder node clocks — confirmed correct; web server clock — confirmed correct, and it's one of
  the node machines) before finding the real explanation: it was never a data bug. The timeline's
  default view is a 24-hour window *centered* on the playhead — when the playhead starts at "now"
  (the common case), the right half of that window is mathematically always ~12 hours into the
  future, because nothing sits to the right of "now" but time that hasn't happened yet. Correct
  math, confusing default. `timeline.js` now clamps so neither timeline can scroll or zoom past the
  real current instant at all — there's never a recording there, so a view that could show one just
  looked broken.

### Added

- The Playback timelines now remember your scrub position and zoom level between visits
  (`localStorage`, keyed independent of which view is selected) — reloading the page resumes where
  you left off instead of jumping back to "most recent recording." That default only applies until
  a position has ever been saved.
- A 24-hour clock toggle for both timelines' time labels (`timeline.js` gained `setHour24`),
  persisted the same way. Also finished wiring this — the previous release only built the
  internal formatting support and never added the actual UI control, so the toggle didn't exist
  anywhere to click.

### Added

- The two Playback timelines (selected-camera and merged-across-all-cameras) now stay in sync on
  both time position and zoom level — scrubbing or wheel-zooming either one mirrors onto the other,
  via new `setRange`/`getRange` on `timeline.js`'s instance API alongside the existing `setCenter`.
  All three are no-callback setters so mirroring a change from one timeline to the other can't
  bounce back and re-trigger itself.

## [0.17.0] - 2026-08-08

**No `LarisVMS.Node` changes in this release — no recorder-node update needed.**

### Fixed

- **Playback never loaded any video, root cause found: every timeline/segment range query was
  silently shifted by the server's UTC offset.** ASP.NET's query-string binding parses a value like
  `2026-08-08T21:45:31.890Z` into a `DateTime` with `Kind=Local`, *converted* to the server's zone —
  verified directly: on this UTC-7 host it binds as `14:45:31 Local`, seven hours off. SQL Server's
  `datetime2` carries no offset, so EF sent that shifted wall-clock value straight into
  `WHERE StartUtc < @to AND EndUtc > @from` and every range query searched the wrong window. The
  failure was asymmetric, which is why it was hard to spot: the *timeline* still looked fine (its
  whole visible window shifts together, so it just looks like a different stretch of footage), but
  the per-instant segment lookup returned segments hours away from the requested time, so
  `findSegment()` never matched and no tile ever loaded a video — matching the reported "blank cells
  / stuck on Loading…" exactly. `TimelineService` now normalizes every incoming range bound through
  a new `NormalizeToUtc` before it touches the database (Local → converted back to the true instant;
  Utc → untouched; Unspecified → stamped UTC rather than assumed local, so a client that omits the
  trailing `Z` can't reintroduce the same shift). Covered by 4 new unit tests, including an
  end-to-end one that passes a range the exact way binding delivers it and asserts the segment is
  still found — both new tests were confirmed to fail against the unfixed code before the fix landed.
- The `AbortError` console noise introduced by 0.16.0's fetch cancellation is gone. Aborting a fetch
  mid-body-read rejects at `resp.arrayBuffer()`, not at `fetch()` — that call sat outside the
  try/catch, so every superseded drag-scrub surfaced an "Uncaught (in promise) AbortError". It's now
  inside the guard, and the tile's public `seekTo` additionally swallows expected aborts so a
  rejection can never escape as an unhandled promise (no caller awaits it).

## [0.16.0] - 2026-08-08

**No `LarisVMS.Node` changes in this release — no recorder-node update needed.**

### Fixed

- Found the real cause of Playback's "stuck on Loading…" symptom, confirmed live via
  `net::ERR_INSUFFICIENT_RESOURCES` in the browser console: the 0.15.0 tape-scrubber redesign fired
  `onScrub` on **every raw `mousemove` event** while dragging — a browser dispatches those at a far
  higher rate than any seek pipeline can use, so a half-second drag fired 50-100+ scrub calls, each
  fanning out to one fetch per camera in the view. With several cameras that flooded the browser's
  connection pool almost instantly; what looked like a recorder node "grinding" on a slow SMB read
  was actually storage trying to keep up with hundreds of already-abandoned, superseded requests,
  not a single hung one. Two-part fix: (1) `timeline.js` now throttles live-scrub firing during a
  drag to ~8/sec instead of every mousemove tick (still always fires once more, unthrottled, on
  mouseup so the exact release position is never missed); (2) `playback-player.js`'s per-tile player
  now actually **cancels** a superseded fetch via `AbortController` instead of only marking its
  eventual result stale — the previous token-check-on-arrival guard stopped a stale response from
  being *applied*, but never stopped the request itself from running to completion and holding a
  connection slot the whole time.
- Playback tiles are now click-to-select-primary-camera on the whole cell (video, background, zoom
  buttons — all bubble up), not just the small camera-name label from 0.15.0.
- The Web-tier's `/playback-segment` proxy now applies a 25s timeout to its call to the recorder
  node (was `HttpClient`'s 100s default) — a genuinely stuck/very slow storage read now surfaces as
  a clear timeout error within half a minute instead of leaving the browser's "Loading…" up
  indefinitely with no feedback.

## [0.15.0] - 2026-08-08

### Fixed

- Playback's initial playhead defaulted to `Date.now()` (literally "right now"), which is almost
  never covered by an actual recording — footage is always somewhat behind live, and nothing may
  be recording at all in a dev/test setup. Every tile's first seek found no segment, nothing ever
  loaded into a `<video>`, and pressing Play had no source to play — confirmed live as the reported
  "blank cells, nothing plays" symptom. Selecting a view now resolves the most recent actual
  recording (looked up per the view's primary camera, last 7 days) and starts there instead,
  same as a DVR defaulting to "most recent footage" rather than a bare clock reading.

### Changed

- Bootstrap (5.3.3 → **5.3.8**) and GridStack (**12.6.0**, pinned — was the floating `@12` CDN tag)
  are now vendored locally under `wwwroot/lib/` instead of loaded from `cdn.jsdelivr.net`, matching
  frcastr's existing pattern for Bootstrap. `SecurityHeadersMiddleware`'s CSP no longer allow-lists
  `cdn.jsdelivr.net` in `script-src`/`style-src`/`font-src` — nothing needs it anymore, and the app
  now works with zero outbound requests from the browser. This also eliminates the CSP-blocked
  `.map` sourcemap-fetch console warnings the CDN's own files triggered (harmless noise, but noise
  a user reported while diagnosing the playback bug above — same session, easy to conflate with a
  real error).
- **Playback timeline redesigned as a tape scrubber.** The yellow playhead marker is now always
  drawn at the horizontal center of the canvas and never moves; dragging slides the timeline strip
  underneath it instead, live-seeking every tile continuously as you drag rather than just panning
  the view and leaving playback wherever it was. Wheel-zoom now anchors on the playhead (always the
  center point) instead of the cursor, so zooming can't shift the marker off-center. A plain click
  still jump-seeks straight to the clicked instant. Both timelines (per-camera and the merged one)
  stay recentered on the same shared playhead during normal playback too, not just while dragging —
  the strip visibly scrolls as the video plays.
- Playback tiles are now clickable to change which camera drives the per-camera timeline — click a
  tile's camera-name label (⭐ marks the current one, tile gets a highlighted border) instead of
  always being stuck with the view's top-left-most camera.

## [0.13.0] - 2026-08-08

### Fixed

- Playback's per-tile digital-zoom button group (−/⤢/+) covered the entire video tile instead of
  sitting as a small corner overlay — the exact same Bootstrap `.ratio > *` bug Pages/Live's mute
  button hit (see 0.7.0): the button group was a *direct child* of the `.ratio` container, so
  Bootstrap's rule forced it (and its buttons) to `position:absolute;width:100%;height:100%`.
  Fixed the same way: video/status/buttons now live inside one plain inner wrapper div, the only
  direct `.ratio` child.

### Changed

- **Playback is now driven by saved Views, not an ad-hoc camera picker.** Pick a view and its cell
  layout (positions, aspect ratios, cameras — the same arrangement you'd watch live) renders with
  playback video instead of live video. `Pages/Playback`'s camera checkboxes are gone; a view
  dropdown replaces them, and the tile grid is now positioned by the view's own `x/y/w/h` (CSS
  grid) instead of a uniform Bootstrap column layout. The view's top-left-most camera (reading
  order, same convention `Pages/Views/Play` uses for its mobile layout) drives the per-camera
  timeline.
- `Pages/Live` gained a view picker too: choosing a saved view sends you to `Pages/Views/Play`
  (M6's already-built "watch a saved layout live" page, complete with kiosk mode and tours) instead
  of duplicating view-rendering logic on the Live page itself. The page's own flat all-cameras grid
  is unchanged and stays as the default/no-view-picked fallback.

### Added

- A second, merged timeline on `Pages/Playback`: `GET /api/timeline?from=&to=&buckets=` (new
  `TimelineService.GetGlobalBucketsAsync`) reports a bucket as recorded if *any* camera has footage
  there, not just the one driving the per-camera timeline — useful for finding when something
  happened before you know which camera caught it. Both timelines share the same playhead and
  scrubbing either one seeks every tile. No motion aggregation yet, same M8 dependency as the
  per-camera timeline's missing motion coloring — there's no `MotionSpans` data anywhere in the
  system yet for either timeline to draw from.
- 2 new unit tests for the merged-timeline bucketing (a bucket recorded if any camera covers it;
  all-gap when nothing has ever recorded).

## [0.12.0] - 2026-08-08

### Added

- **M7 (pass 1) — Playback & timeline.** Scrub a camera's recorded history and play it back,
  synchronized across multiple cameras.
  - `GET /api/cameras/{id}/timeline?from=&to=&buckets=` — coverage buckets (recorded/gap) for the
    canvas timeline, backed by `TimelineService` reading `Segments`. No motion coloring yet —
    that's M8 (`MotionSpans` doesn't exist until then).
  - `GET /api/cameras/{id}/segments?from=&to=` — the segment list a player resolves "which file
    covers this instant" against.
  - `Pages/Playback`: a camera picker (first checked camera drives the timeline; every checked
    camera plays in lockstep, each resolving its own recordings/gaps independently), a canvas
    timeline (`timeline.js` — wheel to zoom weeks→seconds around the cursor, drag to pan, click to
    scrub), one MSE player per selected camera (`playback-player.js`), and per-tile digital zoom
    (CSS transform scale/pan on the `<video>` element, drag to pan once zoomed).
  - `/playback-segment/{cameraId}/{segmentId}` on `LarisVMS.Web` proxies exactly one segment
    file's bytes from its owning node — same "browser never talks to a node directly" shape as
    `/live`, over HTTP GET instead of a WebSocket. `MediaToken` gained `IssueForSegment`/
    `TryValidateSegment`, a separate token family from the live-view one (deliberately — the
    already-verified `/live` path wasn't touched) that binds a token to one exact file path.
  - Node gained `GET /playback-segment/{cameraId}` (serves the file, validated against the token
    and a directory-prefix check) and `NodeWorker.StorageRoot` (so that endpoint can resolve the
    same `cam-{id}/main` path `RecordingSession` writes to, without a second source of truth for
    where recordings live).
  - **Architecture choice, not a corner cut:** the plan describes `/playback` as one
    server-side-synthesized, timestamp-rebased fMP4 stream spanning multiple segments. This pass
    does it differently — each segment file is already a self-contained fMP4 (its own
    ftyp+moov+moof+mdat), and MSE natively accepts a new initialization segment mid-stream, so a
    player just fetches one segment's full bytes per seek/advance and appends them. No server-side
    MP4 box rewriting needed. Trade-off: a brief decode restart at every segment boundary instead
    of frame-perfect continuity — see Known limitations.
  - 12 new unit tests: `TimelineService`'s bucketing (including the boundary case where integer
    bucket-tick division would otherwise leave a sliver of the requested range uncovered) and
    segment/playback-lookup queries, plus `MediaToken`'s new segment-token family (round-trip,
    camera/path/expiry mismatches, and confirming a live-view token doesn't validate as a segment
    token or vice versa).

### Known limitations (M7 pass 1)

- **Nothing in this pass has been exercised in a real browser** — no browser is available in this
  environment. Unlike M5 (which needed five rounds of real-browser debugging before it actually
  worked despite looking correct on paper each time — codec mismatches, seek bugs, audio track
  issues), this shipped on code review, build, and the 12 unit tests above only. Canvas rendering,
  wheel-zoom math, the MSE multi-segment player, and synchronized playback are all exactly the kind
  of thing that could need real debugging before they work at all. Test before relying on this.
- No hover thumbnails on the timeline — the plan's `/thumb/{cameraId}/{ts}` endpoint and the
  scrub-preview JPEGs it would serve were never actually built in M3 despite being in the original
  storage-layout sketch; adding them is its own scope (frame extraction at arbitrary timestamps),
  not something this pass could fold in.
- Segment-boundary playback restarts the decoder for each new file rather than gapless continuity
  — see the architecture-choice note above.
- No frame-accurate synchronization across tiles during continuous playback — every tile seeks to
  the same wall-clock instant, but each `<video>` then plays natively at 1x on its own; the
  timeline's playhead is driven by the primary tile's own `currentTime`, not a shared driving
  clock, so long playback runs can drift slightly between tiles.
- Digital zoom is CSS transform scale/pan on the video element, not a server-side crop/re-encode —
  zooms into whatever resolution the stream already is.

## [0.11.0] - 2026-08-08

### Changed

- `Pages/Live` no longer shows the "Every camera below connects automatically..." explainer
  paragraph — it was accurate but not something a user needs told every time the page loads.

## [0.10.0] - 2026-08-08

### Added

- Recorder nodes now resume recording on their own after rebooting during a central-server/DB
  outage, instead of sitting idle until the server answers again. `node.config` gained a
  `CachedConfig` field holding the most recent successful camera-config response (camera list,
  RTSP URIs, credentials, resolved retention/quota) — DPAPI-protected the same way the node's
  registration secret already is, since it carries camera credentials — persisted after every
  successful reconcile. If a `GetConfigAsync` call fails while nothing is recording yet (the
  cold-start-during-an-outage case), the node falls back to that cached config and starts recording
  from it immediately; the moment the server answers for real, it reconciles against the fresh
  response as normal. An already-*running* node hitting an outage was never affected by this gap —
  a failed reconcile cycle has always left active recording sessions alone — this only closes the
  restart-during-outage case.

### Known limitations

- `StorageManager` (retention/quota/watermark eviction) has the same "depends on reaching the
  server" shape and doesn't get the cache fallback this pass — a long outage still pauses cleanup on
  affected nodes until the server is reachable again. Left out deliberately: this is a disk-usage
  concern, not a recording-continuity one, and didn't need to block the fix above.
- No video is ever lost either way, cache or no cache: `RecordingSession` always rescans its whole
  output directory from empty state whenever a session (re)starts, so segments written while
  disconnected still get indexed once the node reconnects — this change is purely about *starting*
  recording sooner during an outage, not about recovering data that was already safe.

## [0.9.0] - 2026-08-08

### Added

- **M6 (pass 1) — Views & layout editor.** Save a camera-wall layout and open it later instead of
  rebuilding it every visit.
  - `View` entity (`Pages/Views/{Index,Editor,Play}`): name, owner, shared/personal, a GridStack
    cell layout (`LayoutJson`), and a tour interval. Personal views are only editable/deletable by
    their owner; shared views are visible and editable by anyone with `Views.View`/`Views.Edit`
    (seeded onto the Viewer role — building your own camera wall doesn't need elevated access).
  - `Pages/Views/Editor`: GridStack drag-to-arrange and resize, a camera palette to add tiles
    (click-to-add — see Known limitations), a per-cell aspect-ratio dropdown (the plan's full list:
    1:1, 4:3, 3:2, 16:10, 16:9, 1.85:1, 21:9, 2.39:1, and the vertical inverses), and a per-cell
    "hide on phone" toggle. Every tile plays real live video via the same `larisvmsLiveView` player
    `Pages/Live` uses, letterboxed to its aspect ratio with `object-fit: contain` regardless of the
    grid rectangle's actual shape, so dragging/resizing a tile never distorts the picture.
  - `Pages/Views/Play`: read-only display of a saved view. Desktop renders the exact saved
    geometry with plain CSS grid (no GridStack JS needed just to display a fixed layout). Phones
    get a **derived** layout — cameras ordered top-to-bottom/left-to-right from the desktop
    positions, stacked one or two columns (`mobileTwoColumn`), sized from each cell's aspect ratio,
    cells flagged "hide on phone" skipped — never persisted, so a view built on desktop always gets
    a usable phone layout for free.
  - Fullscreen kiosk button on `Pages/Views/Play` (real Fullscreen API, not just a URL flag) —
    hides the nav bar and toolbar while active for a clean wall-mounted display.
  - "Start tour" on `Pages/Views/Index`: cycles through every shared view that has a tour interval
    set, each shown for its own interval (default 15s if unset when reached directly), looping.

### Known limitations (M6 pass 1 — tracked for a follow-up pass, not silently dropped)

- Adding a camera to the grid is click-to-add from the palette, not drag-and-drop onto a cell —
  GridStack's own drag/resize for arranging *already-added* tiles works normally; only the initial
  "camera → grid" step was simplified. Native drag-in wasn't implemented this pass because it's the
  more failure-prone half of GridStack's interactivity and this environment has no browser to
  verify it in live.
- None of this milestone's UI (editor drag/resize, aspect-ratio letterboxing, the derived mobile
  layout, kiosk fullscreen) has been visually confirmed in a real browser yet — verified by build +
  the existing test suite only, same caveat as always applies to browser-only behavior with no
  browser available to check it in.
- `Pages/Views/Index` shows "You" vs "another user" for a shared view's owner rather than their
  actual name — no `UserManager` lookup wired into the list query yet.
- Per-camera ACL (`CameraAccess`) isn't wired into view visibility — a shared view shows every
  camera in its layout to anyone who can see the view, same as `Cameras/Index` today. Consistent
  with the rest of the app: `CameraAccess` has been schema-ready since M1 but isn't enforced by any
  page yet, so adding enforcement just for Views would be inventing a check nothing else has.

## [0.8.0] - 2026-08-08

### Added

- Dark mode toggle (🌙/☀️ nav-bar button), completing the M1 groundwork (`data-bs-theme`,
  anti-flash inline script) with the visible switch and `theme.js` logic the plan always called
  for. Defaults to the system's `prefers-color-scheme` ("auto") when no preference is stored yet,
  live-updates if the OS theme changes while auto is in effect, and otherwise persists an explicit
  choice to `localStorage`.
- Every table on `Cameras/Index` and `Admin/Nodes` is now click-to-sort by any column (ascending/
  descending, with a ▲/▼ indicator), and both pages gained a filter box above the table that
  narrows rows by substring match as you type. Both are one small shared, dependency-free IIFE
  each (`sortable-table.js`, `list-filter.js`) — no server round-trip, since these list sizes fit
  comfortably in one page — meant to be applied to every table/list page built from here on, not
  just these two.
- Quick enable/disable on `Cameras/Index`: a 🟢/🔴 button next to each camera's name toggles
  `Camera.IsEnabled` in place, without opening `Edit`. Disabling never touches the camera's group/
  node assignment, credentials, or settings — recording just stops on the node's next reconcile
  cycle (`NodeService.GetConfigAsync` already only hands out enabled cameras), and re-enabling
  resumes with whatever configuration was already there.
- Bulk camera re-pointing: select multiple cameras on `Cameras/Index` via row checkboxes and
  reassign them all to a different node (or unassign) in one action, instead of one `Edit` page at
  a time — the real case this covers is moving a batch of cameras off a node being decommissioned.
  Reassigning never touches `Segment` rows already written under the old node; that footage stays
  attached to whichever node actually recorded it.
- Stale-segment warning (⚠️): a badge next to a camera on `Cameras/Index`, and a per-node count on
  `Admin/Nodes`, shown whenever a camera has `Segment` rows recorded under a `NodeId` other than
  its *current* `Camera.NodeId` — footage still sitting on a node the camera is no longer assigned
  to (surfaced after the bulk re-pointing above, or any manual reassignment). A live query against
  `Segments`, not a stored flag, so it clears on its own once those rows are gone (retention sweep
  or manual delete) with no explicit dismissal step. Covered by three unit tests: flagged while
  present, not flagged when a camera's segments are all on its current node, and clears once the
  stale rows are removed.

This closes out the cross-cutting UI backlog the plan had been carrying since M4/M5 ("must land
before the plan is done") — `Cameras/Index` and `Admin/Nodes` were the two pages that needed the
retrofit; every list page built from M6 onward gets sortable/filterable for free by using the same
convention.

## [0.7.0] - 2026-08-08

### Added

- Live view now connects automatically for every camera as soon as `Pages/Live` loads — no "Watch"
  button anymore. Matches the XProtect-style behavior requested for the eventual view-groups work
  (M6): live streaming should start immediately, not wait for a click.
- Live view auto-reconnects on its own after a dropped/corrupted session (confirmed cause in
  practice: roaming between mesh WiFi access points mid-stream) — any session-ending event (decode
  error, WebSocket close/error) now triggers a clean teardown and a fresh session automatically,
  with backoff (2s, doubling to a 30s cap; resets to 2s once a session has run cleanly for 15s+, so a
  transient blip recovers fast but a genuinely offline camera doesn't get hammered). Previously this
  needed a manual Stop/Watch click to recover — confirmed as the actual fix before this was built.
- Live view mute/unmute button on any camera whose main stream has audio (`CameraStream.HasAudio`).
  Every stream still starts muted regardless of a previous session's state — autoplay-with-sound is
  blocked by browser policy anyway, so starting muted keeps behavior consistent across tiles instead
  of depending on whichever camera happened to be unmuted last.
- `build-node.ps1 -ExtraPublishPath` mirrors the built node package to a second location (e.g. a
  network share a recorder machine can reach directly), so installing/upgrading a node no longer
  depends on manually copying the `publish\LarisVMS.Node\win` folder there each time.

### Changed

- `LarisVMS.Node`'s HTTP client can now skip TLS certificate validation via the existing
  `--insecure-tls` flag / `LARISVMS_INSECURE_TLS` env var without a code change — this was already
  wired end-to-end but undocumented as the recommended fix for a self-hosted server with no cert
  covering its hostname. Both production nodes run with it enabled for now; it should become a real
  settings-driven toggle once the M6+ settings system exists, rather than a service-install-time flag.

### Fixed

- **Live view is now confirmed working end-to-end in a real browser** — both H.264 and HEVC cameras
  play successfully (audio included), closing out the one M5 pass-1 item that had only ever been
  verified via ffprobe against captured ffmpeg output, never an actual browser (see 0.6.0's "Known
  limitations" below, updated accordingly). Getting here took five independent fixes, each masking
  the next until fixed in order: the IIS WebSocket feature missing (HTTP 400 on every request), no
  TLS cert covering the server's hostname for the node's own connection, wrong codec family selected
  (H.264 tried before checking the camera's actual codec), the audio track undeclared in the
  SourceBuffer's codecs string, and finally a late-joining viewer's `currentTime` never landing inside
  the buffered range. Each is documented individually below.
- Live view connected, transferred real data continuously (confirmed: tens of MB per session in IIS
  logs, `appendBuffer` never throwing), and still showed no video with no error —
  `video.readyState` stuck at `HAVE_METADATA` forever. Cause: the live leg's fMP4 timestamps track
  elapsed time since ffmpeg started, not since the viewer connected, so a late-joining viewer's first
  buffered range starts well past the fresh `<video>` element's default `currentTime = 0`. MSE won't
  advance past `HAVE_METADATA` until `currentTime` falls inside a buffered range, so playback never
  starts — silently, since nothing about this is an error condition from MSE's point of view. Fixed
  by seeking `videoEl.currentTime` to the start of the newest buffered range the first time any range
  becomes available, standard practice for live-streaming MSE players joining mid-stream.
- The mute-button fix below introduced a regression of its own: wrapping the video/status/button trio
  in a `position-relative` div broke the `.ratio` box entirely (tiles rendered square, no video, no
  errors, and the wrapper's `Watch` button became unreliable to click). Cause: Bootstrap utility
  classes carry `!important`, so `position-relative` on a direct `.ratio` child fights the framework's
  own `.ratio > * { position: absolute; ... }` rule instead of complementing it. The wrapper needs no
  positioning classes at all — it already becomes `position: absolute` for free as a direct `.ratio`
  child, which is a perfectly valid containing block for its own absolutely-positioned children.
- Live view's mute/unmute button covered the entire video tile instead of sitting as a small corner
  overlay. Cause: Bootstrap's `.ratio > *` rule forces every *direct child* of a `.ratio` container to
  `position: absolute; width: 100%; height: 100%` (needed for the `<video>` itself, but the button was
  also a direct child of the same `.ratio` div). Fixed by wrapping the video/status/button trio in an
  inner div so only that wrapper is a direct `.ratio` child; the button is now a small translucent icon
  in the bottom-right corner as intended.
- `live-view.js` only declared the video codec in the `SourceBuffer`'s "codecs" parameter, never the
  audio codec — even for the cameras that all have an audio track (`CameraStream.HasAudio = true`).
  The fMP4 init segment the node sends declares both tracks (recording is `-c copy`, so audio is
  always present in the container when the camera has it), and MSE treats a `SourceBuffer` whose
  declared codecs don't match the init segment's actual tracks as a hard failure — the same "removed
  from parent media source" symptom as the codec-*family* mismatch fixed above, but from audio being
  entirely undeclared rather than the video codec being wrong. Confirmed as a second, independent
  cause of the same error: switching a camera to H.264 (avoiding the HEVC decode-support question
  entirely) still failed identically until this fix. `mp4a.40.2` (AAC-LC) is now appended to every
  candidate's codecs string whenever the stream has audio.
- `live-view.js` always tried H.264 codec strings first when opening the browser's `SourceBuffer`,
  regardless of what the camera's stream actually is. `MediaSource.isTypeSupported` only checks
  whether the browser *could* decode a codec family in the abstract, so it reports H.264 as supported
  on essentially every browser even when the camera is HEVC (true for all six cameras in this
  install — `CameraStream.Codec = "hevc"`, since recording is `-c copy` and every one of them
  natively encodes HEVC). The mismatch meant a SourceBuffer typed for H.264 was fed real HEVC bytes,
  which fails to decode, and the browser tears down the errored `MediaSource` — surfacing as no video
  and the "removed from parent media source" error above rather than a clear codec message. The
  player now reads the camera's actual codec (already reported by the node, `CameraStream.Codec`) and
  only tries mime candidates from the matching family. Browsers without HEVC decode support (Chrome
  and Firefox on most platforms, without a paid Windows codec pack) will now get a clear "no supported
  codec" message instead — actually decoding HEVC there needs the M5 "codec-support detection with
  hardware-transcode fallback" work, which is still not built (see Known limitations below).
- Every `/live/{cameraId}` request returned HTTP 400 ("Connection error" in the browser) regardless
  of node/camera state, because IIS's WebSocket Protocol feature (`Web-WebSockets`) wasn't installed
  on the server — IIS stripped the WebSocket upgrade headers before they reached the app, so
  `ctx.WebSockets.IsWebSocketRequest` was always false. Live view was never actually verified against
  a real browser before now (see 0.6.0's "Known limitations" below); this is what was blocking it.
  No code change — installing the Windows feature is the fix — but noted here since it's the kind of
  thing a fresh install of this app needs called out explicitly.
- `live-view.js` treated an `appendBuffer` failure as non-fatal (show the error, keep going), so once
  a session's `SourceBuffer` was detached from its `MediaSource` — which happens whenever the video
  element's `src` is cleared while stopping/restarting a stream — every subsequent WebSocket fragment
  re-threw the same "removed from parent media source" error forever instead of ending the session.
  An append failure now closes the socket and marks the session closed, same as calling `stop()`.
  Also moved the video element's `src` teardown into `stop()` itself, after the socket is closed and
  the session is marked closed, instead of the caller doing it separately right after — the ordering
  is what caused the detached-SourceBuffer race in the first place when stopping and rewatching a
  camera in quick succession.
- Live view returned 503/"Connection error" for every camera on both real nodes after the M5
  upgrade — `Node.MediaSigningKey` was still null for any node that registered before M5 shipped,
  since it was only ever generated at registration, and the CHANGELOG's stated fix ("re-register the
  node") turns out to be more disruptive than described: re-registering mints a brand new `NodeId`,
  orphaning the old one along with its camera assignments and storage override. Fixed properly
  instead — the key is now handed out through `NodeConfigResponse`, the same config a node already
  polls every 30s, with a lazy server-side backfill if a node's stored key is still null. An existing
  node now self-heals within one reconcile cycle; no re-registration, no restart.
- `install-node.ps1` never opened a firewall rule for the M5 live-view port — confirmed on a real
  node (`NVR1`): `LarisVMS.Web`'s proxy could open a TCP connection to the port, but every WebSocket
  request just hung until timeout rather than failing fast, because nothing was actually listening
  from the *firewall's* perspective. Now creates an inbound allow rule for `-LivePort` (default 8554)
  idempotently. Also added the missing `-LivePort` parameter itself — the node's own `--live-port`
  flag existed but the installer never had a way to pass a non-default value through to it.

## [0.6.0] - 2026-08-08

### Added

- **M5 (pass 1) — Live view.** Watch a camera's live feed in the browser from `Pages/Live`.
  - Recording now tees the one RTSP session ffmpeg already holds into two muxers — the existing
    segment recorder, unchanged, plus a continuous fragmented-MP4 stream to the process's own
    stdout — instead of opening a second session against the camera just to serve live view.
    Verified against a real Amcrest camera (not a synthetic source — see Fixed below for why that
    distinction mattered): both legs produced valid, ffprobe-readable HEVC+AAC content.
  - `RecordingSession` scans its own stdout for the fMP4 init segment boundary (`Mp4BoxScanner`,
    unit tested against synthetic box data), buffers it, and raises `LiveFragmentReceived` for
    everything after — a late-joining viewer gets the init segment once, then every fragment live.
  - Nodes now host a small Kestrel endpoint (`/live/{cameraId}`, WebSocket) — plain HTTP, LAN-only,
    reachable only by LarisVMS.Web's proxy, never by a browser directly. This is a deliberate
    architecture decision, not a shortcut: direct browser-to-node `wss://` would mean every node
    needs its own TLS certificate (self-signed and asking each viewer to trust it, or a real one via
    an internal CA) before video plays at all; proxying through IIS needs nothing installed on a
    node. Resolves the "Node TLS strategy" item the plan had left open since M1.
  - A 60-second HMAC token (`MediaToken`, unit tested), signed with a per-node key generated at
    registration (`Node.MediaSigningKey`, encrypted at rest), is what keeps a node's live port from
    being wide open to anything else on the LAN that knows the URL shape — validated locally by the
    node, no DB round trip.
  - `LarisVMS.Web` gained `GET /live/{cameraId}` (`Cameras.View`-gated): accepts the browser's
    WebSocket, opens its own outbound one to the camera's node (address from `Node.LastIpAddress` +
    the newly self-reported `Node.LivePort`), and relays frames between them.
  - Browser side is a vanilla-JS MSE player (`live-view.js`) — click a camera tile on `Pages/Live` to
    connect, no framework, matching the rest of the app's no-build-step JS approach.

### Changed

- `NodeService`'s heartbeat-persisting method (previously `UpdateStorageStatsAsync`) is now
  `RecordHeartbeatAsync` and also persists the node's self-reported `LivePort` — it was already the
  one place `Version` got updated after the earlier version-reporting fix, so folding `LivePort` in
  alongside it kept everything the heartbeat actually reports in one place rather than splitting it
  across two similarly-named methods.

### Fixed

- The node's self-reported version included the build's git commit hash
  (`0.5.0+987c0f72423e2d662c7e8a5474866fc2b4bc9485`) instead of just `0.5.0` — the .NET SDK appends
  `+<git-sha>` to `InformationalVersion` by default inside a git repo (source revision embedding).
  `NodeVersion` now strips everything from the `+` onward.
- Two real bugs in the new tee'd ffmpeg command, both caught by testing locally before either could
  reach a real recording node:
  - ffmpeg's tee muxer treats `\` as its own generic escape character inside its bracket-option
    syntax, so escaping a Windows drive-letter colon as `C:\...` -> `C\:\...` (the naive fix) instead
    corrupted the *entire* path — every other backslash in it got silently eaten too
    (`C:\Users\...` became `C:Users...`). Fixed by normalizing to forward slashes first, then
    escaping only the remaining colon — ffmpeg accepts forward slashes in Windows paths natively, so
    no other backslashes remain to cause the same problem.
  - The live leg's `movflags` used `default_base_is_moof`, the flag name used in ffmpeg's own CLI
    documentation and, previously, this project's architecture plan — this build's mov muxer only
    accepts `default_base_moof` and rejects the other name outright, which aborted the *entire* tee
    (both legs, including recording) the moment ffmpeg tried to write the first header. Confirmed via
    `-h muxer=mov`. Both fixes verified end-to-end against a real Amcrest camera stream before this
    code path was ever built into a node package.

### Known limitations (M5 pass 1 — tracked for a follow-up pass, not silently dropped)

- Codec-support detection and hardware-transcode fallback are not implemented. In practice this has
  turned out to matter less than expected — HEVC plays natively in the browsers tested against this
  install (see [Unreleased]'s "Live view is now confirmed working end-to-end" entry) — but a browser
  genuinely unable to decode a camera's native codec still just won't play it; no fallback exists yet.
- Main/sub auto-switch by tile size, snapshot/still capture, and instant replay (pre-record ring
  buffer) are not implemented — plan items for a later M5 pass.
- `Node.MediaSigningKey` is only ever generated at registration — a node that registered before this
  release has none, and live view returns 503 for its cameras until that node re-registers (delete
  `node.config`, re-run `install-node.ps1` with the registration key). Upgrading in place (the normal
  path) does *not* trigger this — only a fresh registration does.
- ~~Live view was verified end-to-end against one real camera... but actual browser playback has not
  been visually confirmed.~~ Resolved — see [Unreleased].

## [0.5.0] - 2026-08-08

### Added

- `Admin → Nodes` shows each node's IP address, captured server-side from the connection on every
  authenticated register/heartbeat/config/segments request (`Node.LastIpAddress`) rather than
  self-reported by the node — useful for spotting a node on the wrong subnet/VLAN or one whose IP
  changed unexpectedly.
- Real stream resolution/codec, read from ffmpeg's own stderr the moment it opens a camera's Main
  stream and reported back to the web (`POST /api/nodes/streams/info`). ONVIF's advertised
  VideoEncoderConfiguration is unreliable — confirmed: Amcrest omits it entirely for H.265 profiles
  — so `CameraStream.Width/Height/Codec` used to stay blank for those cameras even though the RTSP
  URI itself was correct and recording worked fine. Only ever reported for Main today, since that's
  the only stream a node's ffmpeg process actually opens (Sub/Third aren't consumed until M5's live
  view starts a process for them too).
- Per-stream enable/disable and a display-name override, on `Cameras/Edit`'s Streams table.
  Disabling a stream removes it from what `NodeService.GetConfigAsync` hands to nodes, so disabling
  Main stops that camera recording without touching the camera itself — same mechanism, one level
  more granular than `Camera.IsEnabled`. Both survive a re-probe: `CameraService.ReplaceStreamsAsync`
  now matches existing rows to freshly-probed profiles (by ProfileToken, falling back to Role) and
  carries IsEnabled/CustomName forward instead of deleting and recreating the whole set, which would
  otherwise have silently wiped any customization on the next "Re-probe" click.

### Changed

- `install-node.ps1`'s upgrade path (re-running it against an already-installed node) no longer
  deletes and recreates the Windows Service — it stops the existing one and updates its binary
  path/display name/credential in place via WMI (`Win32_Service.Change`), leaving Event Viewer
  history and anything else referencing the service intact. It also no longer re-resolves ffmpeg
  from PATH/winget on every upgrade: if ffmpeg is already sitting in this node's own install dir from
  a previous run, it's reused as-is, and the winget fetch/copy step is skipped entirely unless
  `-FfmpegPath` is passed explicitly to replace it.

### Fixed

- `install-node.ps1` stopping the Windows Service only waits for `LarisVMS.Node.exe` itself to exit —
  Windows doesn't kill child processes when their parent dies, so if `LarisVMS.Node`'s own graceful
  shutdown doesn't finish killing each `ffmpeg.exe` it spawned before the SCM's stop timeout hits,
  those are left running, orphaned, and still holding their DLLs open. Confirmed on a real node: this
  made re-running the script to upgrade an already-running node fail with "the process cannot access
  the file... being used by another process" while copying ffmpeg. The script now unconditionally
  stops any `ffmpeg.exe` still running after the service reports Stopped, plus retries the copy
  itself a few times as a second line of defense against the same handle-release race. (An earlier
  version of this fix tried to filter to only ffmpeg processes under the node's own install
  directory by checking each process's `.Path` — that filter silently matched nothing, since
  querying `.Path` on a process running as a different account, LocalSystem by default here, can
  fail even from an elevated session. Simpler and correct: ffmpeg is only ever run by LarisVMS.Node on
  this machine, so there's nothing to filter for.)
- Nodes kept reporting a stale version in `Admin → Nodes` no matter how many times they were
  upgraded. Two compounding bugs: `NodeHeartbeatRequest.Version` was sent on every heartbeat but the
  heartbeat endpoint never read it, so `Node.Version` only ever got set once, at first-ever
  registration, and never again — confirmed live: two real nodes stuck showing `0.3.0` despite
  running an already-upgraded `0.4.0` binary. Separately, the node's own reported version was a hand
  maintained string literal (`"0.4.0"`) rather than read from the build, which is exactly what let it
  drift two releases behind in the first place. Fixed both: the heartbeat handler now persists
  `Version` alongside the disk-usage stats it already recorded, and the node reads its version from
  its own assembly metadata (`LarisVMS.Node.csproj`'s `<Version>`) via a new `NodeVersion.Current`
  instead of a literal anyone could forget to bump. Confirmed live: both real nodes corrected to
  `0.4.0` on their next heartbeat, with no redeploy needed for the server-side half of the fix.
- The `AddCameraStreamEnabledAndCustomName` migration (added for the enable/disable/rename feature
  above) would have defaulted every *existing* stream's new `IsEnabled` column to `false` — EF's
  migration scaffolding defaults a new non-nullable bool column to the CLR default, not the C#
  property initializer's `true`. Combined with `NodeService.GetConfigAsync`'s new
  `Where(s => s.IsEnabled)` filter, applying this as generated would have silently stopped recording
  on every camera already in production the moment it deployed. Caught before deploying; the
  migration explicitly backfills `defaultValue: true` instead. Confirmed live post-deploy: all 18
  existing `CameraStreams` rows came through with `IsEnabled = true`.

## [0.4.0] - 2026-08-08

### Added

- **M4 — Storage manager.** Every node now runs a `StorageManager` background service that
  enforces, in order: age-based retention (per camera, resolved through `ISettingsResolver`),
  per-camera storage quota (`Camera.QuotaBytes`, oldest segments evicted first once over the cap),
  and a global watermark backstop (delete the oldest segments across every camera on the node,
  regardless of retention/quota settings, once the storage volume passes a configurable % used —
  the hard "the disk is nearly full" fallback the plan calls out as independent of retention days).
  It's purely filesystem-driven — `LarisVMS.Node` has no DB connection by design — and reports back
  which files it deleted via a new `POST /api/nodes/segments/delete` so the web deletes the matching
  `Segment` rows; the never-touch-a-file-younger-than-5-minutes guard keeps it from ever racing
  `RecordingSession`'s in-progress segment. Empty date/hour folders left behind by eviction are
  pruned in the same pass.
- `ISettingsResolver` gained a `Node` scope: retention now resolves **Camera → Node → Global →
  30-day compiled-in default**, the specific case the plan called out `SettingScope.Node` for
  ("global retention, overridable per node, overridable again per camera on top of that"). New
  `GetSourceAsync` (which scope actually supplied the value) and `GetOwnOverrideAsync` (a scope's
  own override with no walk, for populating edit forms) support the "inherited vs. overridden" UI
  without duplicating the resolution logic. `SetOverrideAsync` sets or clears a Camera/Node-scoped
  override; a blank value removes it. CameraGroup-scoped overrides remain schema-ready but
  unimplemented — no feature has needed the ancestor walk yet.
- Nodes self-report free/total storage bytes on every heartbeat (`GetDiskFreeSpaceEx`, which
  — unlike `System.IO.DriveInfo` — resolves UNC paths, so this works identically for a local
  storage root and a `\\server\share` one). `Admin → Nodes` shows a usage bar per node plus a rough
  "days of retention remaining" estimate (free bytes ÷ that node's write rate over the last 24h —
  a projection from recent activity, not SMART/disk health).
- New `Admin → Retention` page for the global retention-days and watermark-% defaults. `Admin →
  Nodes` gained a per-node retention override column; `Cameras/Edit` gained a per-camera retention
  override and a storage quota (GB) field. `Cameras/Index` and `Admin/Nodes` both show the
  *effective* resolved value, never a separate "has an override" query, so the display can't drift
  from what the storage manager actually enforces.
- `Cameras/Index` shows each camera's storage usage (sum of `Segments.SizeBytes`) against its quota.
- `StorageManager`'s eviction-decision helpers (age cutoff, oldest-first quota selection, bottom-up
  empty-directory pruning) are unit tested against a real temp directory tree, not just built and
  trusted — `LarisVMS.Node` now has `InternalsVisibleTo` for `LarisVMS.Tests` for exactly this.

### Fixed

- The node's self-reported version (sent on registration and every heartbeat) was hardcoded to
  `"0.3.0"` since M3 and had drifted two releases behind the actual build. Now `0.4.0`, matching
  `<Version>`; still a literal rather than read from the assembly, so this will drift again next
  milestone unless whoever bumps the version remembers to grep for it — worth automating properly
  before it causes real confusion in `Admin → Nodes`' Version column.

### Changed

- Recording segments now write into nested `<camera>/main/yyyy/MM/dd/HH/` folders instead of one
  flat directory. At the default 60s segment length a single camera writes ~1,440 files/day, and a
  flat directory holding a realistic 30-90 day retention window (tens to hundreds of thousands of
  files) makes every directory listing measurably slower — including `RecordingSession`'s own poll
  every few seconds. ffmpeg's segment muxer has no option to create missing directories itself (this
  build has no `-strftime_mkdir`, only `-strftime`), so a naive nested pattern would abort the whole
  recording at the first hour boundary; `RecordingSession` now pre-creates the current *and next*
  hour's folder before starting ffmpeg and on every poll tick, so the next folder always exists well
  before ffmpeg needs it regardless of how long the current attempt has been running. Verified live:
  clean recording across an hour-folder pre-creation (confirmed the next hour's empty folder exists
  ahead of time) with segments landing correctly. Folder names are the *node's local* time, not UTC,
  deliberately — they're for a human browsing the filesystem, and `Segment.StartUtc`/`EndUtc` (what
  actually matters for correctness) are computed independently from file metadata, not parsed from
  the path.

## [0.3.1] - 2026-08-08

### Added

- Node management: `Admin → Nodes` now supports editing a node's name and per-node storage root
  override (blank falls back to the global `Storage.RootPath` setting — useful when one node records
  to its own local disk instead of the shared target) and deleting a node outright. Deleting a node
  unassigns its cameras (`Camera.NodeId` set null via `DeleteBehavior.SetNull`) rather than deleting
  them, and leaves already-written `Segment` rows untouched — they're historical recordings, not live
  node state. `Cameras → Index` now shows which node each camera records through, linking to
  `Admin → Nodes`, so an unassigned (non-recording) camera is visible at a glance.

- `build-node.ps1` / `install-node.ps1`, replacing the M3 stub. `build-node.ps1` publishes
  `LarisVMS.Node` as a self-contained single-file win-x64 executable (Windows only — the node's
  registration store is DPAPI-based and throws on Linux; that support isn't implemented yet, so
  publishing a linux-x64 build would just fail at first run) and bundles `install-node.ps1`
  alongside it, mirroring `dploid`'s `build-agent.ps1`/`install-agent.ps1` shape. `install-node.ps1`
  installs to `C:\Program Files\LarisVMS\Node`, registers a Windows Service with the registration
  arguments baked into its command line (only actually used on first start — after that,
  `node.config` exists and registration is skipped, so re-running the installer to change settings
  is safe), configures restart-on-failure recovery, and can fetch the LGPL "shared" FFmpeg build via
  `-InstallFfmpeg`. Takes `-ServiceCredential` for nodes whose storage root is a network share, since
  the default LocalSystem account can't authenticate to SMB. Verified end-to-end: `build-node.ps1`
  produces a working 50MB self-contained exe that runs standalone.

### Fixed

- `-InstallFfmpeg`'s winget bootstrap went through three failure modes in a row on real recorder
  machines while going through a PowerShell module layer (`Microsoft.WinGet.Client`): depending on
  `Repair-WinGetPackageManager`, which doesn't exist on every version of that module;
  `Install-Module -Force` refusing to run at all when some of the module's cmdlets were already
  present without that one ("commands are already available... use `-AllowClobber`"); and, on one
  machine, a third-party `Cobalt` module shadowing the same cmdlet names (`Install-WinGetPackage`
  etc.) with its own broken `winget.exe` lookup. The module layer is dropped entirely — `winget.exe`
  is now located directly by its own well-known install path (`PATH`, then `%ProgramFiles%\WindowsApps
  \Microsoft.DesktopAppInstaller_*\`, matching `dploid`'s `deploy.ps1` probe) and invoked directly, no
  cmdlet resolution involved.
- Whichever way ffmpeg was resolved (found on `PATH`, `-FfmpegPath`, or freshly installed by
  `-InstallFfmpeg`), the resolved location was baked into the service's command line as-is. A winget
  install lands under the *installing user's* `%LOCALAPPDATA%`, which the Windows Service — running
  as LocalSystem or a dedicated service account, never as whoever happened to run the installer —
  has no access to; the service would have started but failed to launch ffmpeg at all. The resolved
  ffmpeg (its `.exe` and the DLLs a shared build depends on) is now always copied into the node's own
  `C:\Program Files\LarisVMS\Node\ffmpeg\` before the service is registered, regardless of where it was
  originally found, so the service account's access to it no longer depends on where installation
  happened to leave it.

## [0.3.0] - 2026-08-08

### Added

- 24/7 recording engine (`LarisVMS.Node`, `LarisVMS.Media`). A recorder node is a separate Windows
  Service process — deliberately not part of the IIS-hosted web app, since app pool recycles would
  otherwise interrupt recording. `RecordingSession` supervises one `ffmpeg -c copy` process per
  camera's Main stream, writing 60-second fMP4 segments with a state machine
  (`Idle → Connecting → Recording → Backoff`) and a stalled-stream watchdog: if no new segment lands
  within 150s the process is killed and restarted with exponential backoff, since an ffmpeg process
  that's alive but has stopped producing output is the common real-world failure mode, not a crash.
  **Verified against a real Amcrest camera on a live SMB share**: sustained multi-segment continuous
  recording, killing the ffmpeg process mid-recording (auto-restarted within ~4s and resumed
  cleanly), and killing/restarting the whole node process (resumed recording immediately from its
  persisted registration).
- Node control plane (`LarisVMS.Web` `/api/nodes/*`). Register/heartbeat/config/segment-report
  endpoints and `NodeAuthMiddleware` mirror dploid's proven `AgentAuthMiddleware` shape — bearer
  `"{nodeId}:{secret}"`, SHA-256 hashed and compared with `CryptographicOperations.FixedTimeEquals`.
  Secret rotation (`PreviousApiKeyHash`) and dploid's nonce/replay hardening are deliberately not
  wired up yet — a single long-lived secret per node is enough to get recording working and doesn't
  block adding rotation later without a wire-protocol change.
- `Node` and `Segment` entities. `Segment` is clustered on `(CameraId, StartUtc)` rather than `Id`
  (every real query is "this camera, this time range") ahead of the M4 date-partitioning work the
  plan calls for on this table, and carries a unique index on `FilePath` as a backstop against
  duplicate reports — see Fixed below for why that backstop turned out to matter in practice.
- Admin → Nodes page and a camera-to-node assignment dropdown on the camera edit page.
- Development-mode error visibility: `web.config` now sets `ASPNETCORE_ENVIRONMENT=Development` and
  `Program.cs` explicitly wires up `app.UseDeveloperExceptionPage()`, so unhandled exceptions show a
  full stack trace in the browser instead of the generic error page during this pre-release,
  milestone-by-milestone development phase. Must be switched back to `Production` before any
  milestone that records real footage or is reachable outside a trusted dev network.

### Fixed

- An ONVIF camera's RTSP session commonly carries a third "Data: none" track alongside video/audio
  (the metadata stream defined by the profile's `MetadataConfiguration`, not a real recordable
  stream). Recording used `-map 0` (all streams), and the MP4 muxer has no tag for an unknown-codec
  data stream — confirmed against a real Amcrest camera, this made `Could not write header` abort
  the *entire* segment, losing perfectly good video and audio along with it. Now maps `0:v` and the
  optional `0:a?` explicitly.
- A single camera whose recording briefly failed to start (bad credentials, network hiccup) left
  behind a 0-byte segment file — ffmpeg opens/truncates the output file as part of starting a header
  write, before it can fail that write. These were being reported as real segments (visible as
  `SizeBytes = 0` rows once this was caught against the live database), which would have corrupted
  the timeline. The poller now skips zero-byte files.
- Every ffmpeg restart re-created `RecordingSession`'s de-duplication set from scratch, so each
  reconnect after a crash rescanned the whole output directory with no memory of what it had already
  reported and re-reported all of it — reproduced live as 5-10x duplicate rows for the same handful
  of files during a crash loop. The set is now scoped to the whole recording session, not each
  connection attempt. A full node *process* restart (not just an ffmpeg reconnect) still has a
  smaller version of this gap, since the de-duplication set is in-memory — confirmed live after
  restarting the node process, which re-reported 7 already-known segments in one batch. `Segment`
  now has a unique index on `FilePath`, and `NodeService.RecordSegmentsAsync` retries a failed batch
  save with just the colliding entries detached, so a duplicate report never costs the rest of the
  batch or crashes the request. Verified against the live database: 9 total rows, 9 distinct
  `FilePath` values, zero duplicates, despite the restart re-scan.

- A single camera with an undecryptable stored credential (e.g. encrypted under a Data Protection
  key ring that no longer exists) crashed the entire Cameras list, because `CameraService.ListAsync`/
  `GetAsync` decrypted `Username`/`Password` for every row as a side effect of loading the entity,
  even though neither page displays them. Both now project every column except the two credential
  columns, so decryption never runs on a read path that doesn't need it. `DeleteAsync` was changed
  to issue the delete directly (`ExecuteDeleteAsync`) rather than loading the entity first, so a
  broken row can still always be removed, and `ProbeAsync` — the one path that does need the
  decrypted credentials — now catches a decrypt failure and reports it as a normal probe error
  instead of throwing out of the request.
- ONVIF calls to a camera's HTTPS device service failed outright, surfaced through the camera's own
  firmware as a misleading "client may not support HTTPS mode" fault, because the `HttpClient` used
  for ONVIF calls performed standard certificate chain validation — which every LAN ONVIF camera's
  self-signed certificate fails unconditionally, not just misconfigured ones. Certificate validation
  is now disabled specifically for the named `"onvif"` HttpClient (never used for anything else).
- `Camera.OnvifPort` was hardcoded to `80` whenever the entered device service URI had no explicit
  port, even for an `https://` URI (whose real default is `443`) — `Uri.Port` already resolves the
  correct scheme default and is used directly now.

## [0.2.0] - 2026-08-08

### Added

- Hand-rolled ONVIF SOAP client (`LarisVMS.Onvif`). The plan originally called for
  `dotnet-svcutil`-generated clients from vendored WSDLs, but ONVIF's WSDL/XSD tree is notorious for
  breaking that generator (circular schema imports), so instead this is plain XML request/response
  templates over `HttpClient` for the ~10 operations LarisVMS actually needs today
  (`GetDeviceInformation`, `GetCapabilities`, `GetServices`, `GetProfiles`, `GetStreamUri`,
  `GetSnapshotUri`). Far less generated code, easy to extend per-operation.
- WS-Security UsernameToken digest auth. `OnvifSoapEnvelope` builds the
  `PasswordDigest = Base64(SHA1(Nonce + Created + Password))` header per call, with a fresh nonce
  every time so a captured request can't be replayed.
- WS-Discovery LAN auto-scan. `WsDiscoveryClient` sends a multicast Probe to
  `239.255.255.250:3702` across every active, multicast-capable network interface and collects
  ProbeMatch responses. Only reaches cameras on the same broadcast domain — cameras on another VLAN
  need manual entry via their device service URL, which the Discover page links to directly.
- ONVIF Profile S/T/G/M capability probe. `CameraCapabilityProber` infers the profile matrix from
  which services a device advertises (Media→S, media2→T, recording/search/replay→G,
  analytics+events→M), plus PTZ/imaging/events/analytics/audio-out/relay/digital-input flags. This
  is best-effort inference from advertised services, not certified ONVIF conformance — documented as
  such on `CameraCapabilities` and in the UI.
- Main/Sub/Third stream ranking (`CameraProfileRanker`). Ranks a camera's ONVIF media profiles by
  name first, resolution second. Verified against a real Amcrest IP5M-B1276EW-AI: its H.265
  Main/Sub1 profiles return no `VideoEncoderConfiguration` detail at all — not from `GetProfiles`,
  not from a direct `GetVideoEncoderConfiguration(token)` call either, a genuine firmware gap —
  while its H.264 sub-stream reports full detail. A pure resolution-based ranking would have picked
  the lower-quality H.264 stream as "Main"; the name heuristic gets it right and is covered by a
  unit test reproducing this exact profile shape.
- Camera entities: `Camera`, `CameraCapabilities` (1:1), `CameraStream`, `CameraGroup`
  (self-referencing tree with a `MaterializedPath`). `Camera.Username`/`Password` are encrypted at
  rest via the `SecretProtection` converters added in 0.1.0 — confirmed round-tripping correctly
  against a live database (raw column read back as `enc:v1:...` ciphertext; `CameraService` decrypts
  transparently on the way back out).
- Camera management pages: `Cameras/Discover` (WS-Discovery scan → one-click add), `Cameras/Edit`
  (manual add/edit, credentials, re-probe button, capability + stream display), `Cameras/Index`
  (list with Profile S/T/G/M and PTZ/Talk/Meta/I-O badges), `Cameras/Groups` (site/building/floor
  tree). Gated behind `[Authorize("Cameras.View"/"Cameras.Edit")]`, resolved automatically by the
  `PermissionPolicyProvider` from 0.1.0 with no new policy registration needed.
- `CameraService`: camera CRUD plus the probe orchestration that turns a `GetCapabilities`/
  `GetServices`/`GetProfiles` round trip into persisted `CameraCapabilities` and `CameraStream` rows.
  A failed re-probe (camera offline, wrong credentials) keeps the last-known-good capabilities/
  streams rather than wiping them out.

### Fixed

- SOAP response parsing matched only the SOAP 1.1 envelope namespace. The request is sent as SOAP
  1.1, but real camera firmware (confirmed: Amcrest) replies with a SOAP 1.2 envelope
  (`http://www.w3.org/2003/05/soap-envelope`) regardless of the request's version, so every call
  failed with "Response had no SOAP Body." `OnvifSoapClient` now matches `Body`/`Fault` by local
  element name instead of a fixed namespace, independent of which SOAP version the device echoes
  back.

## [0.1.0] - 2026-08-08

### Added

- Solution skeleton: `LarisVMS.slnx` with the seven-project layering from the plan (`Core`, `Onvif`,
  `Media`, `Infrastructure`, `Web`, `Node`, `NodeUpdater`) plus `tests/LarisVMS.Tests`, all targeting
  `net10.0` with `Directory.Packages.props` for central package management from day one. `LarisVMS.Onvif`
  and `LarisVMS.Media` were placeholder projects until this release; `LarisVMS.Node` and
  `LarisVMS.NodeUpdater` still print a not-yet-implemented message pending milestone M3.
- ASP.NET Core Identity + Resource×Action RBAC: `ApplicationUser`, cookie auth with login/logout
  audit events, and `PermissionPolicyProvider` (lifted from rsolva) resolving any
  `"{Resource}.{Action}"` authorization policy on the fly so pages can write
  `[Authorize("Cameras.Edit")]` with zero per-combination `AddPolicy` registration. `Administrator`
  and `Viewer` roles are seeded by the setup wizard.
- Per-camera access control model: `CameraAccess` (Role/User principal × All/Group/Camera scope ×
  View/Playback/Export/Ptz/Talk/Configure action flags), defined ahead of `Camera`/`CameraGroup` so
  the permission model was complete before M2.
- Settings with per-camera override: `Setting` (global) + `SettingOverride`
  (Global/CameraGroup/Camera scoped) and `ISettingsResolver`, the single mechanism every
  "global with per-camera override" feature (retention, recording mode, motion sensitivity, ...)
  reads through, rather than a bespoke nullable column per feature.
- Encryption at rest: `SecretProtection` (rsolva's static-protector, `enc:v1:`-marker,
  design-time-passthrough shape) with a fail-loud `Unprotect` on decryption failure. Key ring lives
  under `%ProgramData%\LarisVMS\keys`, DPAPI-wrapped on Windows.
- UTC timestamp handling: `ApplicationDbContext.ConfigureConventions` stamps every
  `DateTime`/`DateTime?` column as UTC on read, copied from rsolva.
- `AppVersions` table and footer: the version shown in the page footer is read from the highest
  `AppVersions` row, intended to be populated by a data-only migration on every release so a
  half-applied deploy is visible on every page rather than only in the assembly version.
- Setup wizard: `Index → Database → Admin → Storage → Node → Branding → Review`, gated by a cached
  `SetupMiddleware` (frcastr's `static volatile bool` pattern). `SetupDatabaseAsync` creates the
  database with `COLLATE Latin1_General_100_CI_AS_SC_UTF8`, runs `MigrateAsync`, and grants the IIS
  app pool identity `db_owner`. The Storage and Node steps write placeholder `Settings` rows
  (`Storage.RootPath`, `Node.RegistrationKey`) ahead of the real `StorageTarget`/`Nodes` entities
  landing in M4/M3.
- Security headers and `/health`: `SecurityHeadersMiddleware` (rsolva's CSP, extended with
  `media-src blob:` for the MSE playback path M5 will add) and an anonymous `/health` endpoint
  backed by `DbHealthCheck`, which reports healthy rather than failing when the database isn't
  configured yet (pre-setup).
- `deploy.ps1`: frcastr's IIS deploy script (admin check, site/URL resolution, `dotnet tool
  restore`, connection-string read from the deployed `setup-generated.json`, publish, stop pool,
  `try { migrate + robocopy /MIR } finally { start pool }`), with three LarisVMS-specific additions: a
  storage-root guard that throws before deploying if the configured storage root resolves under the
  IIS site directory (a `/MIR` there would delete every recording), `/XD` exclusions for
  `recordings`, `spool`, and `exports` as a second line of defense, and a post-deploy `/health`
  probe. `install-node.ps1` is a stub until M3.

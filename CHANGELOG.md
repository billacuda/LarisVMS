# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.161.6] - 2026-08-30

### Fixed

- **The last 2 of 24 Snapshots cards that still 502'd after 0.161.5** (confirmed live to fail in
  under a second — not gate contention, a genuine per-span capture failure). `Segment.DurationMs` is
  derived from wall-clock `EndUtc - StartUtc` (`NodeService.RecordSegmentsAsync`), not a
  re-measurement of the file's actual encoded duration, and `GetSnapshotImageInfoAsync`'s offset is
  clamped against that same possibly-inflated value — so a best-frame instant near a short/truncated
  segment's believed end can still land past the file's real content. This exact class of drift was
  already identified and fixed once, for `/playback-thumbnail`'s sibling lookup, with a retry at
  offset 0 on a `502` — `/snapshot-image`'s proxy never got the same fix. `ProxySnapshotImageAsync`
  now retries once at offset 0 on the same segment/crop box, trading exact-instant precision (which
  frame within the recording) for a guaranteed hit, same as the thumbnail path already does.
- **Diagnostic follow-up**: `SnapshotImageCapture.CaptureAsync` now logs ffmpeg's own stderr output
  (not just "produced no bytes") when a capture still fails after the retry, so any future case is
  diagnosable directly from the node's log instead of by elimination.

### Notes

- Web + Node change — rebuild/redeploy the web app and re-run `install-node.ps1` (or let auto-update
  pick up the node build) for the new logging.

## [0.161.5] - 2026-08-30

### Fixed

- **Found via 0.161.4's new logging**: the "No thumbnail available" cards from that release were
  concurrency, not data — a handful of AI-detection Snapshots cards timed out with a `502` on every
  load, and it looked exactly like a sticky, deterministic bug because the *same* cards kept losing
  the same race on every reload. The Snapshots page requests up to a full page (24) of AI-detection
  crops in one burst; `LarisVMS.Node`'s `/snapshot-image` capture was gated to **2 concurrent ffmpeg
  captures with a 3-second give-up**, inherited unchanged from the hover-scrub-preview gate it was
  modeled on. A single capture measured live at 3.5-4.4 seconds, so at most the first couple of a
  24-card burst could ever get a slot before the 3s window closed — every other card 502'd, logged
  (as of 0.161.4) as "ffmpeg produced no cropped frame," which is misleading: ffmpeg was never even
  invoked for those, they just never got a turn. The hover-scrub case this gate was designed for has
  a fundamentally different shape — one request at a time, and a request that can't get served
  quickly really is stale — which doesn't apply to a static page where every one of 24 requested
  cards still wants its image. `OnDemandSnapshotGate` (used only by Snapshots-page AI-detection
  crops, nothing else) is now sized 6 concurrent with a 30s timeout instead of 2/3s. The sibling
  `OnDemandThumbnailGate` (shared with live hover-scrub, where the original 2/3s reasoning still
  applies) is unchanged. Both gates' give-up paths now log which one timed out and after how long, so
  this class of failure is never silently indistinguishable from a real ffmpeg failure again.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up).

## [0.161.4] - 2026-08-30

### Added

- **Diagnostic logging for a separate, still-open issue**: some Snapshots cards show "No thumbnail
  available" even though their footage plays fine via Playback, and reloading doesn't fix it — a
  sticky, per-card failure distinct from the footage-coverage guard fixed in 0.161.0-0.161.3.
  `LarisVMS.Node`'s `/playback-thumbnail` and `/snapshot-image` routes previously returned a bare
  404/502 with no log line at all on every failure path, so there was no way to tell which of several
  possible causes was actually firing. Both routes now log a warning identifying which case occurred:
  the requested segment path not matching the node's *current* storage root (e.g. after a per-node
  storage root override was changed, since `Segment.FilePath` is baked in at record time and never
  rewritten), the segment file genuinely missing from disk despite its `Segment` row, or ffmpeg
  producing no frame (timeout, corrupt segment, an offset/crop past the file's actual content). No
  behavior change — purely additive logging to narrow down the real cause from the node's own log the
  next time a card fails this way.

### Notes

- Node change — rebuild and re-run `install-node.ps1` (or let auto-update pick it up) to get the new
  log lines.

## [0.161.3] - 2026-08-30

### Fixed

- **The Snapshots page still timed out (`SqlException: Execution Timeout Expired`) after 0.161.2's
  index.** The index was not the real problem. Pass 2c's footage-coverage test is an *interval-overlap*
  test (`segment.StartUtc < span.EndUtc AND segment.EndUtc > span's play-from point`), and a B-tree
  index can only seek on one range boundary — the other side is always a residual scan. Expressed as a
  correlated subquery it therefore runs per candidate row, and `CountAsync` makes "candidate" mean
  every `MotionSpan` the page's filters allow (tens of thousands) against a `Segments` table holding a
  row per minute per camera. No index makes that shape fast; three successive attempts to fix it by
  rewriting the query (0.161.0 through 0.161.2) each failed live for a different reason.
  **The coverage check now runs after pagination, over just the (at most 24) rows actually being
  rendered**, where every predicate is a constant and each check is an ordinary index seek. Same
  visible behavior — cards whose footage is gone, including spans that fall inside a genuine recording
  gap, still don't appear — and it now uses each camera's own resolved pre-roll rather than one global
  value.
- Deliberate trade-off, documented in the code: the page's total/page count is computed *without* the
  coverage filter, so a page can render fewer than 24 cards and the count can slightly overstate.
  Making the count exact requires the coverage test back inside the counted query, which is precisely
  the thing that cannot be made fast. An approximate count is worth a page that loads.

### Notes

- Web-only fix (`TimelineService.cs`) — no Node change, no schema change. 0.161.2's
  `IX_Segments_CameraId_EndUtc` index is kept: it did not fix the timeout on its own, but it does make
  the new per-row coverage seeks (`CameraId` + `EndUtc >`) efficient, and it is a non-clustered index
  on a table written once per completed recording segment, so it costs effectively nothing.

## [0.161.2] - 2026-08-30

### Fixed

- **Attempted fix for the Snapshots page timing out on every load
  (`SqlException: Execution Timeout Expired`)**, reported live immediately after 0.161.1: added a
  `(CameraId, EndUtc)` index on `Segment`, on the theory that the correlated overlap check was scanning
  for want of an index on `EndUtc`. **This did not fix it** — see 0.161.3 for the actual cause (the
  overlap predicate is a range-on-both-sides test that no B-tree can fully seek, evaluated per row by
  `CountAsync`). The index itself is kept, since it does make 0.161.3's bounded per-row checks
  efficient.

### Notes

- Schema migration: new non-clustered index `IX_Segments_CameraId_EndUtc`. Building it may take a
  moment on a `Segments` table with a large history — expect the migration step of the next deploy to
  pause briefly rather than complete instantly.

## [0.161.1] - 2026-08-30

### Fixed

- **The Snapshots page threw an unhandled exception on every load** (`InvalidOperationException:
  ... could not be translated`), reported live immediately after deploying 0.161.0. Pass 2c's
  new query-time footage guard (`GetSnapshotsAsync`) used a shape SQL Server's EF Core provider
  can't translate — first a correlated `Any()` combined with a `Min()` subquery in one `Where`
  predicate, then (after a first attempted fix) a `Join` against a `GroupBy` followed by
  `DateTime`-minus-`TimeSpan` arithmetic on the joined column, which also failed to translate.
  Rewritten (this release) as a single, plain correlated `Any()` (`EXISTS`) testing for an
  actually-overlapping segment — the simplest shape that reliably translates on every provider.
  **This version timed out live under real data volume** (missing supporting index) — fixed in
  0.161.2 by adding the index, not by changing this query again.
- **Many plain-motion/zone Snapshots cards still showed "No thumbnail available" even once the page
  loaded**, reported live in the same session — unrelated to anything Pass 3 addresses (that pass
  is about AI-detection crop quality, not plain-motion thumbnail lookup). The guard's first working
  version only checked "is this span newer than the camera's *overall* earliest remaining segment,"
  which is a real gap, not just an approximation: a span can be newer than a camera's oldest
  surviving footage while its own specific instant still falls inside a genuine recording gap (a
  node restart, a dropped RTSP connection) with nothing actually covering it. This release's `Any()`
  guard is what actually fixes this correctly (once 0.161.2's index makes it fast enough to ship).

### Notes

- Web-only fix (`TimelineService.cs`) — no Node change, no schema change.

## [0.161.0] - 2026-08-29

### Fixed

- **One moving object could produce several snapshots.** D-FINE's per-frame class guess can flicker
  for a single physical object (a cat crossing frame read as cat → dog → cow → horse), and the
  detection pipeline's hysteresis/best-frame/reporting state was keyed by that raw per-frame label —
  each label flickered through opened its own independent span and its own snapshot. Pass 2a of the
  detection/hardware-acceleration overhaul. New `TrackLabelArbiter` resolves each ByteTrack track id
  to one stable label (the highest peak-confidence label seen for that track, with a confidence
  margin a challenger must clear to take over) before hysteresis/best-frame tracking/reporting ever
  see it — the live detection overlay keeps showing the model's raw, unarbitrated per-frame guess.
- **Clicking a Snapshots card dropped the viewer in at the exact detection instant, with no lead-in**
  — the object had often already entered frame during the recording's own pre-roll, but Playback
  started at the moment it was confirmed. Pass 2b. `SnapshotDto` gains `PlayFromUtc`
  (`StartUtc` minus the camera's configured pre-roll, applied uniformly across every span kind),
  separate from the existing `AtUtc` (the thumbnail's own sample point, whose AI/classified branch is
  deliberately pre-roll-less — reverting that once was a confirmed live regression, so it is
  untouched). Both Playback links on the Snapshots page now use `PlayFromUtc`; the thumbnail image
  itself still uses `AtUtc`.
- **A `MotionSpan` row lived forever even after its footage aged out of retention**, leaving a
  permanent "No thumbnail available" placeholder card. Pass 2c. New `MotionSpanRetentionService`
  mirrors `BookmarkRetentionService`'s own "entries expire with the footage they point at" pattern
  (6-hour cadence, same staleness rule — compared against `PlayFromUtc`, not `StartUtc`, since the
  pre-roll lead-in is part of what the card promises to play) but batches via keyset pagination
  rather than loading the whole table, since `MotionSpans` runs far higher-volume than `Bookmarks`.
  `GetSnapshotsAsync` also gained a query-time guard hiding an aged-out span immediately, rather than
  waiting for the next sweep. A new node-authenticated `GET /api/nodes/snapshots/span-ids` plus a
  `StorageManager` reconciliation sweep (mirroring the existing segment-reconciliation pattern, with
  the same reachability-probe/re-check/implausible-mass-deletion guards) self-heals any cached
  snapshot crop file left behind once its owning row is gone.

### Notes

- Node change (pass 2a is Vision Service, 2c's reconciliation sweep is `LarisVMS.Node`) — rebuild and
  re-run `install-node.ps1` on every recorder.
- No schema migration needed — pass 2c adds no new columns, only a new background sweep and a new
  read-only endpoint. A version-bump migration (`AppVersions` insert only) still needs generating:
  `dotnet ef migrations add BumpVersion0_161_0 --project src/LarisVMS.Infrastructure --startup-project src/LarisVMS.Web`
  (empty scaffold since there's no pending model change — add the `INSERT INTO AppVersions`/
  `DELETE FROM AppVersions` SQL to its `Up()`/`Down()` by hand, matching every other BumpVersion
  migration in this file's history) — not generated as part of this change, same standing reason as
  earlier passes' migrations.

## [0.160.0] - 2026-08-29

### Changed

- **AI detection now fits each camera's own real aspect ratio into D-FINE's square input, instead of
  decoding every camera to one fixed global resolution and stretching whatever came out into a
  square regardless of shape.** Pass 1 of the detection/hardware-acceleration overhaul. A new
  `InferenceProfile` (per camera) computes the network-input geometry from that camera's real
  Sub-stream dimensions (the ffmpeg-probed ground truth, `CameraStream.Width/Height` — see
  `RecordingSession.TryParseVideoStreamLine`'s own reasoning for why that beats ONVIF's advertised
  value) and the deployment's chosen aspect mode:
  - **Letterbox** (new default): preserves the camera's own aspect ratio, padding with black bars to
    fill the square — a portrait or panoramic camera keeps correct proportions instead of being
    squashed. `VisionSession`'s own ffmpeg filter chain now does the aspect-preserving scale and pad
    directly (a plain, always-CPU `pad` filter — deliberately not attempted inside `scale_cuda`'s own
    GPU filter chain, an unverified pairing this codebase has already been burned by once with
    privacy-mask burn-in), so the captured frame arrives pre-sized and pre-letterboxed.
  - **Stretch**: today's pre-existing behavior (independent per-axis scale, no padding), kept so the
    change is bisectable.
  - A third mode, **AspectMatched**, is reserved for a possible future pass but not implemented —
    requesting it throws.
- **`SKBitmap.Resize` is gone from the AI-detection hot path.** Before this, `VisionSession` always
  decoded to a fixed 1280x720 and `DFineEngine.Preprocess` did a second, separate non-aspect-
  preserving resize down to D-FINE's 640x640 input. Now `VisionSession`'s ffmpeg filter chain decodes
  directly to the exact network input size (stretched or letterboxed, per the mode above), so
  `Preprocess` only ever packs pixels — a real CPU reduction on top of pass 0's motion-decode fix,
  measured per detection-enabled camera rather than per ServerMotion camera.
- New per-node setting, "Aspect fitting" (`Detection.AspectMode`, global default on
  `Admin → Settings → Detection`, per-node override on `Admin → Nodes`, same shape as the existing
  detection-model picker) — Letterbox or Stretch.
- **Retired the global `Detection.Width`/`Detection.Height` settings** (a single decode resolution
  every camera shared regardless of its own shape) — decode resolution is now always derived
  per-camera from its own real stream dimensions. Nothing to migrate: these were never exposed in the
  admin UI, so no stored value needs reconciling.
- Live-detection overlay boxes (`live-view.js`) now line up correctly on non-16:9 cameras — their
  coordinates are normalized against the camera's own real aspect ratio rather than the old fixed
  global decode resolution, which visibly drifted the more a camera's shape differed from 16:9.

### Notes

- Node change — rebuild and re-run `install-node.ps1` on every recorder to pick up the letterbox/
  stretch decode path and the new setting.
- No schema migration needed — `Detection.AspectMode` is a `Setting`/`SettingOverride` row, not a new
  column. A version-bump migration (`AppVersions` insert only) still needs generating:
  `dotnet ef migrations add BumpVersion0_160_0 --project src/LarisVMS.Infrastructure --startup-project src/LarisVMS.Web`
  (this produces an empty scaffold since there's no pending model change — add the
  `INSERT INTO AppVersions`/`DELETE FROM AppVersions` SQL to its `Up()`/`Down()` by hand, matching
  every other BumpVersion migration in this file's history) — not generated as part of this change,
  same standing reason as pass 0's migration.

## [0.159.0] - 2026-08-29

### Changed

- **Server-side motion detection (`MotionSession`) now runs on the same NVDEC/CUDA decode path AI
  detection already uses, on nodes with an Nvidia accelerator resolved.** Pass 0 of the detection/
  hardware-acceleration overhaul: this was the single largest CPU cost in the whole detection stack
  — every camera with a ServerMotion zone ran a continuous *software* video decode of its Sub stream
  regardless of the node's accelerator, unlike recording and live view (`-c copy`, near-zero) or AI
  detection (already GPU-decoded). The CUDA path decodes and scales on the GPU (`scale_cuda` +
  `hwdownload`, the same pairing `VisionSession` already proved end to end) and reads the resulting
  nv12 frame's Y (luma) plane directly — no format-specific code downstream, since a plain grayscale
  frame and nv12's Y plane are byte-identical in shape. Any other accelerator (or none) keeps
  today's plain software decode unchanged; Intel/AMD hwaccel decode for this session is future work.
- **New per-camera "Run server-side pixel motion detection" toggle** (`Cameras/Edit`, default **on**
  — no behavior change on upgrade). Added specifically because `MotionDetectionSource` only chooses
  which signal *gates Motion-mode recording*; it was never a switch for whether `MotionSession` runs
  at all. Before this toggle, a camera whose chosen source was AI detection (or another source) still
  paid for a full Sub-stream decode purely to keep tagging the timeline with plain motion as a
  fallback in case something triggered motion without an object being detected — genuinely useful,
  but not something every camera needs. Turning it off now stops that session (and its decode cost)
  outright; leaving it on keeps exactly today's behavior.

### Notes

- Node change — rebuild and re-run `install-node.ps1` on every recorder to pick up the CUDA motion
  decode path and the new toggle.
- **Migration still needed**: `Camera.ServerMotionEnabled` (new column) requires
  `dotnet ef migrations add AddCameraServerMotionEnabled --project src/LarisVMS.Infrastructure --startup-project src/LarisVMS.Web`,
  run before `dotnet ef database update`/deploy — not generated as part of this change (this
  environment doesn't run `dotnet` commands; see the standing project convention on that).
  `ApplicationDbContext` already configures `HasDefaultValue(true)` for the new column, so the
  generated migration should show `defaultValue: true` on its `AddColumn` call — worth a quick look
  at the generated file before applying, since every existing camera needs to land on `true` (no
  behavior change on upgrade) rather than bool's usual `false` default.

## [0.158.0] - 2026-08-26

### Fixed (post-D-FINE follow-up, confirmed against real hardware)

- **`LarisVMS.Vision.Service` CPU usage went up, not down, after the D-FINE switch** — the opposite
  of the expected effect (DETR-style detection has no per-frame NMS, unlike YOLO). Root cause:
  `DFineDecoder` was computing `Sigmoid` (`Math.Exp`) unconditionally for every query×class
  combination every frame (300×80=24,000 calls for the default model, ~110,000 for the Obj365
  variant), and `DFineEngine`'s preprocessing used `SKBitmap.Pixels` (a full per-pixel color-space
  conversion) plus the tensor's generic multi-dimensional indexer, both avoidable per-frame costs.
  Fixed by pre-filtering candidates in logit-space before ever calling `Exp` (sigmoid is monotonic,
  so this is a cheap comparison with identical results) and by reading raw pixel bytes directly into
  the tensor's own flat buffer instead.
- **AI-detection snapshot crops sometimes missed the detected object entirely** (an empty region
  where it would have been) once D-FINE's tighter, more accurate boxes shipped. Root cause: the
  crop margin (`SnapshotImageCapture.DefaultMarginFraction`) was purely relative to the detected
  box's own size — reasonable with YOLOv9's looser boxes, but D-FINE's tighter ones leave far less
  absolute pixel slack to absorb the pre-existing timing gap between Sub-stream detection (where the
  box was computed) and the Main-stream recording (which the crop is actually taken from) — the same
  gap visible on the *live* detection overlay running slightly ahead of the video it's drawn over.
  Increased the margin and added a floor relative to the *frame's* own dimensions (not just the
  box's), so a small/distant object's tiny box still gets meaningful absolute slack instead of
  scaling down to near-zero alongside it.
- Snapshots page cards now show seconds in their displayed timestamp (was minutes-only) — useful for
  lining up exactly when a snapshot's frame was grabbed against the live view or recording, given
  the timing gap noted above.

### Changed

- **Replaced YOLOv9 with D-FINE as the default AI detection model.** YOLOv9's weight license isn't
  permissive enough for this project; D-FINE (Apache-2.0, end to end) is. Rather than adapt D-FINE
  to impersonate a YOLO architecture for YoloDotNet's own decoder (the same graph-surgery trick
  YOLOv9 needed, and the approach already rejected for RT-DETR in this codebase), `DFineEngine`/
  `DFineDecoder` run D-FINE's real ONNX output directly via `Microsoft.ML.OnnxRuntime` — verified
  end-to-end against a real model run (manually replicating the exact preprocessing/decode math
  against the classic COCO "two cats + remote controls" test image correctly recovered both cats,
  the couch, and both remotes, with clean non-duplicated boxes and no NMS needed) before any
  production code was written. `YoloEngine.cs` is deleted; `YoloDotNet` itself stays as a dependency
  only for its `ObjectDetection`/`LabelModel` types (so `ByteTracker`/`MovementClassifier`/
  `CameraDetectionPipeline` needed no changes) and for its per-accelerator native ONNX Runtime
  packaging. D-FINE is also DETR-style (300 queries, no per-frame NMS) rather than YOLO's dense
  anchor-grid decode+NMS, which — beyond the licensing fix — is expected to noticeably reduce the
  CPU load the previous pipeline was putting on the host while leaving the GPU underused; the
  first real-hardware run should confirm this.
- Detection model selection is now a real, user-facing choice — Admin → Settings → AI Detection
  (`Detection.ModelFamily`/`Detection.DFineWeights`, global default + per-node override on Admin →
  Nodes, since one Vision Service process serves every camera on a node from the same loaded
  model). Two D-FINE weight variants are selectable: `Obj2Coco` (80 COCO classes, default) and
  `Obj365` (365 Objects365 classes, a much richer vocabulary). RF-DETR and YOLOX are reserved
  picker slots (rendered disabled) for a future release — Auto's own Intel/AMD/CPU default would
  normally pick YOLOX, but falls back to D-FINE until it actually ships, so a non-Nvidia node keeps
  detecting rather than silently going dark.
- `tools/export-models/export.py`/`onnx_compat.py` (YOLOv9-specific export/graph-adaptation) are
  replaced by `tools/export-models/fetch_dfine.py` — a plain, pinned-revision download from
  Hugging Face, no export/adaptation step at all since D-FINE's own published ONNX already matches
  what `DFineEngine` expects.
- `CocoCategoryMap`'s `Vehicle`/`Animal` word lists now also cover Objects365's much larger
  vocabulary (`SUV`, `Wild Bird`, `Rickshaw`, ...), and D-FINE's own legacy PASCAL-VOC-era COCO
  spellings (`motorbike`, `aeroplane`) — previously absent, so a motorbike or aeroplane detection
  silently fell through to the generic "Object" category instead of "Vehicle".

### Fixed

- **Login was broken site-wide: the password field rendered as plain text and every sign-in attempt
  failed with HTTP 400**, identically across every browser/device. Root cause: `Areas/Identity/Pages/`
  has its own `_ViewStart.cshtml` (routes the login page through this app's shared layout) but no
  `_ViewImports.cshtml` — and Razor's tag-helper registration is picked up by walking up the directory
  tree from the page itself, which never reaches `Pages/_ViewImports.cshtml` since `Areas/Identity/Pages/`
  isn't a descendant of `Pages/`. Without `@addTagHelper` active, `asp-for="Input.Password"` never
  rendered `type="password"` (plain `<input>` defaults to text), and the form's antiforgery hidden
  input — normally auto-injected by the FormTagHelper — never appeared, so every POST failed
  antiforgery validation. Added the missing `Areas/Identity/Pages/_ViewImports.cshtml`.

- AI-detection snapshots reported duplicate spans for a single continuous sighting of the same
  object. `CameraDetectionPipeline`'s per-label `MotionHysteresis` was constructed with `endAfter:
  TimeSpan.Zero`, so a single missed/occluded detection frame — or a track dipping into `Idle` for
  even a moment before resuming `Moving` — closed the span immediately, and the very next detection
  opened a brand-new one. Added a real grace period (`Detection.IdleTimeoutSeconds`, default 10s,
  global setting) so brief flicker no longer fragments one sighting into several snapshots, while a
  genuinely idle/departed object still finalizes its span once the grace period elapses.
- AI-detection "best frame" snapshot cropping could pick the wrong object when multiple detections
  shared a label — e.g. a snapshot for a car driving past a driveway showing the parked truck
  already in frame instead of the car, because the truck's larger, more stable box out-scored the
  car's on every frame. `CameraDetectionPipeline` now only lets a detection compete for "best frame"
  while it's actually contributing to the reported span (`Moving`, not `Idle`), and tracks a
  separate "oversized" candidate pool (boxes covering more than 80% of the frame) that's only used
  as a fallback when no normal-sized candidate exists at all — so a genuine close-up (a face filling
  the frame) still gets captured correctly.
- Every AI-detected snapshot showed the same generic 📦 emoji regardless of category (Human, Vehicle,
  Animal, Object) — `TimelineService.GetSnapshotsAsync`'s AI-category branch hardcoded the emoji
  literal instead of looking it up per category. Added `CocoCategoryMap.Emoji`, mirroring the
  per-class lookup the camera-native `DetectionDisplay.Emoji` already had.
- AI-detection bounding boxes didn't track zoom/pan on a fullscreened live tile — the box overlay is
  a plain `<canvas>` sibling of the `<video>` element, and zoom/pan is applied as a CSS transform
  directly on the video itself, so the boxes stayed fixed at their pre-zoom screen position while the
  video content scaled/slid underneath them. `fullscreen-tile.js` now exposes the exact transform
  string it applies to the video, and the overlay canvas applies the identical string to itself on
  every redraw, so the two can never drift out of sync with each other.
- Live-view tiles never showed a badge for AI-detected objects at all — the badge-polling query only
  ever looked at camera-native `DetectionKind` spans. Badges for AI categories are now derived
  client-side from the same live per-frame WebSocket stream already used to draw detection boxes
  (`live-view.js`), since only that live state — not anything persisted in the database — actually
  knows whether a specific track is moving right now. Shows one badge per unique specific label
  currently moving (a moving car and a moving truck both badge separately), never for idle/stationary
  objects.
- **Clicking a snapshot (or any other autoplay deep link) landed on a paused tile that needed a
  Pause-then-Play click before video actually started.** Root cause, in `playback-player.js`:
  `resolveDeepLink` set the page's "playing" state to true before any tile had a source attached,
  which drove `applySpeed` to call `videoEl.play()` on a source-less `<video>` — per spec that's not
  a no-op, it makes the element "potentially playing" at `readyState = HAVE_NOTHING` and fires a
  `waiting` event, arming the 3-second stall watchdog for a load that hadn't even started. The real
  segment fetch (routinely longer than 3s for a cold/large 4K segment) then tripped that watchdog's
  recovery path, which read `!videoEl.paused` — false, since `teardown()` had legitimately paused the
  element for the reload — as "don't resume autoplay", stranding the tile paused while the toolbar
  still read "Pause". Fixed two ways: `applySpeed` no longer calls `play()` on a tile with no segment
  attached yet (the one call that was arming the watchdog spuriously), and recovery now asks each
  tile whether the *page* currently intends it to be playing instead of trusting the element's
  momentary paused state during a reload — the give-up path (after repeated recovery failures at the
  same target) also resets the page's Play/Pause button to match, rather than leaving it stuck
  showing "Pause" over a tile that stopped trying to resume.

### Changed

- Unified the two separate "a person was detected" vocabularies: camera-native detections
  (`DetectionKind.Person`, from onboard ONVIF/CGI analytics) and the AI pipeline's own `Human`
  category never shared a filter token before, so the Snapshots page showed two separate "person"
  checkboxes for what is conceptually one thing. Renamed the enum member to `DetectionKind.Human`
  (its persisted value is unchanged) so it now shares the same name/filter token/emoji as the AI
  category's `Human`, the same way `Vehicle`/`Animal` already do — the two checkboxes collapse into
  one automatically via the Snapshots page's existing dedupe-by-name rendering.

### Added

- AI detection's confidence/IoU thresholds and which stream it watches (Main/Sub) are now real
  settings, editable on a new AI Detection tab (`Admin/Settings/Detection`) with the same
  global-default + per-camera-override pattern Recording settings already use. Confidence/IoU were
  previously deployment-wide only, with no admin UI to change them at all (`NodeService` read them
  from a `Setting` row nothing ever wrote); which stream feeds detection used to be unconditionally
  hardcoded to Sub in `NodeWorker.ReconcileVision`, with no way to change it. Confidence/IoU are
  entered as sliders (5% steps, 50% centered) rather than free-text boxes, with a live readout next
  to each that tracks the drag as it happens.
- The camera list now shows which cameras have AI detection enabled and which stream it's watching
  (`🤖 Sub`/`🤖 Main` next to the camera name) — previously only visible by opening each camera's own
  Edit page one at a time.

- The Snapshots page filter is now a collapsible tree in a left-side sidebar next to the snapshot
  grid, instead of a flat row of checkboxes above it. Each AI-detection category (Vehicle, Animal,
  ...) that has more than one specific label actually seen expands to show a checkbox per label (car,
  truck, bicycle, ...) — unchecking a whole category still excludes everything under it as before,
  and unchecking one specific label now excludes just that label while its siblings stay included.
  The tree is populated live from what's actually in the database, not a fixed list, since which
  categories/labels exist depends entirely on which detection model produced them.

## [0.157.1] - 2026-08-25

### Fixed

- `build-node.ps1`'s model-bundling step threw `The property 'Count' cannot be found on this object`
  under `Set-StrictMode -Version Latest` whenever exactly one `.onnx` file matched — `Get-ChildItem`
  unwraps a single-item result to a bare `FileInfo` instead of an array, and strict mode turns the
  resulting missing-`.Count` into a terminating error rather than `$null`. Wrapped the result in `@()`
  to keep it an array regardless of match count.
- `install-node.ps1` hit the same `Set-StrictMode`/`.Count` bug as `build-node.ps1` above, in
  `Stop-OrphanedFfmpeg` and `Stop-OrphanedVisionService` — `Get-Process` unwraps to a bare `Process`
  instead of an array when exactly one orphaned process matches, and `.Count` doesn't exist on it
  under strict mode. Same fix, wrapped in `@()`.
- Live-view Moving/Idle AI-detection bounding-box overlay (`live-view.js`'s `startDetectionOverlay`,
  shipped earlier in this same release) was never actually wired up — no checkbox existed anywhere on
  `Views/Play`, and nothing ever called it. Added a Moving/Idle checkbox pair to the Play toolbar and
  wired `view-play.js` to start one overlay per tile and apply both toggles to every tile at once.
  Both toggles persist per-user via the existing server-backed preferences store (not localStorage),
  the same as playback-player.js's own toggles — they follow the viewer across devices/logins rather
  than resetting on every page load.
- **`LarisVMS.Vision.Service` couldn't find ffmpeg.** `VisionServiceSupervisor` never told the child
  process where it lives — `VisionServiceOptions.FfmpegPath` was left unset, so
  `FfmpegPathResolver.Resolve(null)` fell back to a bare `"ffmpeg"` relying on `PATH`, which this
  service (normally LocalSystem) doesn't have. `LarisVMS.Node` itself always knows the real path
  (`--ffmpeg-path`, `LARISVMS_FFMPEG_PATH`, or its own PATH probe, resolved once at its own startup)
  and now passes it straight through via `Vision__FfmpegPath`, so the two processes can't disagree
  about which ffmpeg they're each running.
- **`build-node.ps1` never bundled an exported model.** It searched `models/` non-recursively, but
  `tools/export-models` drives libreyolo, which writes to `models/weights/` next to the `.pt` it
  converted from — so a perfectly good export was reported as "No .onnx model found" and the package
  shipped with an empty `models/` folder. Now searched recursively and flattened into the package.
  `tools/export-models/README.md` corrected too: it claimed output lands in `models/` directly.
- **False gaps in recording coverage — Playback reporting "no recording available" for windows a
  real, intact segment actually covered, even in Continuous mode.** `RecordingSession` computes each
  segment's `StartUtc`/`EndUtc` from `firstRealDataObservedUtc`, a dictionary meant to hold one
  timestamp per file, shared between two independent lookups that are supposed to describe the same
  physical instant: the end of segment N and the start of segment N+1 both mean "when did the file
  that became segment N+1 first receive real data." The lookup for each fell back to that file's raw
  `CreationTimeUtc` when the dictionary had no entry yet — but never wrote that fallback *into* the
  dictionary, so if the preceding segment's `end` resolved via the fallback (file still header-only,
  below `MinRealDataBytes`, on that poll) while the file's *own* `start` was resolved on a later poll
  once real data had actually grown it past the threshold, the two independently-computed values for
  the same instant could — and confirmed live against a production `Segments` table, routinely did —
  diverge, by anywhere from a couple of seconds to nearly a minute. Found via a snapshot that existed
  with no playable recording behind it at the same timestamp: the underlying video was continuous
  throughout — this was purely an indexing gap. Root-caused by directly reading the recorder's Segments
  table and comparing filename-encoded vs. true (NTFS `LastWriteTimeUtc`) segment write times to rule
  out a suspected local/UTC labeling bug in `RecordingSession.StartFfmpeg`'s (deliberately local,
  cosmetic-only, and unrelated) `%Y%m%dT%H%M%SZ` folder/filename pattern before finding the real cause.
  Fixed by routing both lookups through one write-through resolver, so whichever side asks first
  permanently locks in the answer for both — `end(N)` and `start(N+1)` are now provably equal by
  construction. Affected roughly 41% of all segment transitions in one night's recording, repaired
  retroactively across the production `Segments` table (8,531 rows corrected, ~17.8 hours of
  already-recorded footage made playable again; 72 rows left untouched as likely genuine outages,
  distinguishable by size — every false-gap value fell under a minute, while the excluded rows ranged
  from just over a minute to over an hour).
- **Live-view detection boxes never drew, despite the WebSocket streaming real data every tick.**
  `ProxyDetectionOverlayAsync` (Web's `/live/{cameraId}/detections` proxy) re-serialized each tick
  with a bare `JsonSerializer.SerializeToUtf8Bytes(enriched)` — no options, so .NET's PascalCase
  default (`MovementState`, `X`, `Y`, `W`, `H`, `ColorHex`, ...) shipped to the browser instead of
  the camelCase this app's JS everywhere else expects (`Results.Json`'s own Minimal API default, and
  every hand-written anonymous-object endpoint already written in camelCase to match). `live-view.js`'s
  `draw()` reads `box.movementState`/`box.x`/`box.colorHex`/etc. — all `undefined` against the
  PascalCase payload, so `box.x * dw` became `NaN` and `ctx.strokeRect(NaN, NaN, NaN, NaN)` silently
  drew nothing. No exception anywhere in the chain: confirmed live via the browser's own WS inspector
  showing real, correctly-shaped detections arriving every ~150-300ms while the canvas stayed
  empty. Also meant the Moving/Idle checkboxes were never actually filtering anything, since
  `undefined !== 'Moving'`/`'Idle'` never matched either. Fixed at the one serialize call with an
  explicit camelCase `JsonSerializerOptions`, built once per viewer connection rather than per tick.
- **AI detection could never find its model on any node.** `VisionServiceOptions.ModelPath` defaulted
  to the literal filename `models/model.onnx`, but nothing in this project produces that name —
  `tools/export-models` emits the upstream weight name (`yolo9-t.onnx`, `yolo9-s.onnx`) and
  `build-node.ps1` bundles whatever `.onnx` files exist verbatim. So a correctly built, correctly
  installed package with a correctly configured GPU still failed every start with ONNX Runtime's
  `Load model from models/model.onnx failed. File doesn't exist`. Vision Service now discovers the
  bundled model: an explicitly configured path that exists still wins, otherwise it uses what was
  actually bundled in that directory. More than one bundled model is the normal case rather than an
  error (export.py's own default set is two), so it picks deterministically by name and logs which,
  instead of refusing to start; none at all now fails with a message naming the directory and
  pointing at the exporter.
- **Stopping the node service left `LarisVMS.Vision.Service.exe` running.** `NodeWorker`'s shutdown
  stopped the Vision Service child as the *last* step of its `finally` block — behind awaiting every
  recording/motion/event/integration session and four `CancellationToken.None` server flushes, each
  carrying the API client's own timeout. A slow or unreachable server at shutdown pushed that past
  the host's `ShutdownTimeout`, and when that fired the remaining cleanup never ran, so the one step
  that must not be skipped was the first one lost. The orphan then held its own exe and the
  CUDA/cuDNN natives beside it open, failing the next in-place upgrade with "being used by another
  process". Stopping the child is now the first thing the `finally` does, and the child is
  additionally placed in a Windows job object with `KILL_ON_JOB_CLOSE` so the OS terminates it
  whenever the node process goes away — covering the paths no shutdown code can reach at all
  (`ShutdownTimeout` expiry, a crash, `taskkill`, the SCM giving up on a hung stop).
- `install-node.ps1` now checks the AI-detection native dependencies at install time and, where it
  can, fixes them: it reads which accelerator the package was actually built for from the provider
  DLL beside the exe, verifies each required CUDA/cuDNN library resolves the way the OS loader would
  (install directory, system directories, and the machine `PATH` from the registry rather than a
  possibly-stale `$env:PATH`), and copies anything missing in from where these libraries actually
  land — a pip `nvidia-*` site-packages `bin`, NVIDIA's own cuDNN layout, or the CUDA Toolkit's
  `bin`. cuDNN copies bring the whole `cudnn*.dll` set, since `cudnn64_9.dll` is a dispatcher that
  loads its siblings at runtime and copying only the DLL named in a loader error just moves the
  failure to the next one. Copying beside the exe rather than editing `PATH` is deliberate: a
  Windows Service inherits its environment from `services.exe` at boot and would not see a new
  `PATH` entry until the machine is rebooted. Anything genuinely absent is reported with where to
  get it, and a missing `.onnx` model is called out too. Never fatal — a node without GPU libraries
  still records normally.
- A `LarisVMS.Vision.Service` startup failure was undiagnosable from the node's own log, which said
  only `Vision Service returned InternalServerError` once per reconcile tick while the actual reason
  existed nowhere but the Windows Application event log. Vision Service's `/start` now catches the
  failure and returns the flattened exception message chain as ProblemDetails, and `NodeWorker` reads
  that body into its warning — so a machine-level setup problem (a missing CUDA runtime DLL, an
  absent `.onnx` model, a GPU the driver won't hand out) names itself in `node-*.log` directly.
  Found via a recorder that was missing `cublasLt64_12.dll`: the CUDA Toolkit runtime has to be
  installed on any node running a `-Accel Cuda` build, since neither the NuGet packages nor
  `install-node.ps1` provide it. README gains a "Recorder node dependencies" section covering this —
  what each `-Accel` variant needs installed on the node, and that both `deploy.ps1 -NodeAccel` and
  `build-node.ps1 -Accel` silently default to `Cpu`.
- `Cameras/Edit`'s "Watch this camera for AI object detection" checkbox and "Motion detection source"
  dropdown always reset to unchecked/auto on the next page load, even though the save itself worked —
  `CameraService`'s hand-rolled `ProjectWithoutCredentials` projection (used by every `GetAsync`/
  `ListAsync` read) explicitly lists which `Camera` columns to carry over, and the two columns this
  release added were never added to that list, so every read silently came back with the type's
  default (`false`/`null`) regardless of what was actually stored. The Node-side config path reads
  `Camera` directly and was unaffected — recorders had the correct value the whole time, only the web
  UI's own display of it was wrong.

Node change — rebuild and re-run `install-node.ps1` on every recorder to pick up the CUDA/cuDNN
detection, ffmpeg-path, model-discovery, and Vision Service shutdown fixes above; a node not yet
rebuilt keeps working exactly as it did under 0.157.0 (recording unaffected either way). No new
migration content beyond this version-bump row — the Segments repair above was applied directly, not
through a schema change.

## [0.157.0] - 2026-08-24

### Added

- **Native AI object detection.** LarisVMS now runs its own real-time YOLO/ByteTrack detection
  pipeline per node instead of depending on onboard camera analytics or a separately-run debug
  process (`aitest`, the standalone repo this was prototyped and validated in — now untouched,
  read-only reference). The existing onboard-classification object detection (ONVIF PullPoint /
  Dahua-Amcrest CGI, see 0.30.0-era entries) is unaffected and keeps working exactly as before; this
  is a second, independent detection source layered alongside it, not a replacement.
  - **Capture**: a new `VisionSession` (`LarisVMS.Vision`) opens a *second*, independent RTSP session
    against a camera's Sub stream — the same established pattern `MotionSession`/`SubLiveSession`
    already use for exactly this kind of analysis workload — decoding via a GPU-hybrid ffmpeg pipeline
    (`-hwaccel cuda -hwaccel_output_format cuda` + `scale_cuda`, falling back to plain CPU scale on
    other accelerators). Inference (YoloDotNet/ONNX Runtime) and tracking (a full MIT-licensed
    ByteTrack port — Kalman filter, track matching, the works) run against its frames, with a new
    `MovementClassifier` splitting each tracked object into Moving/Idle by centroid displacement and
    keeping the single best-scoring frame (`confidence × normalized box area`) seen across its whole
    lifetime for later use as a snapshot.
  - **Runs as a sibling process, not inside `LarisVMS.Node.exe`.** A new `LarisVMS.Vision.Service`
    executable (localhost-only control API) carries the GPU/ONNX Runtime native dependencies, so a
    site that never enables AI detection pays nothing for it, and a bad GPU/driver interaction can
    never take down camera recording itself — `NodeWorker`'s new `VisionServiceSupervisor` starts,
    supervises, and restarts it as a child process, same "desired-state reconcile" shape every other
    node-side session already uses. `LarisVMS.Node` still has **zero** reference to the GPU-dependent
    `LarisVMS.Vision` library.
  - **Hardware accelerator is a per-node setting** (`Admin → Nodes`: Auto / Nvidia / Intel / AMD /
    CPU), not per-camera — a node has one physical machine's worth of hardware. A new
    `AccelCapabilityProber` probes what's actually present at startup and reports it back on every
    heartbeat (`Admin → Nodes`' new readout column); Auto picks the best of what's detected
    (Nvidia > Intel > AMD priority) and never silently falls back to CPU. No usable accelerator (or
    AI detection simply not enabled) means no `LarisVMS.Vision.Service` instance runs at all, logged
    once — every other detection path already configured for every camera on that node is completely
    unaffected. `LarisVMS.Vision.Service` is published once per execution provider
    (`build-node.ps1 -Accel Cuda|DirectML|OpenVino|Cpu`, same MSBuild switch pattern `aitest` proved
    out) since YoloDotNet only links one provider per process — a site with genuinely different
    hardware across nodes needs a package built per accelerator.
  - **Categories, not 80 individual colors.** Detected classes bucket into a small, auto-colored
    catalog (`DetectedObjectCategory`: Human / Vehicle / Animal / Object today, more as new classes
    are first seen) so the timeline doesn't turn into a wall of near-indistinguishable colors the way
    coloring all ~80 COCO classes individually would. The specific detected label still rides
    alongside on every snapshot/timeline entry ("Vehicle — car", not just "Vehicle") — only the
    *category* drives the badge color. A brand-new category gets the next unused color from a curated
    palette, same "pick a color that hasn't been used yet" shape `DetectionDisplay` already has for
    the older onboard-classification kinds.
  - **Live-view bounding boxes are a separate, client-side-only overlay** — a new small WebSocket
    (`/live/{cameraId}/detections`) feeding a `<canvas>` drawn over the video element, never touching
    recorded bytes. Moving and Idle objects have **independent** show/hide toggles (both off by
    default), so a scene full of static objects doesn't have to compete with what's actually moving
    unless a viewer specifically wants to see it too.
  - **Motion-mode recording gains exactly one *primary* generic motion source.** Previously every
    configured signal (server-side pixel zone, ONVIF camera events, a vendor integration) counted
    independently toward a Motion-mode camera's keep/discard decision and toward tagging the
    timeline — redundant when more than one is configured for the same camera, since they're all
    reporting the same real event. A new per-camera `Motion detection source` setting
    (`Server-side motion` / `Camera events` / `Vendor integration` / `AI detection`, defaulting to
    whichever of the first three is actually configured, richest signal first, if never explicitly
    set) narrows the three generic sources to exactly one active at a time. A named event tag rule and
    AI detection's own object labels are deliberately **outside** this restriction — both always tag
    the timeline and can each independently keep a segment, regardless of which source is chosen as
    primary, the same "always-on OR term" precedent event tag rules already had. Being purely
    additional OR terms throughout, none of this can ever discard footage that would otherwise have
    been kept.
  - **Snapshot images for a detected object are cropped to its bounding box** — a new, genuinely
    separate `snapshots/` cache tier (distinct from the existing hover-thumbnail `thumbs/` cache),
    capped at 720p, generated from whichever frame across the whole detection scored best (confidence
    × box size), with margin added around the tight box so it doesn't read as a context-free sliver.
    `StorageManager` deletes a segment's matching snapshot image alongside it at every eviction path
    (retention, quota, watermark, orphaned-camera sweep) — the same "never outlives its source
    footage" guarantee the thumbnail cache already had.
  - **Model export tooling** (`tools/export-models/`) ported byte-for-byte from `aitest` so LarisVMS
    never depends on that repo for its `.onnx` files — exports permissively-licensed (MIT) YOLOv9
    weights and adapts them to load in YoloDotNet. Deliberately excludes Ultralytics YOLOv8/11/26:
    those weights are AGPL-3.0, and this project is MIT.
  - **Recorder-node auto-update now also keeps an already-installed `LarisVMS.Vision.Service.exe`
    current**, not just `LarisVMS.Node.exe` itself. `deploy.ps1` registers the matching Vision binary
    from the same `build-node.ps1` publish alongside the Node exe (`NodeBuildVersion` gains optional
    `Vision*` columns for it — a build without one, e.g. `-SkipNodeVision`, simply offers nothing extra),
    and `LarisVMS.NodeUpdater` swaps it in the same pass as the Node exe, guarded to only ever touch a
    node that already has a working Vision Service install (its native DLLs and exported model are
    placed by `install-node.ps1`, never by this download/swap path, so swapping the bare exe onto a
    node that's never had it would just crash-loop with nothing to load) — a node's *first* Vision
    Service install still needs one `install-node.ps1` run, same as this release's own rollout below.
    Deliberately best-effort throughout: a Vision Service download/checksum/swap failure is logged and
    retried next heartbeat, never blocking or failing the primary Node exe update it rides alongside.
    ⚠️ One binary per platform row, not per node — a fleet with genuinely mixed hardware (some nodes
    GPU-capable, some not) needs to manage the Vision Service binary on those specific machines by
    hand rather than relying on this, since there's no per-node accelerator-variant selection in the
    approval queue (`Admin → Node Builds` flags it explicitly).

  **Unverified against real GPU hardware or an actual camera end-to-end** — every piece above builds,
  and the pure/isolated logic (movement classification, category/color assignment, accelerator
  selection, motion-source resolution, crop-rectangle math, the Vision Service download/checksum
  staging and updater arg parsing) is unit tested, but the capture→inference→tracking pipeline itself,
  the concurrent-RTSP-session count this adds on top of Main recording/Sub motion/Sub adaptive-live
  (now up to four per camera), the snapshot crop's Main/Sub field-of-view alignment, and the Vision
  Service auto-update path's own real Windows-Service-restart choreography all still need a real run
  against real hardware before this is trusted the way the rest of this app's shipped features are.

**Node change — `install-node.ps1` re-run needed on every recorder, once, for this release.** No
already-installed node has ever had `LarisVMS.Vision.Service.exe` (or its native DLLs/`models/`) at
all before this release, and auto-update's download/swap path is only ever a same-file overwrite —
see above — so there's nothing there yet for it to keep current. `LarisVMS.Node` and
`LarisVMS.NodeUpdater` bumped to 0.157.0 in lockstep. A recorder with no rebuilt/re-installed package
keeps recording exactly as before — AI detection is additive and every camera's other detection paths
are unaffected — it just won't offer AI detection until reinstalled; from the *next* release onward,
an already-Vision-equipped node picks up a newer Vision Service exe the same automatic way it already
picks up a newer Node exe. New `DetectedObjectCategory` table, new columns on
`MotionSpan`/`Camera`/`Node`, and new `NodeBuildVersion` Vision* columns — database migration
required (`deploy.ps1` applies it automatically unless run with `-SkipMigrations`).

## [0.156.1] - 2026-08-23

### Fixed

- **Every single page load threw `ArgumentNullException: Value cannot be null. (Parameter 'ClientId')`
  once 0.154.0 shipped** — reported live, not just an Entra sign-in attempt: `AuthenticationMiddleware`
  builds every registered `IAuthenticationRequestHandler` scheme's options on *every* request (to check
  which scheme, if any, owns the current request path), which includes the "EntraID" OpenIdConnect
  scheme regardless of whether Entra sign-in is enabled. Two compounding bugs, both fixed:
  - `EntraOidcOptionsConfigurator` was registered as `IConfigureNamedOptions<OpenIdConnectOptions>`
    in DI — but `OptionsFactory<T>`'s constructor only takes `IEnumerable<IConfigureOptions<T>>`, and
    the container resolves by the exact registered service type, not by every interface the
    implementation happens to satisfy. Registered under the wrong interface meant `Configure()` never
    ran at all, leaving `ClientId` at `OpenIdConnectOptions`' own `null` default. Now registered as
    `IConfigureOptions<OpenIdConnectOptions>`.
  - Even with that fixed, an unconfigured deployment (no `EntraSsoSettings` row, or a blank ClientId)
    would still fail validation, since the configurator fell back to an *empty* string rather than a
    placeholder — `OpenIdConnectOptions.Validate()` requires ClientId non-empty unconditionally. Now
    falls back to a syntactically valid placeholder GUID, same "harmless placeholder, never reached by
    a real sign-in while unconfigured" reasoning `Authority`'s own fallback already used (Login.cshtml
    only offers the "Sign in with Microsoft" button once `EntraSsoSettings.IsEnabled` is true).

Web-only, no node change.

## [0.156.0] - 2026-08-23

### Fixed

- **Some snapshots stalled forever on Play, spamming the console with repeated "tile stalled...
  recovering" / "recovering... reloading" pairs.** The existing stall watchdog (added to catch a
  seek into an instant that will never be buffered) kept reloading at the *identical* stalled
  instant every time, looping under its own 3s cooldown rather than resolving. It now tracks
  consecutive recoveries at the same target: after the 3rd attempt in a row it falls back once to
  that segment's own start, and if still stuck, gives up with a permanent "Playback stalled at this
  point — try scrubbing elsewhere." message instead of looping indefinitely. The root cause of the
  underlying stall itself (most plausibly a node-side I/O hang on a specific segment file, or a
  `Segment.DurationMs`/real-encoded-length drift the same class as the v0.119.0 thumbnail-502 fix)
  is not yet confirmed — this is a bounded mitigation, not a structural fix; flagged for a future
  pass once real reproduction logs are available.

### Added

- **Snapshots search: filter by single camera, all cameras, a camera group, or a saved view**
  (previously only single-camera or all). `ITimelineService.GetSnapshotsAsync` now takes a camera-id
  set instead of one optional id. A new `ICameraGroupService.GetCameraIdsInSubtreeAsync` resolves a
  selected group to itself and every descendant group's cameras — the same `MaterializedPath`-prefix
  cascade `CameraAccessService` already uses for a Group-scoped access grant — and a selected view
  resolves via the existing `ViewLayout.CameraIds` helper plus `IViewService.GetVisibleToAsync`. No
  `CameraAccess` scoping was added to the new modes, staying consistent with the existing
  single-camera filter, which has never had it either.
- **Playback timeline is taller and auto-hides on mobile.** On a phone-width viewport the timeline
  canvases render taller (44px, up from the desktop 30px) for easier pinch-to-zoom, and the timeline
  strip overlays the video grid instead of pushing it down — reusing the same relocate-and-overlay
  CSS shape the existing fullscreen timeline already uses, rather than a new layout. It fades out
  after 5 seconds of no touch/mouse activity and reappears on the next touch, governed by a new
  "Auto-hide timeline" toggle (phone-only, default on) persisted per-user through the existing
  `UserPreference` mechanism, so it's remembered across devices and logins.

Web-only, no node change.

## [0.155.0] - 2026-08-23

### Fixed

- **Changing an already-recording camera's ONVIF Device Service URI never took effect — the camera
  kept recording (and live-viewing) from the old source until deleted and re-added.** Reported live:
  pointing a camera at another camera's device and back left it permanently stuck serving the
  *other* camera's stream. Root cause: `NodeWorker.Reconcile()`'s Main-stream branch never compared
  a camera's current RTSP URI against the one its already-running `CameraRecorder` was started
  with — only a Privacy-mask or segment-length change ever triggered a restart. The Sub/adaptive-
  stream branch (`ReconcileLiveSub`) already did this correctly; the Main-stream branch now runs the
  same signature-and-restart check, so an already-recording camera picks up a changed URL on its
  next reconcile (~30s) instead of needing a delete-and-re-add.

**LarisVMS.Node change — install-node.ps1 re-run needed on every recorder.**

## [0.154.0] - 2026-08-23

### Added

- **M20 pass 3: "Sign in with Microsoft" (Entra ID).** Starting with Entra rather than the roadmap's
  other identity item (LDAP sync) since this deployment already has a Microsoft Graph email provider
  configured (M15 pass 2) — an Entra tenant and app-registration workflow already exist here; LDAP
  assumes an on-prem Active Directory this deployment shows no sign of having.
  - **Sign-in only, never registration.** This app disabled self-registration app-wide (M14,
    `RegistrationDisabledMiddleware`) specifically because a self-created account gets no role and
    there's no invite/approval step. The packaged Identity UI's default external-login callback
    assumes the opposite — first sign-in for any identity falls through to "confirm your email to
    finish creating an account." Two Identity pages are overridden locally (`Areas/Identity/Pages/
    Account/Login`, `.../ExternalLogin` — Razor Pages' own page-override convention, the same
    mechanism `_ViewStart.cshtml` already used to carry this app's layout onto the packaged pages) so
    a first-ever Entra sign-in is matched by email against an **existing**, admin-provisioned
    `ApplicationUser` (`Admin → Settings → Users`) and linked via ASP.NET Core Identity's own
    `AspNetUserLogins` table — no new table needed. No match means rejected with a clear message, not
    silently registered.
  - New `Admin → Settings → Security` section: enable/disable, Tenant ID, Client ID, Client Secret
    (encrypted at rest, same `SecretProtection` pattern every other credential in this app uses). The
    "Sign in with Microsoft" button only appears once enabled — `Login.cshtml`'s own override filters
    the external-scheme list, independent of the OIDC scheme being registered in the pipeline at all
    times.
  - **Takes effect immediately, no app restart** — a real ASP.NET Core options-pattern subtlety: an
    `IConfigureNamedOptions<OpenIdConnectOptions>` reads `EntraSsoSettings` from the database at
    sign-in time rather than once at boot, but the options pattern caches a named options instance
    after its first resolution regardless, so on its own this would still only run once per process.
    The Security page's own save handler explicitly evicts the cached entry
    (`IOptionsMonitorCache<OpenIdConnectOptions>.TryRemove`) after saving, forcing the next sign-in
    attempt to re-resolve with the freshly saved row.
  - **Deliberately out of scope, flagged not silently skipped**: no Entra group→Role mapping (that's
    the separate LDAP-sync roadmap item's own scope) — roles are still assigned manually on the Users
    page, same as every account today; single-tenant only, matching the Graph email provider's own
    shape; no account-linking UI, since first-sign-in auto-link already covers this app's actual use
    case.

**New package reference**: `Microsoft.AspNetCore.Authentication.OpenIdConnect` — despite most
authentication handlers shipping in the ASP.NET Core shared framework, this one doesn't and needs an
explicit `PackageReference` (added to `Directory.Packages.props`).

Web-only, no node change.

## [0.153.0] - 2026-08-23

### Added

- **Field-level help text collapses behind an ℹ️ icon (hover or click to reveal) instead of always
  sitting under the field**, across every admin/settings page and `Cameras/Edit` where it had piled up
  enough to make the form itself read as more help text than form: `Cameras/Edit`, `Admin > Settings >
  Branding/Recording/Storage and Retention/Nodes/Logs/Events/Security/LiveView/Cameras/Backups`,
  `Admin > Settings > Permissions > Roles`, and `Admin > Alerts > Edit`. New `help-popover.js`
  (Bootstrap Popover, `trigger: 'hover focus'` — a click already focuses the icon, so this covers both
  asks from one config without needing a separate outside-click dismiss handler) sources each popover's
  content from a same-page `.help-text-source` element via an explicit `data-help-target` id, not DOM
  proximity — needed because a few fields (Recording mode override, in particular) already had several
  conditional help blocks stacked after one input. Loaded globally in `_Layout.cshtml`, inert on any
  page with no `.help-icon` elements. Genuinely actionable warnings (a missing Motion zone/schedule
  window/event rule) stay as visible, always-shown text — only the plain explanatory blocks collapse.

### Changed

- **`Cameras/Edit` no longer edits group membership** — it now shows an existing camera's current
  groups as plain badges (built-in "All Cameras" excluded, same as every other "current groups" display
  in this app) with a link to `Cameras/Groups`, which already owns adding/removing a camera to/from a
  group via its "Manage cameras" popup. Removes the multi-select, its site-grouped `<optgroup>`
  rendering, and the `SetCameraGroupsAsync` call from this page's save handler entirely — the "Add
  camera" flow no longer offers an initial group either (every new camera still gets the built-in "All
  Cameras" group automatically, per `CameraService.AddAsync`).

Web-only, no node change.


## Archived history

Only the 10 most recent versions are kept in this file. Older entries are archived in full below
(nothing summarized or dropped), newest first, at most 20 versions per file:

- [v0.144.0 – v0.152.0](changelog-archive/v0.144.0-to-v0.152.0.md) (2026-08-23)
- [v0.131.0 – v0.143.0](changelog-archive/v0.131.0-to-v0.143.0.md) (2026-08-21 – 2026-08-23)
- [v0.111.0 – v0.130.0](changelog-archive/v0.111.0-to-v0.130.0.md) (2026-08-19 – 2026-08-21)
- [v0.91.0 – v0.110.0](changelog-archive/v0.91.0-to-v0.110.0.md) (2026-08-17 – 2026-08-19)
- [v0.81.1 – v0.90.0](changelog-archive/v0.81.1-to-v0.90.0.md) (2026-08-15 – 2026-08-17)
- [v0.61.0 – v0.81.0](changelog-archive/v0.61.0-to-v0.81.0.md) (2026-08-14 – 2026-08-15)
- [v0.41.0 – v0.60.0](changelog-archive/v0.41.0-to-v0.60.0.md) (2026-08-11 – 2026-08-14)
- [v0.21.1 – v0.40.0](changelog-archive/v0.21.1-to-v0.40.0.md) (2026-08-09 – 2026-08-11)
- [v0.1.0 – v0.21.0](changelog-archive/v0.1.0-to-v0.21.0.md) (2026-08-08 – 2026-08-09)

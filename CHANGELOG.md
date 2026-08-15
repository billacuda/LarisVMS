# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

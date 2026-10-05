# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.212.0] - 2026-10-04

### Added

- Settings → Events: "Reset all object colors to defaults" button.
- Dashboard: a card per node with CPU, memory and network load, total recording fps, and (with object
  detection on) events and human/vehicle/animal counts for the last hour, 24 hours, 7 days and 30 days.
- Live and View tiles: drag a box to zoom into it, the same as Playback. While zoomed, a reset button
  shows in the tile's lower right, and right-clicking the video goes back to 1×.

### Changed

- Playback "Now" jumps to the end of the newest recorded footage instead of the current time, which
  usually had nothing playable yet.

- Live view: a moving object keeps its AI box and badge through pauses, until it has been still for
  the detection idle timeout, instead of blinking off whenever it stops.
- Vision Service: cameras running the same model variant can share ONNX Runtime sessions, up to
  `Vision:MaxCamerasPerSession` cameras per session (default 1, no sharing). Raising it trades
  per-frame inference latency for lower memory use.

### Fixed

- Playback drag-to-zoom: after zooming back out (or leaving fullscreen), dragging panned instead of
  selecting a new area. Tiles now keep a single zoom state, and panning stays within the frame.
- Live AI box labels running off the right edge of the video or overlapping each other.
- Zones editor: the motion wash on grid cells and polygon zones ran ahead of the live video. It is now
  held back to line up with the video, the same way live AI boxes are.
- Footage outliving its retention when it was outside a node's current storage folders (a storage
  path that changed, or a camera that moved to another node). The server now lists overdue footage
  to the node that owns it, wherever it is, and the record is removed only once the node confirms the
  file is gone. Nodes also remember unreported deletions across restarts, and storage-pressure cleanup
  removes leftover footage from moved cameras before an active camera's.
- Admin → Nodes: leftover-footage warnings show keep-forever and archive retention correctly, flag
  footage the node hasn't confirmed deleting, ignore a failover backup's own recordings, and can
  forget records left on a deleted node.
- Settings → Events: a color's Reset button now counts as an unsaved change.
- GPU-batched slice mode rejected for generic ONNX models whose outputs are in pixel units or
  sorted by score, over sub-pixel rounding differences. Its self-check now compares each tile's
  detections as a set, and falls back to per-tile inference whenever it can't prove each slot carries
  its own tile. A batched graph that fails is not rebuilt for other cameras on the same slice layout.
- Live view, playback, snapshots, exports and node control calls failing ("Invalid URI: The hostname
  could not be parsed") for nodes and media proxies that check in over IPv6: their address is now
  stored bracketed.
- Live AI boxes trailing moving objects: vision frames are stamped with their stream arrival time
  rather than when decoding finished, and the Web relay's own handling time is counted. The vision
  cadence log line now reports the decode delay this removes.
- Vision Service GC churn for generic ONNX models on the CPU-per-tile slice path: each tile is now
  preprocessed straight from the frame instead of through two new large buffers per tile per frame.
- Fewer vision frame drops: the Vision Service no longer copies model outputs every frame. Frames
  published while the detection engine is still building no longer count as drops. The cadence log
  line adds GC pause time and memory load.
- Live tiles: object badges no longer cover the Motion badge. Motion comes first, the object badges
  follow it on the same row, and they wrap only when they would run off the tile.

## [0.211.0] - 2026-10-02

### Added

- Installable as an app on phones: web app manifest (named after Branding), app icons, favicon and a
  minimal service worker (no caching).

### Changed

- Phone layout: the sidebar is an overlay drawer below desktop width instead of squeezing the page,
  shell padding is tighter, the topbar username is hidden on small screens, page headers and toolbars
  wrap, and long tab rows scroll sideways.

## [0.210.0] - 2026-10-02

### Added

- MSI installers for the web server, recorder node and media proxy (`build-installers.ps1`). They take
  the same settings as the install scripts on the command line, prompt for anything missing, remember
  settings for upgrades, and take over script-installed machines in place.

### Changed

- The web server applies database migrations itself on startup; upgrades no longer need the repo.
- The web server is published self-contained, so no ASP.NET Core runtime is needed.
- The web server registers the node and proxy builds bundled with it as Pending on startup, replacing
  install-web.ps1's direct database writes.
- Setup wizard: the database step explains that SQL Server Express needs `.\SQLEXPRESS` as the server
  name, both as a hint and in the error when the server can't be found.

### Fixed

- Fresh installs: the setup wizard failed with "too many redirects", then an error on every page,
  because settings and Entra sign-in were read from a database that didn't exist yet.
- Fresh installs: creating the admin account failed with "Role SUPER ADMIN does not exist", and left a
  half-created account that locked the wizard. Roles are now seeded before the admin is created, and
  a failed role assignment removes the account.
- Live view stayed on "Reconnecting" for a node on the same machine as the web server that connects
  through `localhost` (IPv6 `::1`).
- Live AI boxes lagged moving objects by ~700 ms: detection frames were decoded frame-threaded
  (~600 ms) and rate-limited with a look-ahead filter (~80 ms), and each frame was timestamped after
  both. Decoding is now low-latency and the rate limit keeps frames as they arrive.
- Live AI boxes moved in steps two or three times a second instead of smoothly; they now follow
  objects at the display's frame rate.
- Choosing DirectML as a node's AI accelerator turned AI detection off on NVIDIA and Intel GPUs.
- In-progress motion in Grid mode was rejected by the server and only appeared on the timeline once it
  ended.

## [0.209.0] - 2026-10-01

### Added

- Help section (left nav, below Settings): documentation for every part of the system, split into
  topics, with "Learn more" links from settings pages.
- Node edit page (Settings → Nodes → a node). All node settings, grouped into sections, replace the
  inline table editing.

### Changed

- UI cleanup. Settings, node and camera pages use grouped sections with a one-line hint per field
  instead of ℹ️ popovers; long explanations moved to Help. Fields that don't apply to the current
  choice are hidden, Save stays visible at the bottom, and leaving with unsaved changes asks first.
- Nodes list shows only the key columns; node actions moved to a ⋯ menu that floats over the table.
- Camera edit page grouped into General, Connection, Recording, Storage, AI detection and Motion;
  capabilities and streams are under a collapsible Device panel.
- Snapshots: the play button sits on the camera name/time row instead of its own footer.
- Max detection frame rate defaults to 7 fps (was 10) on new installs; existing installs keep 10.
- Settings tiles are alphabetical; added a Camera settings tile and renamed the duplicate-sounding
  ones (Logging, Node defaults, Recording defaults, Security).

### Fixed

- Dashboard's Nodes online only counted nodes that had cameras the user could see. It now counts
  every registered node.
- Settings → Email showed none of the provider fields (SMTP, Graph, Gmail).
- Blazor pages could keep using stale CSS/JS after an update; asset URLs are now versioned.
- Capability badges on the camera page ignored dark mode.

## [0.208.0] - 2026-09-24

### Fixed

- AI detection stayed dead until a manual node restart after a GPU driver reset. The vision process
  kept running with every camera's inference failing. It now exits after 60s with no successful
  inference on any camera while frames keep arriving, and the node restarts it.
- A camera whose inference failed on every frame stopped logging its detection cadence line, so
  the failure count that line is meant to carry never appeared. It now logs every 30s regardless.

## [0.207.0] - 2026-09-22

### Fixed

- Live view could freeze for tens of seconds and reconnect every tile at once. Caused by node-side
  thread-pool starvation; mitigated by raising the pool's minimum thread count.
- AI detection boxes led or lagged the object they marked. Boxes are now scheduled against a
  measured video latency instead of a fixed delay, and interpolated between detection ticks so
  motion is smooth instead of stepped.
- A stalled live tile could roll back several minutes of buffer and force a full reconnect.
  Old buffered video is now trimmed as playback advances.
- Saves on Admin → Settings → Detection and Admin → Settings → Email silently did nothing after
  the Blazor rewrite of those pages — the page reloaded stored values over the submitted ones
  before the save ran. Fixed; audited the rest of that migration and found no other pages affected.

### Added

- Live-view health telemetry (stream stalls, catch-ups, decode throughput, resyncs, box alignment)
  logged server-side instead of only to the browser console.
- A node process health heartbeat that flags when it stalls and whether GC or thread starvation is
  the cause.
- Live view now recovers automatically from an unexpected playback pause instead of waiting to
  drift out of sync.

## [0.206.0] - 2026-09-18

### Changed

- Migrated the remaining multi-handler-form pages (Cameras/Index, Cameras/Groups, Logs/AuditLogs,
  Admin/Settings/Email, Admin/Settings/Detection, Admin/Nodes, Permissions) from Razor Pages to
  Blazor.

### Fixed

- Snapshots' and Audit Logs' pagination links could land on the Dashboard instead of the current
  page.
- Snapshots' filter checkboxes stopped surviving a refresh or new login after the move to Blazor's
  client-side navigation.
- Blazor forms across the site returned errors after the migration: duplicate antiforgery tokens,
  forms sharing one name across a list of rows, and dropdowns resetting after save. All fixed.

## [0.205.0] - 2026-09-15

### Added

- New Settings hub (`Admin → Settings`) replacing the old Admin nav dropdown and top-level buttons.

### Changed

- Reworked the web UI to a sidebar + topbar shell with new colors/typography over Bootstrap.

### Fixed

- Several dark-mode/theme bugs: sidebar accent color override, primary-button hover direction,
  missing RGB theme variables, capability badges, Setup wizard dark mode, and fullscreen kiosk mode
  targeting the old navbar.

## [0.204.0] - 2026-09-15

### Changed

- Web no longer requires IIS — self-hosts Kestrel directly as its own Windows Service, the same way
  the recorder node does. New `install-web.ps1` install/upgrade path.

### Fixed

- Several issues surfaced by the IIS → Kestrel migration: IPv4-mapped-IPv6 addresses breaking media
  proxying, HTTP/2 WebSocket multiplexing breaking the live detection overlay, a missing example
  config file in published builds, and `install-node.ps1` always touching the firewall rule.

## [0.203.0] - 2026-09-14

### Fixed

- Slice-mode detection regressions from the previous release: objects at the true image edge could
  vanish, and a fast detection path could mislabel position. Also fixed a pre-existing bug where a
  clipped object could show a stray duplicate box.

## [0.202.0] - 2026-09-14

### Fixed

- Better ONNX model metadata detection. Custom models now work in Slice mode. Fixed duplicate/split
  detections when two tiles disagreed on an object's class.

### Added

- Custom ONNX models can use a faster single-pass detection mode when the model supports it, with
  automatic fallback to the safe per-tile path.

## [0.201.0] - 2026-09-13

### Changed

- Relicensed from MIT to Apache-2.0, matching sibling project SideGlance's own relicensing, so code
  and patterns can be shared between the two freely.

### Added

- Dashboard shows a spinner while a camera's AI detection engine is still cold-building.
- Settings changes now reach a running camera pipeline within seconds instead of up to 30.
- AI backend dropdown gains TensorRT and OpenVINO options, fixing a bug where Intel and AMD both
  silently mapped to DirectML.
- D-FINE FP16 via TensorRT, with automatic FP32 fallback if a camera's engine overflows.
- New "Custom" model family runs any ONNX model dropped into the node's models folder.
- Models are no longer bundled into the node build; drop them into the models folder instead.
- The external HTTP inference backend can send raw pixels instead of JPEG for lower overhead, and
  reports a per-stage timing breakdown.

### Changed

- External inference traffic now uses its own connection pool, separate from node report traffic.
- The external backend sends raw JPEG bytes instead of base64-encoded JSON.
- Detection frame-rate cap (`Detection.MaxFps`) is now per-camera, not just per-node.

### Fixed

- The external inference model picklist ignored the real input size due to a JSON casing mismatch,
  and its health probe always failed.
- Snapshot crop margin was too tight in Slice mode with the external backend.

## [0.200.0] - 2026-09-11

### Fixed

- A per-node settings override could be silently lost when saving from a stale page — saving now
  checks the node hasn't changed underneath since the page loaded, and rejects the save instead of
  overwriting.
- Admin → Settings → AI Detection (and Cameras → Edit) could silently fail to save after clicking
  Test Connection/Re-probe, since Save reused that handler instead of its own.

## [0.199.0] - 2026-09-09

### Changed

- Snapshots now shows detections only, not plain motion.

### Fixed

- An AI detection could be missing from Snapshots if the retired Motion toggle had been unchecked.

## [0.198.0] - 2026-09-09

### Fixed

- Live view stuttered and repeatedly caught up compared to smooth recorded playback. The live
  stream flushed a fragment only per keyframe (bursty); it now flushes every ~500ms. The client also
  chased the live edge too aggressively; it now holds a small jitter buffer with gentle rate
  correction instead.
- A slow viewer's connection could corrupt its own stream and force a jarring reconnect. The node
  now disconnects that viewer cleanly instead.

## [0.197.0] - 2026-09-09

### Fixed

- A fast-moving object could show no live detection box unless "Idle" was also enabled — a
  movement-tracking gap on a briefly missed frame (motion blur, occlusion) wiped its history and
  reset it to Idle. Track state now survives ByteTrack's coast-through-miss window.

## [0.196.0] - 2026-09-08

### Fixed

- The storage watermark backstop deleted archive-enabled footage even when the archive volume was
  healthy, and gave up too easily on transient SMB/USB blips. Retries added; the watermark pass now
  moves footage to archive when possible, falling back to delete only when necessary.

## [0.195.0] - 2026-09-08

### Changed

- Detection-box jitter rejection is now its own opt-in, per-camera, tunable setting
  (`Detection.RejectMotionJitter`), off by default — it helped some cameras but made a noisier
  model misclassify parked vehicles as moving all night. Snapshot finalization grace period is now
  its own setting too (`Detection.DepartureGraceSeconds`).

### Fixed

- Motion-span reporting could log duplicate-key errors when a batch reported the same span twice.
- A recorder node's primary drive could fill to 100% when its archive volume was unreachable — the
  watermark pass now deletes to free space in that case (superseded by 0.196.0, which restores
  archiving once the volume is healthy again).

## [0.194.0] - 2026-09-08

### Added

- Recording failover — assign a recorder node a backup node; if it goes down, its cameras move to
  the backup for recording and live view until it recovers. The failover decision uses a
  health-check quorum to avoid flapping on a flaky link. New maintenance mode and "recording only"
  (AI off) per-node switches.

## [0.193.0] - 2026-09-07

### Added

- Media proxies now auto-update, the same way recorder nodes do.

### Fixed

- Enabling archive storage on a node with a large existing footage backlog could stall; reports now
  go out in batches.

## [0.192.0] - 2026-09-07

### Added

- Media proxy tier — a standalone TLS-terminating relay between browsers and recorder nodes for
  live view and playback, useful when a proxy machine can hold a real certificate but the nodes
  can't. Nodes can be assigned a primary/backup proxy with automatic fallback to direct-to-node or
  through this server.

## [0.191.0] - 2026-09-06

### Added

- Direct-to-node streaming — live view and playback can connect a browser straight to the recorder
  node instead of relaying through this server. Off by default; falls back safely to the proxy path
  when a node isn't ready.

### Fixed

- Zooming the timeline in fullscreen also zoomed the video underneath it.

## [0.190.0] - 2026-09-06

### Added

- Recorder-node check-ins are now replay-hardened (rolling nonce). Bearer-secret rotation support
  (dormant by default). One-shot media tokens (playback, thumbnails, snapshot crops) are now
  refused on reuse.

## [0.189.0] - 2026-09-06

### Fixed

- Moving people/animals could be misclassified as idle by an overly strict directedness check added
  in 0.188.0; reverted to a simpler centroid-displacement check.
- The node registration key field on Admin → Settings → Nodes rendered blank instead of masked.

### Changed

- Hover thumbnails and AI snapshot images are now stored as WebP instead of JPEG.

## [0.188.0] - 2026-09-06

### Changed

- Storage configuration (recording/archive paths) is now per recorder node instead of one global
  setting.

### Added

- Archive storage tier — a node can move aging footage to a secondary volume instead of deleting
  it, with its own retention window; playback and thumbnails work transparently from either volume.
- Node build numbers, so a rebuild without a version bump still registers as newer for auto-update.
- Snapshot badges now show object counts (e.g. "Human ×2").

### Fixed

- A redundant "Person" entry appeared under "Human" in the Snapshots filter tree.
- A parked vehicle could intermittently register as moving.
- A departing object's snapshot could stay open long enough to merge with an unrelated later object
  of the same type.

## [0.187.2] - 2026-09-05

### Fixed

- Dragging the recording timeline flashed the video black between positions.

### Changed

- Camera queries that load groups/streams together now run as split queries for performance.

## [0.187.1] - 2026-09-05

### Fixed

- A snapshot with several tagged objects now crops around all of them instead of just the
  highest-confidence one.

### Changed

- AI detections of people are now labeled "Human" instead of "Human — person".

## [0.187.0] - 2026-09-05

### Added

- D-FINE can now run on TensorRT at FP32 (opt-in). FP16 support is wired but not yet selectable —
  no working mixed-precision model export exists yet.

### Changed

- D-FINE is now labeled "experimental"; YOLOX remains the recommended default.

## [0.186.3] - 2026-09-05

### Changed

- The vision log now identifies cameras by name instead of GUID.

## [0.186.2] - 2026-09-05

### Fixed

- On a node with TensorRT enabled, only the first camera in Slice mode worked — every other camera
  failed on every frame due to a TensorRT engine cache collision that didn't account for per-camera
  shape differences. Also fixed misleading "warm cache" logging and excessive per-frame error spam.

### Added

- The vision log now reports a Slice camera's resolved tile geometry and per-slice
  detection/merge counts.

## [0.186.1] - 2026-09-05

### Fixed

- Clicking Play on Playback before a camera's stream finished loading could leave it silently
  paused.

## [0.186.0] - 2026-09-05

### Added

- New "Slice" aspect-fitting option for AI detection — cuts a wide/tall camera's frame into
  overlapping tiles run at full resolution instead of letterboxing and downscaling, to catch small
  or distant objects. Can run entirely on GPU.

### Changed

- Slice mode forces GPU frame preprocessing on for that camera.

## [0.185.0] - 2026-09-05

### Changed

- Internal groundwork for batched AI inference — no behavior or performance change yet.

## [0.184.0] - 2026-09-05

### Changed

- AI-detection snapshots now keep upgrading to the clearest view of a moving object instead of
  freezing on its first sighting.

## [0.183.0] - 2026-09-05

### Removed

- High-resolution re-detection and high-resolution snapshots — neither worked reliably, and the
  former was the largest CPU cost measured on a busy node. Also removed the vision debug-image
  setting that existed only to diagnose it.

## [0.182.0] - 2026-09-04

### Fixed

- D-FINE on a TensorRT-enabled node produced no detections at all, with nothing in any log
  (FP16 overflow). D-FINE now runs on plain CUDA by default; TensorRT is opt-in per node. Also
  fixed misleading "cold cache" logging.

### Changed

- Camera row actions (Zones, Event tags, Schedule, Re-probe) are now emoji-only buttons with
  tooltips.

## [0.181.0] - 2026-09-04

### Added

- Snapshot badges now show each AI detection's confidence score.

### Fixed

- Snapshot badges and card borders now follow the Events settings colors instead of an internal
  auto-assigned color.

## [0.180.0] - 2026-09-03

### Added

- Live view can label AI detection boxes with their confidence score.

### Changed

- Live-view Moving/Idle detection controls are now switches, saved per account.

## [0.179.1] - 2026-09-03

### Fixed

- Enabling TensorRT could stop recording and live view entirely — engine builds ran on the
  recording pipeline's own thread and starved it. Builds now run off that path, one at a time.
  Also fixed overlapping start requests tearing down in-progress builds, and a too-generous
  timeout starving the live detection overlay.

### Changed

- TensorRT's GPU workspace is now bounded instead of unconstrained.

## [0.179.0] - 2026-09-03

### Added

- One recorder node package now runs on any hardware — picks CUDA, DirectML, or CPU at startup,
  and the CUDA provider library downloads on demand instead of always being bundled.

### Fixed

- An Intel-iGPU node built for CUDA failed AI detection outright.
- TensorRT was calling an API newer ONNX Runtime no longer accepts.

## [0.178.0] - 2026-09-02

### Changed

- Playback timeline bookmark markers now have an outline for legibility over colored backgrounds.

## [0.177.0] - 2026-09-02

### Added

- The Snapshots filter tree now remembers its collapsed/unchecked state per user.

## [0.176.0] - 2026-09-02

### Changed

- Snapshot cards are framed more prominently in their badge color(s), including a gradient border
  for cards spanning multiple detection types.

## [0.175.0] - 2026-09-02

### Fixed

- Lowering the AI detection confidence below 0.6 had no effect — a separate, fixed tracking
  threshold silently discarded anything under it regardless of the configured confidence.

### Added

- Detection cadence log line now reports what the model found vs. what survived tracking, to
  distinguish "no detection" from "detected but rejected."

## [0.174.2] - 2026-09-02

### Changed

- AI detection no longer allocates its model input buffer per frame, cutting a major source of GC
  pressure on a busy node.

## [0.174.1] - 2026-09-02

### Changed

- AI detection no longer allocates two large buffers per frame, cutting GC pressure further. The
  detection cadence line now also reports GC/allocation stats.

## [0.174.0] - 2026-09-02

### Added

- Per-camera AI detection orientation override, for cameras that advertise the wrong stream shape
  over ONVIF.
- Per-camera detection cadence log line (frames captured/processed/dropped, inference time).

### Fixed

- AI detection could restart a camera's watch in a loop after a routine re-probe overwrote a
  resolution correction.

## [0.173.1] - 2026-09-01

### Security

- Camera RTSP credentials were being written into node log files via ffmpeg's own output; now
  scrubbed. Rotate existing logs.

## [0.173.0] - 2026-09-01

### Added

- A medium D-FINE model option, alongside the existing small variants.

## [0.172.2] - 2026-09-01

### Changed

- Detection span reports are now processed one batch at a time per node, merging conflicts instead
  of dropping them on a race. Hardening only.

## [0.172.1] - 2026-09-01

### Fixed

- AI detection could silently stop producing spans/snapshots for a busy camera due to a
  unique-index violation in span coalescing.

## [0.172.0] - 2026-09-01

### Fixed

- Portrait/corridor-mounted cameras produced severely stretched AI-detection snapshot crops when
  their watch stream had never been resolution-probed.

## [0.171.0] - 2026-09-01

### Added

- Detection frame-rate cap setting.
- YOLOX is now the default detection engine (was D-FINE), with a per-node model-size picker.
- Playback "Now" button.
- A "catching up" badge for fast-forward/drift-correction.
- Snapshots pagination above the grid as well as below.
- Optional high-resolution snapshots.

### Fixed

- AI-detection snapshots are now cropped from the exact detected frame instead of a later
  timestamp seek, and are no longer taken a fraction of a second late.
- Overlapping detections on one camera now show as a single card.

### Changed

- Node registration key is now masked.
- Framework log noise is hidden by default.
- FFmpeg is no longer bundled with the node — install it separately.
- Single-camera playback hides the redundant merged timeline.

## [0.170.0] - 2026-08-31

### Changed

- High-resolution re-detection now runs its pixel work on the GPU instead of the CPU, cutting a
  major CPU cost on multi-camera nodes.

### Added

- Deployment-wide log level setting, applied without a restart.

## [0.169.1] - 2026-08-31

### Changed

- High-res re-detection's keyframe decode now runs on NVDEC where available, cutting CPU load
  further.

## [0.169.0] - 2026-08-30

### Added

- GPU frame preprocessing option, moving per-frame color conversion/normalization off the CPU.
  Vendor-neutral, off by default.

## [0.168.0] - 2026-08-30

### Added

- Vision debug images are now a toggle instead of always-on.

### Changed

- Snapshot images are cropped from the exact detection frame when high-res re-detection is
  enabled, with millisecond-precision fallback seeking.

### Fixed

- Duplicate Snapshots cards for a single object, caused by tracker-ID churn.
- Camera-native and LarisVMS AI detections of the same object no longer double up in Snapshots.

## [0.167.3] - 2026-08-30

### Fixed

- Grid mode's mask/size/sensitivity/active-mode choice never survived a page refresh — a stale
  field projection silently dropped them on read-back (the save itself always worked).

## [0.167.2] - 2026-08-30

### Added

- Drag-select for the Grid editor's cells instead of one click per cell.

## [0.167.1] - 2026-08-30

### Changed

- Merged the Grid and Polygon zone editors onto one Zones page.
- Grid cell edits now require an explicit Save.
- New cameras default to Grid mode.

## [0.167.0] - 2026-08-30

### Added

- Grid editor UI — live video with a click-to-mask cell grid, size selector, and sensitivity
  slider.

## [0.166.0] - 2026-08-30

### Added

- Backend for Grid-mode motion detection, an alternative to hand-drawn polygon zones and the
  primary tuning mechanism on nodes without a GPU. Editor UI ships in a later release.

## [0.165.1] - 2026-08-30

### Fixed

- Zone Kind serialized as a bare integer instead of its name over the API, breaking zone colors
  and the edit form's Kind selector.
- Live per-zone motion wash is now fully transparent at rest instead of a continuous fade, so it's
  obvious when the feed isn't reaching the browser.

## [0.165.0] - 2026-08-30

### Added

- The Zones editor now shows real live video with each Motion zone washed by its own live motion
  score, instead of a static snapshot with fixed-opacity polygons.

## [0.164.0] - 2026-08-30

### Added

- High-resolution re-detection's result now actually feeds the snapshot a viewer sees, instead of
  only being logged.

## [0.163.4] - 2026-08-30

### Fixed

- Nothing bounded how many high-res re-detection triggers could run at once across cameras,
  risking CPU pegging and duplicate/fragmented snapshots. Now limited to one at a time
  process-wide.

## [0.163.3] - 2026-08-30

### Fixed

- High-res re-detection's debug image dump silently failed to write anything, with no indication
  why (logged below the file logger's minimum level).

## [0.163.2] - 2026-08-30

### Fixed

- The node process could crash entirely after an ordinary HTTPS timeout talking to the web tier —
  a cancellation-exception check incorrectly treated a timeout the same as a real shutdown.

### Added

- Temporary diagnostic dump of high-res re-detection's decoded/cropped frames as JPEGs, to
  visually confirm box alignment.

## [0.163.1] - 2026-08-30

### Fixed

- Vision Service never had its own log file — its console output was captured at the wrong log
  level and silently dropped before reaching disk.

## [0.163.0] - 2026-08-30

### Added

- Motion-guided native-scale re-detection — an opt-in pass that re-runs detection at full
  resolution on a newly-moving object's first frame, for better accuracy than the continuous
  lower-resolution pass.

## [0.162.0] - 2026-08-30

### Added

- Main-stream fragment ring buffer — eager in-memory capture from the high-res stream at detection
  time, replacing lazy after-the-fact segment seeking. Buffers only in this release; nothing
  consumes it yet.

## [0.161.6] - 2026-08-30

### Fixed

- A few remaining Snapshots cards still 502'd — a segment-duration estimate could be slightly
  inflated, landing past the file's real content. Now retries at offset 0 on failure, same as the
  thumbnail path already did.

## [0.161.5] - 2026-08-30

### Fixed

- "No thumbnail available" on some Snapshots cards was concurrency, not data — the capture gate
  allowed only 2 concurrent ffmpeg captures with a 3s timeout, inherited from an unrelated
  hover-preview feature, so most of a 24-card page burst timed out. Raised to 6 concurrent / 30s
  for the Snapshots case.

## [0.161.4] - 2026-08-30

### Added

- Diagnostic logging for "No thumbnail available" cards that don't self-heal — the failure routes
  previously returned a bare error with no explanation.

## [0.161.3] - 2026-08-30

### Fixed

- The Snapshots page still timed out after 0.161.2's index — the real cause was an interval-overlap
  query no index can fully seek. The coverage check now runs after pagination, over only the rows
  being rendered.

## [0.161.2] - 2026-08-30

### Fixed

- Attempted fix for Snapshots page timeouts (added an index) — did not fix it; see 0.161.3 for the
  actual cause. Index kept since it helps the real fix.

## [0.161.1] - 2026-08-30

### Fixed

- The Snapshots page threw an unhandled exception on every load due to a query EF Core couldn't
  translate to SQL; rewritten to a simpler shape (still timed out under load — fixed in 0.161.2).
- Many plain-motion Snapshots cards showed "No thumbnail available" even after the page loaded, due
  to an incomplete footage-coverage check.

## [0.161.0] - 2026-08-29

### Fixed

- One moving object could produce several snapshots when the detection model's per-frame class
  guess flickered; detections are now arbitrated to one stable label per tracked object.
- Clicking a Snapshots card jumped straight to the detection instant with no lead-in; now starts
  from the camera's configured pre-roll.
- A motion-span row could live forever after its footage aged out, leaving a permanent broken
  placeholder card; now cleaned up on a retention sweep.

## [0.160.0] - 2026-08-29

### Changed

- AI detection now fits each camera's own real aspect ratio into the model's square input instead
  of stretching every camera to one fixed global resolution — new Letterbox (default) and Stretch
  modes. Live-detection overlay boxes now line up correctly on non-16:9 cameras.

## [0.159.0] - 2026-08-29

### Changed

- Server-side motion detection now runs on GPU decode where available, cutting a major CPU cost.
  New per-camera toggle to disable it entirely on cameras that don't need the fallback.

## [0.158.0] - 2026-08-26

### Fixed

- Vision Service CPU usage went up, not down, after switching to D-FINE, due to unnecessary
  per-frame math.
- AI-detection snapshot crops could miss the detected object entirely once D-FINE's tighter boxes
  shipped; crop margin increased.
- Login was broken site-wide (password field showed as plain text, every sign-in failed) due to a
  missing Razor view-imports file in the Identity area.
- AI-detection snapshots could fragment a single sighting into several due to too-strict span
  closing; added a grace period.
- Best-frame snapshot cropping could pick the wrong object when multiple detections shared a label.
- Snapshot emoji was generic regardless of category.
- AI-detection boxes didn't track zoom/pan on a fullscreened live tile.
- Live-view tiles never showed a badge for AI-detected objects.
- Clicking a snapshot deep link could land on a paused tile needing a manual Pause/Play click to
  actually start.

### Changed

- Replaced YOLOv9 with D-FINE as the default AI detection model (licensing).
- Detection confidence/IoU thresholds and which stream feeds detection are now real, editable
  settings.
- Unified the "person detected" vocabulary between camera-native and AI detections.

### Added

- AI detection settings tab with a global-default + per-camera-override pattern.
- Camera list shows which cameras have AI detection enabled and which stream.
- Snapshots filter is now a collapsible tree instead of a flat checkbox row.

## [0.157.1] - 2026-08-25

### Fixed

- PowerShell strict-mode bugs in `build-node.ps1`/`install-node.ps1` when exactly one file/process
  matched a query.
- The live-view AI-detection box overlay toggle (shipped earlier) was never actually wired up in
  the UI; added.
- Vision Service couldn't find ffmpeg — its path was never passed through to the child process.
- `build-node.ps1` never bundled an exported model — a non-recursive search missed it.
- False gaps in recording coverage: Playback reported "no recording available" for windows that
  were actually covered, due to a segment-boundary timestamp bug. Retroactively repaired affected
  segments.
- Live-view detection boxes never drew despite real data streaming, due to a JSON casing mismatch.
- AI detection could never find its model on any node, due to a hardcoded wrong default filename.
- Stopping the node service left the Vision Service process running, risking a failed in-place
  upgrade.
- `install-node.ps1` now checks and can fix missing CUDA/cuDNN dependencies at install time.
- A Vision Service startup failure was undiagnosable from the node's own log; now surfaced.
- Camera edit fields for AI detection/motion source always reset to defaults on reload despite the
  save working correctly (a read-side projection gap).

## [0.157.0] - 2026-08-24

### Added

- Native AI object detection — a real-time YOLO/ByteTrack pipeline running per node, independent of
  onboard camera analytics. Runs as a separate sibling process so a bad GPU/driver interaction can't
  affect recording. Hardware accelerator is a per-node, auto-detected setting. Detected classes
  bucket into a small color-coded category set. Live-view bounding box overlay via its own
  WebSocket. Motion-mode recording gains one designated primary source when multiple signals are
  configured. AI-detected objects get their own cropped snapshot images. Recorder-node auto-update
  now also keeps the Vision Service binary current. Unverified against real GPU hardware or an
  actual camera end-to-end at ship time.

## [0.156.1] - 2026-08-23

### Fixed

- Every page load threw an exception once Entra SSO shipped, due to a DI registration bug and a
  validation edge case in an unconfigured deployment.

## [0.156.0] - 2026-08-23

### Fixed

- Some snapshots stalled forever on Play, looping the same recovery attempt; now gives up
  gracefully after repeated failures instead of looping indefinitely.

### Added

- Snapshots search can now filter by camera group or saved view, not just single camera/all.
- Playback timeline is taller and auto-hides on mobile.

## [0.155.0] - 2026-08-23

### Fixed

- Changing an already-recording camera's ONVIF device URI never took effect — it kept recording
  from the old source until deleted and re-added.

## [0.154.0] - 2026-08-23

### Added

- "Sign in with Microsoft" (Entra ID) — sign-in only, matched against an existing
  admin-provisioned account; no self-registration. New Security settings section; takes effect
  without a restart.

## [0.153.0] - 2026-08-23

### Changed

- Field-level help text now collapses behind an info icon instead of always showing, across most
  admin/settings pages.
- `Cameras/Edit` no longer edits group membership directly — links to the Groups page instead.

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

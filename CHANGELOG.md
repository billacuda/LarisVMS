# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
  depends on manually copying the `publish\NidusVMS.Node\win` folder there each time.

### Changed

- `NidusVMS.Node`'s HTTP client can now skip TLS certificate validation via the existing
  `--insecure-tls` flag / `NIDUSVMS_INSECURE_TLS` env var without a code change — this was already
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
  node (`NVR1`): `NidusVMS.Web`'s proxy could open a TCP connection to the port, but every WebSocket
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
    reachable only by NidusVMS.Web's proxy, never by a browser directly. This is a deliberate
    architecture decision, not a shortcut: direct browser-to-node `wss://` would mean every node
    needs its own TLS certificate (self-signed and asking each viewer to trust it, or a real one via
    an internal CA) before video plays at all; proxying through IIS needs nothing installed on a
    node. Resolves the "Node TLS strategy" item the plan had left open since M1.
  - A 60-second HMAC token (`MediaToken`, unit tested), signed with a per-node key generated at
    registration (`Node.MediaSigningKey`, encrypted at rest), is what keeps a node's live port from
    being wide open to anything else on the LAN that knows the URL shape — validated locally by the
    node, no DB round trip.
  - `NidusVMS.Web` gained `GET /live/{cameraId}` (`Cameras.View`-gated): accepts the browser's
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

- `install-node.ps1` stopping the Windows Service only waits for `NidusVMS.Node.exe` itself to exit —
  Windows doesn't kill child processes when their parent dies, so if `NidusVMS.Node`'s own graceful
  shutdown doesn't finish killing each `ffmpeg.exe` it spawned before the SCM's stop timeout hits,
  those are left running, orphaned, and still holding their DLLs open. Confirmed on a real node: this
  made re-running the script to upgrade an already-running node fail with "the process cannot access
  the file... being used by another process" while copying ffmpeg. The script now unconditionally
  stops any `ffmpeg.exe` still running after the service reports Stopped, plus retries the copy
  itself a few times as a second line of defense against the same handle-release race. (An earlier
  version of this fix tried to filter to only ffmpeg processes under the node's own install
  directory by checking each process's `.Path` — that filter silently matched nothing, since
  querying `.Path` on a process running as a different account, LocalSystem by default here, can
  fail even from an elevated session. Simpler and correct: ffmpeg is only ever run by NidusVMS.Node on
  this machine, so there's nothing to filter for.)
- Nodes kept reporting a stale version in `Admin → Nodes` no matter how many times they were
  upgraded. Two compounding bugs: `NodeHeartbeatRequest.Version` was sent on every heartbeat but the
  heartbeat endpoint never read it, so `Node.Version` only ever got set once, at first-ever
  registration, and never again — confirmed live: two real nodes stuck showing `0.3.0` despite
  running an already-upgraded `0.4.0` binary. Separately, the node's own reported version was a hand
  maintained string literal (`"0.4.0"`) rather than read from the build, which is exactly what let it
  drift two releases behind in the first place. Fixed both: the heartbeat handler now persists
  `Version` alongside the disk-usage stats it already recorded, and the node reads its version from
  its own assembly metadata (`NidusVMS.Node.csproj`'s `<Version>`) via a new `NodeVersion.Current`
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
  It's purely filesystem-driven — `NidusVMS.Node` has no DB connection by design — and reports back
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
  trusted — `NidusVMS.Node` now has `InternalsVisibleTo` for `NidusVMS.Tests` for exactly this.

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
  `NidusVMS.Node` as a self-contained single-file win-x64 executable (Windows only — the node's
  registration store is DPAPI-based and throws on Linux; that support isn't implemented yet, so
  publishing a linux-x64 build would just fail at first run) and bundles `install-node.ps1`
  alongside it, mirroring `dploid`'s `build-agent.ps1`/`install-agent.ps1` shape. `install-node.ps1`
  installs to `C:\Program Files\NidusVMS\Node`, registers a Windows Service with the registration
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
  `C:\Program Files\NidusVMS\Node\ffmpeg\` before the service is registered, regardless of where it was
  originally found, so the service account's access to it no longer depends on where installation
  happened to leave it.

## [0.3.0] - 2026-08-08

### Added

- 24/7 recording engine (`NidusVMS.Node`, `NidusVMS.Media`). A recorder node is a separate Windows
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
- Node control plane (`NidusVMS.Web` `/api/nodes/*`). Register/heartbeat/config/segment-report
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

- Hand-rolled ONVIF SOAP client (`NidusVMS.Onvif`). The plan originally called for
  `dotnet-svcutil`-generated clients from vendored WSDLs, but ONVIF's WSDL/XSD tree is notorious for
  breaking that generator (circular schema imports), so instead this is plain XML request/response
  templates over `HttpClient` for the ~10 operations NidusVMS actually needs today
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

- Solution skeleton: `NidusVMS.slnx` with the seven-project layering from the plan (`Core`, `Onvif`,
  `Media`, `Infrastructure`, `Web`, `Node`, `NodeUpdater`) plus `tests/NidusVMS.Tests`, all targeting
  `net10.0` with `Directory.Packages.props` for central package management from day one. `NidusVMS.Onvif`
  and `NidusVMS.Media` were placeholder projects until this release; `NidusVMS.Node` and
  `NidusVMS.NodeUpdater` still print a not-yet-implemented message pending milestone M3.
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
  under `%ProgramData%\NidusVMS\keys`, DPAPI-wrapped on Windows.
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
  `try { migrate + robocopy /MIR } finally { start pool }`), with three NidusVMS-specific additions: a
  storage-root guard that throws before deploying if the configured storage root resolves under the
  IIS site directory (a `/MIR` there would delete every recording), `/XD` exclusions for
  `recordings`, `spool`, and `exports` as a second line of defense, and a post-deploy `/health`
  probe. `install-node.ps1` is a stub until M3.

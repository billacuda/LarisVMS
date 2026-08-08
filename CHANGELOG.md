# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-08-08

### Added

- **M4 — Storage manager.** Every node now runs a `StorageManager` background service that
  enforces, in order: age-based retention (per camera, resolved through `ISettingsResolver`),
  per-camera storage quota (`Camera.QuotaBytes`, oldest segments evicted first once over the cap),
  and a global watermark backstop (delete the oldest segments across every camera on the node,
  regardless of retention/quota settings, once the storage volume passes a configurable % used —
  the hard "the disk is nearly full" fallback the plan calls out as independent of retention days).
  It's purely filesystem-driven — `Rcordr.Node` has no DB connection by design — and reports back
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
  trusted — `Rcordr.Node` now has `InternalsVisibleTo` for `Rcordr.Tests` for exactly this.

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
  `Rcordr.Node` as a self-contained single-file win-x64 executable (Windows only — the node's
  registration store is DPAPI-based and throws on Linux; that support isn't implemented yet, so
  publishing a linux-x64 build would just fail at first run) and bundles `install-node.ps1`
  alongside it, mirroring `dploid`'s `build-agent.ps1`/`install-agent.ps1` shape. `install-node.ps1`
  installs to `C:\Program Files\Rcordr\Node`, registers a Windows Service with the registration
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
  `C:\Program Files\Rcordr\Node\ffmpeg\` before the service is registered, regardless of where it was
  originally found, so the service account's access to it no longer depends on where installation
  happened to leave it.

## [0.3.0] - 2026-08-08

### Added

- 24/7 recording engine (`Rcordr.Node`, `Rcordr.Media`). A recorder node is a separate Windows
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
- Node control plane (`Rcordr.Web` `/api/nodes/*`). Register/heartbeat/config/segment-report
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

- Hand-rolled ONVIF SOAP client (`Rcordr.Onvif`). The plan originally called for
  `dotnet-svcutil`-generated clients from vendored WSDLs, but ONVIF's WSDL/XSD tree is notorious for
  breaking that generator (circular schema imports), so instead this is plain XML request/response
  templates over `HttpClient` for the ~10 operations Rcordr actually needs today
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

- Solution skeleton: `Rcordr.slnx` with the seven-project layering from the plan (`Core`, `Onvif`,
  `Media`, `Infrastructure`, `Web`, `Node`, `NodeUpdater`) plus `tests/Rcordr.Tests`, all targeting
  `net10.0` with `Directory.Packages.props` for central package management from day one. `Rcordr.Onvif`
  and `Rcordr.Media` were placeholder projects until this release; `Rcordr.Node` and
  `Rcordr.NodeUpdater` still print a not-yet-implemented message pending milestone M3.
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
  under `%ProgramData%\Rcordr\keys`, DPAPI-wrapped on Windows.
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
  `try { migrate + robocopy /MIR } finally { start pool }`), with three Rcordr-specific additions: a
  storage-root guard that throws before deploying if the configured storage root resolves under the
  IIS site directory (a `/MIR` there would delete every recording), `/XD` exclusions for
  `recordings`, `spool`, and `exports` as a second line of defense, and a post-deploy `/health`
  probe. `install-node.ps1` is a stub until M3.

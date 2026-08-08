# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

# Fresh-install issues found while building the demo site (2026-10-02)

Found by running LarisVMS Web from source (`main` at 24a8e89, version 0.209.0) against an empty
SQL Server Express on a machine that has never had LarisVMS installed. The setup-wizard code involved
is unchanged since v0.209.0, so **the released MSI very likely has issues 1–4 and 7–11**. That hasn't been
confirmed. To confirm, install `LarisVMS-Web-0.209.0-x64.msi` on a clean VM and open the wizard.

Fixes for 1–11 are in the working tree (6 still needs the Models folder committed), **uncommitted**, and haven't been through the unit tests
(the test project doesn't build on a fresh clone; see issue 6). Each should go into a patch release
and be checked on a clean VM with the MSI.

## 1. Setup wizard: ERR_TOO_MANY_REDIRECTS (blocker)

**Symptom:** the browser shows "too many redirects" on first visit; the wizard never appears.

**Cause:** `PortSegmentationMiddleware` and `IpAllowListMiddleware` read settings from the database
on every request, including the wizard's own `/Setup` pages. Before setup there's no connection
string, so EF throws "No database provider has been configured". `UseExceptionHandler("/Error")`
re-executes as `/Error`, which isn't exempt in `SetupMiddleware`, so it's redirected to `/Setup`,
which throws again, and so on.

**Fix (uncommitted):**
- `SetupMiddleware` marks pre-setup requests to exempt paths (`IsSetupPending`), and `/error` is now exempt.
- `PortSegmentationMiddleware` and `IpAllowListMiddleware` skip their checks while setup is pending.

## 2. Setup wizard: HTTP 500 on every page (blocker, hidden behind #1)

**Cause:** `EntraOidcOptionsConfigurator.Configure` queries `EntraSsoSettings` on every request
(AuthenticationMiddleware validates every OIDC scheme's options), which throws before there's a database.

**Fix (uncommitted):** `ReadSettings` returns null (treated as "Entra not configured") when there's
no connection string yet, or when the tables don't exist yet (`SqlException`).

## 3. Admin step: "Role SUPER ADMIN does not exist" (blocker)

**Cause:** `RoleSeedService` and `CameraGroupSeedService` only run at startup, and only when a
database is already configured. On a fresh install the wizard's Database step creates the database
after startup, so the roles are never seeded before `CompleteSetupAsync` adds the admin to "Super
Admin". The comment in `CompleteSetupAsync` assumes otherwise.

**Fix (uncommitted):** `Pages/Setup/Admin.cshtml.cs` runs both seeders (both idempotent) before
`CompleteSetupAsync`.

**Related, not fixed:** the same startup block also runs migrations and
`BundledBuildRegistrar.RegisterAsync`. Migrations are covered by the wizard, but on a fresh MSI
install the bundled node/proxy builds won't show under Settings → Node builds until the service
restarts once. Consider running the registrar from the wizard too, or at the end of setup.

## 4. Admin step: a failed role assignment leaves a half-created admin

**Cause:** `CompleteSetupAsync` creates the user, then `AddToRoleAsync` throws. The user stays, and
because `IsSetupCompleteAsync` returns true when any user exists, the wizard is then skipped, leaving
an admin with no role.

**Fix (uncommitted):** if assigning the role fails, the new user is deleted before the error is shown.

## 5. Database step: default server "." doesn't fit SQL Server Express

The server field defaults to `.` (the default instance). SQL Server Express installs as the named
instance `SQLEXPRESS`, and TCP and SQL Browser are off by default, so `.`, `localhost` and IP
addresses all fail with "error: 26 - Error Locating Server/Instance Specified". Only
`.\SQLEXPRESS` (or `<hostname>\SQLEXPRESS`) works. Suggestions:
- Default or placeholder `.\SQLEXPRESS`, and/or a hint under the field.
- On error 26, add "If you installed SQL Server Express, use .\SQLEXPRESS".

**Fix (uncommitted):** a hint under the field, and the error message adds "use <name>\SQLEXPRESS"
when the connection fails to find the server and the name has no instance part.

## 6. `.gitignore` hides `src/LarisVMS.Vision/Models/`

The root rule `models/` (for downloaded `.onnx` files) also matches the source folder
`src/LarisVMS.Vision/Models/`, because Git on Windows ignores case. That folder isn't in the repo,
so a fresh clone can't build `LarisVMS.Vision`, the Vision Service, `build-node.ps1`,
`build-installers.ps1` or the test project.

**Fix (uncommitted):** the rule is now `/models/` (repo root only). **Still to do:** commit
`src/LarisVMS.Vision/Models/` from the machine that has it. Afterwards, verify with a fresh clone + `dotnet build` + `.\build-installers.ps1`.

## 7. Live view stuck on "Reconnecting" when the node reaches the server over IPv6 loopback

**Symptom:** cameras record fine, but every live tile says "Reconnecting". The web log shows
`UriFormatException: Invalid URI: The hostname could not be parsed` at `Program.cs` (the `/live` proxy).

**Cause:** a node on the same machine as the web server, using `--server-url https://localhost:8444`,
connects over `::1`. `NodeAuthMiddleware` stores that as `Node.LastIpAddress`, and every
node-facing URL is built as `ws://{ip}:{port}/...` or `http://{ip}:{port}/...`. Unbracketed IPv6
isn't a valid host there. This affects live, detections overlay, motion zones, snapshots, exports,
node control calls and failover probes. It's likely on single-box installs where the node is
pointed at `localhost`.

**Fix (uncommitted):** `IpAllowListPolicy.Unmap` stores `::1` as `127.0.0.1`, with a new unit test.

**Not fixed:** any other IPv6 node address still breaks those URLs. Proper fix: bracket IPv6 hosts
when building URLs (one helper used at every call site), or build them with `UriBuilder`.

## 8. Grid-mode motion checkpoints are rejected (in-progress motion missing from the timeline)

**Symptom:** web log `DbUpdateException ... FK_MotionSpans_Zones_ZoneId` every few cycles while
any camera has motion.

**Cause:** `NodeWorker.EnqueueMotionCheckpoints` reports Grid mode's internal sentinel zone
(`GridRegionZoneId = Guid.Empty`) as-is. The completed-span handler rewrites it to null, but the
checkpoint path doesn't, so the server drops every in-progress Grid-mode span on the FK. Grid is the
default motion mode, so this affects most installs. Spans still appear once motion ends.

**Fix (uncommitted, node change):** the checkpoint loop rewrites the sentinel to null as well.

## 9. Choosing "DirectML" on a node without an AMD GPU disables AI detection

**Symptom:** with the node's AI accelerator set to "DirectML" (Settings → Nodes → node), the Vision
Service never starts on an NVIDIA or Intel machine, and no camera gets detections. Nothing explains why.

**Cause:** the "DirectML" option is stored as `AiAccelerator.Amd`. `AccelSelection.Choose` honours
an explicit choice only when that vendor's hardware is detected, so on a non-AMD GPU it resolves to
null, which means no Vision Service. The enum's own docs say Amd means "DirectML, works on any DX12 GPU".

**Fix (uncommitted, node change):** `Amd` resolves whenever any GPU (NVIDIA, Intel or AMD) is
detected, with new unit tests. Also consider logging a warning (and showing it on the node page)
whenever the chosen accelerator resolves to nothing while cameras have AI detection on.

## 10. Live-view AI boxes trail moving objects by ~600 ms (all installs)

**Symptom:** boxes follow their objects but visibly lag behind them. The README's "Known
limitations" lists this ("Bounding boxes for moving objects may not be exactly in sync").

**Cause:** `VisionSession.BuildFfmpegArgs` decodes with ffmpeg's defaults. H.264 decoding is then
frame-threaded, which holds back roughly one frame per thread (ffmpeg uses up to 16). Frames are
stamped "captured" when they come out of the pipe, and the browser overlay positions boxes on the
video timeline by that stamp, so every box is drawn as late as the decoder delay. The live-view
video itself is copied without decoding, so it doesn't have this delay.

**Measured** on a 16-thread CPU at 25 fps by matching identical decoded frames from two ffmpeg
processes reading the same stream: default settings delivered each frame **598–600 ms later** than
`-fflags nobuffer -flags low_delay -thread_type slice`. With those options added in the demo (through
a pass-through ffmpeg wrapper), boxes tracked their objects "much better".

**Fix (uncommitted, Vision change):** those three options are added before `-i` in
`VisionSession.BuildFfmpegArgs`, with a new test in `VisionSessionFfmpegArgsTests`.

**Second part, same symptom (~80 ms):** the detection frame-rate cap used ffmpeg's `fps=N` filter,
which picks the input frame nearest each output slot and so holds frames until later ones arrive.
Measured with the same frame-matching method: median **79 ms** extra at 7 fps from 25 fps; the NVDEC
decode itself added nothing measurable. **Fix (uncommitted):** `fps=N` is replaced with
`select='isnan(prev_selected_t)+gte(floor(t*N),floor(prev_selected_t*N)+1)'`, which keeps the first
frame of each 1/N s slot as it arrives: 0 ms added, same average rate (70 frames in 10 s at N=7).
`VisionSessionFfmpegArgsTests` was updated to match. It needs a
Vision Service build (blocked by issue 6). That line has been removed from the README's Known limitations
(uncommitted). Consider the same options
for `MotionSession` (server motion), which decodes the same way.

## 11. Live-view AI boxes move in steps (~2–3 updates/s) instead of smoothly (all installs)

**Symptom:** boxes jump along behind objects at what looks like 1–2 fps, even though detection runs
at ~7 fps.

**Measured:** the Vision Service publishes a new snapshot ~every 135–140 ms, and moving objects keep
stable track IDs across them (Main Gate: 74 position changes, 6 lost IDs in 6 s). Node and Web relay
every snapshot unchanged. So the detections reach the browser at ~7/s, and the steps come from how the
browser picks the instant to draw.

**Cause (`live-view.js`):** the overlay draws boxes for `presentationNow = now − videoLatencyMs()`,
where `videoLatencyMs = (now − lastFragmentArrivalMs) + driftMs + residual`. `driftMs` was a sample
refreshed only on fragment arrival (every 200–500 ms) or on driftTimer's 1 s tick. Between refreshes
`now − lastFragmentArrivalMs` grows with the clock while `driftMs` stands still, so the two cancel:
the presentation clock froze, then jumped at each refresh, and interpolation had nothing to
interpolate across.

**Fix (uncommitted, web JS only):** each fragment's arrival time is published once its append
completes, together with the buffered end it produced (`bufferedEndSec`). `videoLatencyMs` measures
drift against the video's live `currentTime` on every frame, so the presentation clock advances
continuously.

## 12. Minor: background services log errors before setup

Before the wizard has run, services such as `BookmarkRetentionService`, `MotionSpanRetentionService`
and `ProxyHealthMonitor` log "No database provider has been configured" on every cycle. Harmless,
but noisy and alarming in a fresh install's log. They could check `ISetupService.IsDatabaseConfiguredAsync`
first and skip quietly.

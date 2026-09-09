# LarisVMS

Open source security camera recording software (VMS/NVR) for ONVIF cameras. Records video, audio, and
metadata to local disk or an SMB share; live view, playback, and management are all web-based and
work on phone, tablet, and desktop.

---

> **A note on AI-assisted development**
>
> This project is being built with the assistance of AI tooling (Claude Code). Features are planned
> in detail before implementation, any generated code is reviewed as it's written, and changes are
> tested as they land. AI-generated code can still introduce subtle inconsistencies that aren't
> always caught immediately — if you notice something odd, please open an issue.

---

## **Current version [0.195.0](CHANGELOG.md)**

## Stack

- ASP.NET Core 10, Razor Pages
- EF Core 10 + SQL Server
- Bootstrap 5 + GridStack, vendored locally (`wwwroot/lib/`), no build step and no CDN dependency
- IIS InProcess hosting for the web app; recording runs in a separate Windows Service ("node") so it
  survives IIS app pool recycles — see the architecture plan for why
- FFmpeg (installed per node, not bundled — `winget install ffmpeg`) for RTSP ingest, recording, and transcode fallback

## Status

Milestones **M1 (skeleton, setup, deploy)**, **M2 (ONVIF discovery & camera management)**,
**M3 (recorder node & 24/7 recording)**, **M4 (storage & retention)**, **M5 pass 1 (live view)**,
**M6 pass 1 (views & layout editor)**, **M7 pass 1 (playback & timeline)**, and **M8
(motion/events)** are in place: solution layout, Identity + RBAC, encryption at rest, the setup
wizard, `deploy.ps1`, WS-Discovery LAN scan, ONVIF capability probing (Profile S/T/G/M), camera CRUD,
a Windows Service recorder node that supervises `ffmpeg -c copy` per camera with crash/stall
auto-recovery, a per-node storage manager that enforces retention (global default → per-node →
per-camera, `Admin → Settings → Storage and retention`), per-camera quota, and a watermark backstop.
Each node writes to its own storage path (set at install time or on `Admin → Nodes`); it can also be
given a second **archive volume** (SMB share / USB drive) that aged-out footage is *moved* to
instead of deleted, with its own retention — and if the primary volume fills past the watermark,
footage is archived early rather than lost. Playback, thumbnails and export work transparently from
either volume. Also: browser live view
(`Pages/Live`) proxied through IIS with no direct browser-to-node connection and no certificate
needed on the node — all verified end-to-end against real Amcrest cameras, including killing the
recording process and the node process mid-recording and confirming both recover cleanly. Live view
connects automatically for every camera on page load, plays both H.264 and HEVC natively with audio,
auto-reconnects on its own after a dropped session, and can be toggled per-tile into playback mode
without leaving the page. Every tile with an audio track carries its own mute toggle and volume
slider, on live and playback alike; tiles always start muted, and unmuting one is deliberate, per
tile, and never remembered across a page load. `Pages/Views` saves a camera-wall layout (GridStack drag/resize, per-cell
aspect ratio, live video per tile) and plays it back later (`Views/Play`), with a derived
single/two-column layout on phones in portrait, the real saved layout scaled to fit the window on any
short viewport (a phone in landscape), a fullscreen kiosk mode, and optional rotation through a set
of views on a timer. `Pages/Live` and `Pages/Playback` are both driven by saved Views rather than
ad-hoc camera pickers — pick a view and watch (or scrub) the same arrangement you'd see live, with
double-click-to-fullscreen (wheel-zoom/drag-pan while fullscreen) on every tile across all three
pages. Playback scrubs on two canvas timelines (the selected camera's own coverage, and one merged
across every camera, both showing motion coloring and per-rule tag colors), streams segments
incrementally into the decoder, and plays back synchronized across multiple cameras with drift
correction, each resolving its own recordings/gaps independently.

**M8** adds per-camera Zones (motion/privacy/camera-motion polygons), server-side motion detection
with pre/post-roll, ONVIF PullPoint event ingestion, user-configurable event tag rules (with an
optional "drives recording" gate and a custom timeline color), and a live motion indicator badge —
plus four recording modes per camera (`Continuous`, `Motion`, `Schedule`, `Event`), completing the
milestone's original design. Recorder nodes **auto-update themselves**: `deploy.ps1` registers each
build it produces as Pending on `Admin → Node Builds`, and once approved, every node whose reported
version is older downloads, verifies (SHA-256), and swaps its own binary on its next heartbeat — no
manual `install-node.ps1` re-run needed for an ordinary version bump (a service-identity change, like
the LarisVMS rename, is the one case that still needs a manual reinstall). Multi-camera video export
(Playback's toolbar, "Export…") queues one async job per selected camera — each camera's own node
does a `-c copy` remux of its segments for the range — and delivers finished files through the same
signed-proxy path as playback, tracked on an Exports page (auto-refreshing while a job runs, with
delete and retry actions). A range that crosses a camera's reassignment between nodes splits into one
export per node instead of failing. Hovering the Playback timeline shows a small preview thumbnail
(5-minute buckets, generated on demand with a low-priority backfill for gaps).

**Object detection** reports *what* a camera saw, not just that something moved: cameras whose onboard
analytics classify objects surface as Person / Vehicle / Face / Object, each with its own timeline
color and an emoji badge on the live tile (🚶 🚗 🙂 📦). A detection also counts toward Motion-mode
recording, so footage of a person is retained even when pixel-motion detection wouldn't have fired.
These are **discrete events, not bounding boxes** — "a person was here around this time", with no
on-screen box, since per-frame boxes need the ONVIF metadata RTP track and probing this deployment's
own Amcrest fleet found it carries only a motion-cell grid with no object geometry at all
(`probe-metadata-track.ps1`).

**Native AI object detection** (`LarisVMS.Vision`/`LarisVMS.Vision.Service`) closes that gap without
depending on camera hardware at all: each node runs its own real-time YOLO/ByteTrack detection
pipeline against a second RTSP session on the camera's Sub stream, GPU-accelerated where available
(per-node accelerator setting, `Admin → Nodes`: Auto/Nvidia/Intel/AMD/CPU, graceful fallback and clear
logging when nothing's available). Detected objects draw as live, tracked **on-screen bounding boxes**
— a client-side-only overlay, independently toggleable for Moving vs. Idle objects, never baked into
recordings — colored by a small auto-assigned category (Human/Vehicle/Animal/Object) with the specific
class riding alongside ("Vehicle — car"), and a count on the badge ("Human ×2") when several objects
of the same type were moving at once. A detected object also gets its own cropped, best-frame
snapshot image, separate from the ordinary hover-thumbnail cache; once a moving object leaves it
finalizes its own snapshot promptly rather than being merged with a later, unrelated object of the
same type (`Admin → Settings → Detection → Snapshot motion accuracy`, with an adjustable departure
grace). An opt-in, per-camera jitter rejection (`Detection → Reject stationary-object jitter`) can
additionally hold a distant parked vehicle whose box only wobbles as idle. Runs as a sibling process
(`LarisVMS.Vision.Service`) so a site that never enables it pays nothing for the GPU/ONNX Runtime
dependency, and a bad GPU/driver interaction can never take down recording itself. **Unverified against
real GPU hardware or an actual camera end-to-end** — see [CHANGELOG.md](CHANGELOG.md).

**Camera integration plugins** cover what ONVIF can't express. A provider declares which makes/models
it handles, camera probing matches it automatically from the reported make and model, and the node
runs that vendor session alongside its ONVIF one. Providers are compiled in and listed in one registry
rather than loaded from external assemblies — nodes ship as a single self-contained auto-updating
executable, so a drop-in plugin folder would need its own distribution and version-matching channel.
The first provider reads **Dahua / Amcrest smart events** over the vendor CGI event API; these cameras
classify objects onboard but never publish that over ONVIF, so on this fleet the plugin is the *only*
source of object classes. Verified against real hardware (Amcrest `IP8M-DLB2998EW-AI`): person
detections arrive as clean start/stop pairs, and the camera honors the narrow code subscription the
plugin uses (`probe-dahua-events.ps1`).

**Multi-sensor cameras** (quad-lens and similar) split into one camera per lens, grouped by ONVIF
`VideoSourceToken`, each with its own recorder and retention. Single-lens cameras are unaffected and
still add automatically with no extra step. This also fixed a real bug on multi-lens hardware, where
the profile ranker could mix profiles from different lenses into one camera's Main/Sub streams.

**Branding** (`Admin → Settings → Branding`) sets the application name, primary and accent colors, a
font from a closed list, and a logo shown on the navbar and login page. Values are allowlist-validated
before storage, since they're interpolated into CSS.

**M11 (operations)** is under way: an audit log viewer (`Logs → Audit Logs`, alongside `Logs → System
Logs` in the same top-level nav item, each independently retained and independently permissioned)
recording what each user did, from which IP — including viewing a camera or a view, starting
playback, and starting or downloading an export — where change entries capture the actual
`old → new` values, except for secrets and keys, which record only *that* they changed and never the
value; per-node clock-skew detection (`Admin → Nodes`) flagging when a recorder's own OS clock has
drifted from the server's; scheduled or on-demand database backups (`Admin → Settings → Backups` —
restore is deliberately left to other tools, e.g. SSMS); application log capture on both tiers with a
viewer (`Logs → System Logs`); and a
health dashboard (the Dashboard page, auto-refreshing, sortable and paginated, with an optional
thumbnail column) showing each camera's live fps/bitrate/reconnect count, the audio codec and sample
rate it is actually sending, and every node's online status.

**M14 (identity & access control)**: a Roles admin page (create/rename/delete a role, edit its
Resource×Action permission matrix against a fixed, closed permission catalog — no free-text grants
that could typo into matching nothing) and a Users admin page (create an account, assign roles,
enable/disable via account lockout, reset a password), with self-registration disabled now that
there's a real way to provision accounts. Per-camera/group access control (`Admin → Settings → Camera
Access`) narrows a role's View/Playback/Export/PTZ/Talk/Configure access down to specific cameras or
groups — a role with no grants is unrestricted, so this only ever narrows, never silently locks out an
existing deployment. Per-role session lifetime, server-backed user preferences (theme, last-watched
view, table page sizes — follow the user across devices/browsers instead of living in localStorage),
and running live/playback traffic on a port of its own (see below) round out the milestone.

**M15 (notifications)**: outbound email (`Admin → Settings → Email`) through SMTP, Microsoft Graph
(app-only, no per-user consent), or Gmail (OAuth2, needs a one-time consent redirect — see below), and
**Alerting** (`Admin → Alerts`) — a rule watches one camera or node for a condition (not reporting,
offline, storage below a percentage) and fires through up to six channels: email, webhook, ntfy,
Pushover, Slack, or Teams, each independently configured and cooled down so a standing condition
doesn't re-alert every tick.

**M16 (viewing experience)**: pinch-to-zoom in fullscreen (Live and Playback alike, alongside the
existing wheel-zoom/drag-pan), drag-select-to-zoom on a Playback tile, a playback speed selector from
1/32× to 32× (native `playbackRate` through 8×, a seek-driven stepped/slideshow mode above that), and
a per-user toggle for the timeline's event-tag coloring (off by default — a real-phone walkthrough
found it busy for everyday review).

**M17 (hardware-transcode groundwork)**: each node probes its own `ffmpeg -encoders` output at startup
and reports which hardware encoders it actually has (QSV/NVENC/AMF, badged on `Admin → Nodes`), behind
a shared, unit-tested `-hwaccel`/`-vf`/`-c:v` argument builder every transcode-needing feature below
now shares rather than each inventing its own ffmpeg invocation.

**M18 (recording pipeline)**: **Bookmarks** — mark a moment during Playback with a note, and a
Bookmarks page lists every one with a "▶ Play" deep link back to the exact instant (entries expire
along with the footage they point at). **Snapshots** — every motion/detection event gets a
timeline-matched thumbnail browsable on its own page (paged, filterable, same deep-link-to-Playback
behavior as Bookmarks) instead of only ever being a color on the timeline. **Basic PTZ** — a
directional pad + zoom on each PTZ-capable camera's Live tile (not yet run against real PTZ hardware).
**Adaptive streaming** — a live tile too small to benefit from a camera's full resolution is
automatically served its Sub stream instead of Main, with a manual per-tile Auto/HD/SD override and
fullscreen always forcing Main; a global toggle (`Admin → Settings → Live View`) lets an admin disable
it, and the node itself is the sole gate — turning it off actually stops the extra Sub-stream pulls,
not just hides the client-side switching. **Configurable segment length** (`Admin → Settings →
Recording`, also per-camera) and **indexed scrub seeking** — Playback can now jump straight to a
scrub target inside a segment instead of downloading everything before it first, the fix for a real,
measured slow/stuck-loading scrub on large (24–44MB) 4K/HEVC segments. Static privacy-mask burn-in
(per-camera `Privacy` zones, burned in via the M17 encode pipeline) is implemented and tested but
**currently shipped disabled** — validated live, it gets a masked camera stuck cycling
Connecting/Backoff on real Intel/NVIDIA hardware, and the root cause wasn't found before it was
kill-switched back to safe/off pending real diagnostic logs from a stuck attempt.

Real-time on-screen bounding-box overlays now exist (native AI object detection, above) without
depending on camera hardware at all. The *camera-onboard* path specifically is still blocked on
hardware, unrelated to that: per-frame boxes from a camera's own analytics need the ONVIF metadata RTP
track, and every camera probed on this fleet (including a Dahua/Amcrest model whose vendor CGI events
do carry a `BoundingBox`, not yet consumed) either carries only a motion-cell grid over ONVIF or hasn't
had that box wired up yet. ONVIF-pushed motion zones, instant replay, evidence lock, smart search, a
hardware-decode fallback for a browser that can't natively decode a camera's codec, and a dedicated
mobile-UI polish pass (scoped from a real phone walkthrough, not guessed from the CSS) haven't
started — see [CHANGELOG.md](CHANGELOG.md) for everything shipped and the architecture plan for the
full milestone roadmap.

> **`web.config` currently runs `ASPNETCORE_ENVIRONMENT=Development`**, on purpose, for this
> milestone-by-milestone development phase — it surfaces full exceptions in the browser instead of
> the generic error page. **Switch it to `Production`** before any milestone that records real
> footage or is reachable outside a trusted dev network; Development's error pages can leak
> connection strings and internal file paths.

## Recorder node dependencies

These are installed on each **recorder node** (the machine running `LarisVMS.Node`), not on the web
server. `install-node.ps1` does not install any of them — it only reports what's missing.

### FFmpeg (required)

Every node needs **FFmpeg** — not bundled. Install it on each recorder node:

```
winget install ffmpeg --scope machine
```

The node discovers it automatically at every startup: an explicit `--ffmpeg-path` /
`LARISVMS_FFMPEG_PATH` wins, then `ffmpeg` on `PATH`, then the newest `ffmpeg.exe` under the WinGet
package store (`C:\Program Files\WinGet\Packages\…`). An ffmpeg upgrade is picked up with no
re-install. `install-node.ps1` preflight-checks that it's present and aborts with instructions if
not. FFmpeg is invoked as a separate process, so its license does not propagate (see
`THIRD_PARTY_NOTICES.txt`).

### AI object detection (optional)

Only needed on nodes that will actually run AI object detection. A node without any of this still
records normally and keeps every other detection path (server-side motion zones, ONVIF camera
events, vendor integrations) working exactly as before — AI detection is additive.

There is **one** node package for every machine. It bundles the DirectML and CPU ONNX Runtime
backends plus the small CUDA files; the Vision Service picks one at startup for whatever hardware the
node detected — CUDA for an NVIDIA GPU when the CUDA Toolkit is present, otherwise DirectML (any
Direct3D 12 GPU: NVIDIA, AMD, Intel), otherwise CPU. The large CUDA provider library
(`onnxruntime_providers_cuda.dll`, ~320 MB) is **not** in the package — `deploy.ps1` seeds it into
the server and an NVIDIA node downloads it once, so CPU/DirectML-only installs don't carry it.
`deploy.ps1` / `build-node.ps1` take no accelerator flag:

```powershell
.\deploy.ps1     -IISSiteName "LarisVMS"
.\build-node.ps1                            # -SkipVision for a recording-only package
```

| Detected hardware | Backend used | Must be installed on the node |
|---|---|---|
| NVIDIA GPU | CUDA (falls back to DirectML) | CUDA Toolkit 12.x + cuDNN 9.x (see below) — without them it runs DirectML; the node also downloads the CUDA provider library from the server once |
| AMD / Intel GPU | DirectML | Nothing beyond a current GPU driver |
| No GPU | CPU | Nothing |

`install-node.ps1` reports which backend the node will use and, on an NVIDIA box missing the CUDA
Toolkit, prints exactly what to install; the node runs DirectML in the meantime and `Admin → Nodes`
flags it. TensorRT and OpenVINO are not covered by the auto-path (OpenVINO is not bundled; DirectML
covers Intel GPUs).

**NVIDIA CUDA** needs the CUDA **Toolkit** installed on the node, not just a driver — the NuGet
packages do not ship the CUDA runtime DLLs, and without them the CUDA backend can't load
(`Error loading onnxruntime_providers_cuda.dll which depends on "cublasLt64_12.dll" which is
missing`) and the node falls back to DirectML:

- **[CUDA Toolkit 12.8](https://developer.nvidia.com/cuda-12-8-0-download-archive)** — provides
  `cublasLt64_12.dll`, `cublas64_12.dll`, `cudart64_12.dll`, `cufft64_11.dll`. The installer adds its
  `bin` directory to `PATH`; the runtime libraries alone are enough (Nsight and the Visual Studio
  integration can be skipped).
- **[cuDNN 9.x for CUDA 12](https://developer.nvidia.com/cuda/cuda-x-libraries/cudnn)** — needed for
  `cudnn64_9.dll`, and not included in the CUDA Toolkit installer above. The simplest route on a
  recorder node is pip (this needs [Python](https://www.python.org/downloads/) on the node itself):

  ```powershell
  pip install --extra-index-url https://pypi.nvidia.com nvidia-cudnn-cu12
  ```

  That drops the DLLs under the Python environment's site-packages rather than anywhere the OS
  loader searches, so point `Vision:CudnnPath` at that folder — typically
  `…\site-packages\nvidia\cudnn\bin`. (`python -c "import nvidia.cudnn, os;
  print(os.path.join(os.path.dirname(nvidia.cudnn.__file__), 'bin'))"` prints the exact path.)
  Downloading the archive from NVIDIA instead works the same way: unpack it anywhere, then either
  point `Vision:CudnnPath` at its `bin` or put that directory on `PATH`.
- **TensorRT 10.13.3** — optional, performance only. Off unless `Vision:EnableTensorRt` is set, which
  also requires `Vision:TensorRtEngineCachePath`. YOLOX follows `Vision:EnableTensorRt` /
  `Vision:TensorRtPrecision` directly. D-FINE has its own web control instead —
  **Admin > Settings > Detection > "D-FINE TensorRT"** (`Off` / `FP32`, node-scoped, with a per-node
  override on Admin > Nodes). `FP32` gives graph fusion and kernel selection with no precision risk;
  it still does nothing unless the node also has `Vision:EnableTensorRt` set and the SDK installed.
  An `FP16` option exists but is disabled — a straight FP16 cast overflows D-FINE's transformer
  decoder and the mixed-precision model that would avoid it isn't producible with current tooling.

A detection model is also required: `build-node.ps1` bundles whatever `.onnx` files are in `models/`
at the repo root into the node package, and a node with no model can't detect anything. No model is
committed to this repo — export one with [`tools/export-models/`](tools/export-models/), which needs
**[Python](https://www.python.org/downloads/)** on whichever machine does the export. That's normally
the build machine rather than a recorder node, since the exported `.onnx` travels inside the package
(a node only needs Python of its own if cuDNN is installed there via pip, above):

```powershell
cd tools\export-models
py -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python export.py
```

Only permissively-licensed weights are exported — Ultralytics YOLOv8/11/26 are deliberately excluded,
since their weights are AGPL-3.0 and this project is MIT.

> **A node's *first* AI detection install needs a manual `install-node.ps1` run.** Recorder
> auto-update only replaces binaries that are already present, so it will keep an existing
> `LarisVMS.Vision.Service.exe` current but never place one that was never there.

Whether the accelerator was actually resolved is visible per node at `Admin → Nodes`, and a startup
failure names its own cause in the node's log at `C:\ProgramData\LarisVMS\logs\node-*.log`.

## Quick start

1. Create an IIS site pointing at an empty folder (e.g. `E:\Sites\LarisVMS`)
2. Create an app pool set to **No Managed Code**
3. Set the app pool identity to a service account with access to your SQL Server (permissions are
   granted automatically on database creation, via `db_owner`)
4. Run the deploy script (must be Administrator):

   ```powershell
   .\deploy.ps1 -IISSiteName "LarisVMS" -IISAppPoolName "LarisVMS"
   ```

5. Browse to the site — the setup wizard opens automatically and walks through database, admin
   account, storage location, recorder node registration, and branding (the wizard sets an initial
   name and color; everything else, including the logo, is editable later at
   `Admin → Settings → Branding`)

## Deploy script

```powershell
# By IIS site name (reads the connection string from setup-generated.json at the site root)
.\deploy.ps1 -IISSiteName "LarisVMS"

# By full URL (also resolves virtual applications under a site)
.\deploy.ps1 -IISSiteUrl "https://larisvms.example.com"

# Skip migrations (e.g. before the wizard has run)
.\deploy.ps1 -IISSiteName "LarisVMS" -SkipMigrations
```

`deploy.ps1` never deletes recordings: it refuses to run if the configured storage root resolves
under the IIS site directory, and excludes `recordings/`, `spool/`, `exports/`, and
`data-protection-keys/` from its mirror regardless.

### Live/playback on a separate port

`Admin → Settings → Security` can move live view and playback traffic (`/live`,
`/playback-segment`, `/playback-thumbnail`, `/export-download`, camera snapshots) onto a port of its
own, away from the management interface — useful for firewalling the two differently, or exposing
only one beyond the LAN. Setting it there only tells the app which port to expect that traffic on; it
doesn't open a socket. Add a real IIS binding for the same site first:

```powershell
New-WebBinding -Name "LarisVMS" -Protocol https -Port 8443 -IPAddress "*"
# then bind the same TLS certificate the management port already uses to the new one, e.g.:
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -match "larisvms.example.com"
New-Item -Path "IIS:\SslBindings\0.0.0.0!8443" -Value $cert
```

Then set the matching port number in `Admin → Settings → Security`. Once set, live/playback routes
stop responding on the management port and every other route stops responding on the new one; leave
the setting blank to go back to everything sharing whatever port(s) IIS already binds.

### Email (`Admin → Settings → Email`)

SMTP needs nothing beyond the host/port/credentials themselves. The other two providers need setup on
the provider's side first:

- **Microsoft Graph** — register an app in Entra ID, grant it the `Mail.Send` **application**
  permission (not delegated) with admin consent, and use that app's tenant/client id and a client
  secret. App-only auth — no redirect URI, no per-user consent, no refresh token to manage.
- **Gmail** — create an OAuth client (type "Web application") in a Google Cloud project, and add
  `https://<your-host>/Admin/OAuthCallback` as an **authorized redirect URI** before clicking "Save
  and connect to Google" on the settings page, or Google will reject the redirect. The consent screen
  needs the `https://mail.google.com/` scope enabled (or the app in testing mode with the connecting
  account added as a test user). Losing the Data Protection key ring (see below) invalidates the
  stored refresh token the same way it does every other encrypted credential — reconnect afterward.

## Diagnostic scripts

Read-only research tools. Both open their own connection to a camera and touch nothing the recorders
are doing — safe to run against a live system.

```powershell
# Which smart-event codes does this Dahua/Amcrest camera really emit? Walk through frame while it runs.
.\probe-dahua-events.ps1 -CameraHost 192.168.1.50 -Seconds 60

# Same, but subscribing with the exact filtered code list the plugin sends rather than [All] —
# confirms the firmware honors a narrow subscription instead of going silent.
.\probe-dahua-events.ps1 -CameraHost 192.168.1.50 -Seconds 60 -UsePluginCodes

# Does this camera's ONVIF metadata track carry object geometry (bounding boxes) or only motion cells?
.\probe-metadata-track.ps1 -RtspUri "rtsp://192.168.1.50:554/cam/realmonitor?channel=1&subtype=0"
```

`probe-dahua-events.ps1` is the one to reach for when adding a Dahua/Amcrest camera whose detections
don't appear: it prints which codes the plugin already understands and which it's ignoring, so an
unrecognized firmware spelling is a one-line addition to `DahuaCgiEventParser`'s code table rather
than a guess.

## Data at rest

- Camera credentials, SMB credentials, node media signing keys, and every secret under
  `Admin → Settings → Email` (SMTP password, Graph client secret, Gmail client secret and refresh
  token) are encrypted at rest (`LarisVMS.Infrastructure.Security.SecretProtection`), keyed off a Data
  Protection key ring at `%ProgramData%\LarisVMS\keys`. Losing this key ring makes every encrypted
  value unrecoverable — include it in whatever backs up the server, and never delete it as part of a
  deploy.
- `setup-generated.json` (site root) holds the plaintext database connection string and branding.
  It is machine-specific, gitignored, and must never be committed.

## Solution layout

```
src/
  LarisVMS.Core            domain entities, enums, interfaces
  LarisVMS.Onvif            ONVIF SOAP clients, WS-Discovery
  LarisVMS.Media            FFmpeg process supervision, segment detection
  LarisVMS.Infrastructure   EF Core, auth, setup, settings resolution, node control plane
  LarisVMS.Web              Razor Pages host (IIS) + node control plane API
  LarisVMS.Node              recorder Windows Service — 24/7 recording
  LarisVMS.NodeUpdater       detached helper that swaps the node's binary during an auto-update
  LarisVMS.Vision            AI detection capture/inference/tracking — GPU/ONNX Runtime deps live here,
                             never referenced by LarisVMS.Node itself
  LarisVMS.Vision.Service    sibling process LarisVMS.Node supervises as a child — the only project
                             allowed to reference LarisVMS.Vision
tests/LarisVMS.Tests        xUnit
tools/export-models          Python: exports YOLO weights to ONNX for LarisVMS.Vision.Service
```

## Development

```powershell
dotnet tool restore
dotnet build
dotnet test
dotnet ef migrations add <Name> --project src\LarisVMS.Infrastructure --startup-project src\LarisVMS.Web
```

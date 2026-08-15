# LarisVMS

Open source security camera recording software (NVR) for ONVIF cameras. Records video, audio, and
metadata to local disk or an SMB share; live view, playback, and management are all web-based and
work on phone, tablet, and desktop.

---

> **A note on AI-assisted development**
>
> This project is being built with the assistance of AI tooling (Claude Code). Features are planned
> in detail before implementation, generated code is reviewed as it's written, and changes are
> tested as they land. AI-generated code can still introduce subtle inconsistencies that aren't
> always caught immediately — if you notice something odd, please open an issue.

---

## **Current version [0.69.0](CHANGELOG.md)**

## Stack

- ASP.NET Core 10, Razor Pages
- EF Core 10 + SQL Server
- Bootstrap 5 + GridStack, vendored locally (`wwwroot/lib/`), no build step and no CDN dependency
- IIS InProcess hosting for the web app; recording runs in a separate Windows Service ("node") so it
  survives IIS app pool recycles — see the architecture plan for why
- FFmpeg (bundled, LGPL build) for RTSP ingest, recording, and transcode fallback

## Status

Milestones **M1 (skeleton, setup, deploy)**, **M2 (ONVIF discovery & camera management)**,
**M3 (recorder node & 24/7 recording)**, **M4 (storage & retention)**, **M5 pass 1 (live view)**,
**M6 pass 1 (views & layout editor)**, **M7 pass 1 (playback & timeline)**, and **M8
(motion/events)** are in place: solution layout, Identity + RBAC, encryption at rest, the setup
wizard, `deploy.ps1`, WS-Discovery LAN scan, ONVIF capability probing (Profile S/T/G/M), camera CRUD,
a Windows Service recorder node that supervises `ffmpeg -c copy` per camera with crash/stall
auto-recovery, a per-node storage manager that enforces retention (global → per-node → per-camera,
`Admin → Retention`), per-camera quota, and a global watermark backstop, and browser live view
(`Pages/Live`) proxied through IIS with no direct browser-to-node connection and no certificate
needed on the node — all verified end-to-end against real Amcrest cameras, including killing the
recording process and the node process mid-recording and confirming both recover cleanly. Live view
connects automatically for every camera on page load, plays both H.264 and HEVC natively with audio,
auto-reconnects on its own after a dropped session, and can be toggled per-tile into playback mode
without leaving the page. `Pages/Views` saves a camera-wall layout (GridStack drag/resize, per-cell
aspect ratio, live video per tile) and plays it back later (`Views/Play`), with a derived
single/two-column layout on phones, a fullscreen kiosk mode, and optional rotation through a set of
views on a timer. `Pages/Live` and `Pages/Playback` are both driven by saved Views rather than
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
signed-proxy path as playback, tracked on a new Exports page.

Hardware-transcode fallback for browsers that can't decode a camera's native codec, main/sub
auto-switch, and instant replay are not built yet (M5 pass-1 scope), hover thumbnails on the timeline
aren't built (needs frame-extraction work M3 never added), and PTZ/audio, further investigation
tooling (bookmarks, evidence lock, smart search), and object detection haven't started — see
[CHANGELOG.md](CHANGELOG.md) for what's shipped and the architecture plan for the full milestone
roadmap (PTZ/audio → export/investigation → operations → object detection).

Recorder nodes require **FFmpeg** on the machine they run on (LGPL "shared" build recommended — see
the plan's licensing note). Point a node at it with `--ffmpeg-path` or `LARISVMS_FFMPEG_PATH`, or put
`ffmpeg` on `PATH`. Bundling FFmpeg with node deploys is `build-node.ps1`'s job, not yet implemented.

> **`web.config` currently runs `ASPNETCORE_ENVIRONMENT=Development`**, on purpose, for this
> milestone-by-milestone development phase — it surfaces full exceptions in the browser instead of
> the generic error page. **Switch it to `Production`** before any milestone that records real
> footage or is reachable outside a trusted dev network; Development's error pages can leak
> connection strings and internal file paths.

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
   account, storage location, recorder node registration, and branding

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

## Data at rest

- Camera credentials, SMB credentials, and node media signing keys are encrypted at rest
  (`LarisVMS.Infrastructure.Security.SecretProtection`), keyed off a Data Protection key ring at
  `%ProgramData%\LarisVMS\keys`. Losing this key ring makes every encrypted value unrecoverable —
  include it in whatever backs up the server, and never delete it as part of a deploy.
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
tests/LarisVMS.Tests        xUnit
```

## Development

```powershell
dotnet tool restore
dotnet build
dotnet test
dotnet ef migrations add <Name> --project src\LarisVMS.Infrastructure --startup-project src\LarisVMS.Web
```

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

## **Current version [0.138.0 ](CHANGELOG.md)**

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
on-screen box. Per-frame boxes need the ONVIF metadata RTP track, and probing this deployment's own
Amcrest fleet found it carries only a motion-cell grid with no object geometry at all, so boxes are
not achievable on this hardware regardless of how they're implemented (`probe-metadata-track.ps1`).

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
rate it is actually sending, and every node's online status. Alerting, ONVIF-pushed motion zones, on-screen bounding-box overlays, and a mobile-specific UI
pass are not built yet.

Hardware-transcode fallback for browsers that can't decode a camera's native codec, main/sub
auto-switch, and instant replay are not built yet (M5 pass-1 scope), and PTZ/audio and further
investigation tooling (bookmarks, evidence lock, smart search) haven't started — see
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
tests/LarisVMS.Tests        xUnit
```

## Development

```powershell
dotnet tool restore
dotnet build
dotnet test
dotnet ef migrations add <Name> --project src\LarisVMS.Infrastructure --startup-project src\LarisVMS.Web
```

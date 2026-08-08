# Rcordr

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

## **Current version [0.5.0](CHANGELOG.md)**

## Stack

- ASP.NET Core 10, Razor Pages
- EF Core 10 + SQL Server
- Bootstrap 5 (CDN), no build step
- IIS InProcess hosting for the web app; recording runs in a separate Windows Service ("node") so it
  survives IIS app pool recycles — see the architecture plan for why
- FFmpeg (bundled, LGPL build) for RTSP ingest, recording, and transcode fallback

## Status

Milestones **M1 (skeleton, setup, deploy)**, **M2 (ONVIF discovery & camera management)**,
**M3 (recorder node & 24/7 recording)**, and **M4 (storage & retention)** are in place: solution
layout, Identity + RBAC, encryption at rest, the setup wizard, `deploy.ps1`, WS-Discovery LAN scan,
ONVIF capability probing (Profile S/T/G/M), camera CRUD, a Windows Service recorder node that
supervises `ffmpeg -c copy` per camera with crash/stall auto-recovery, and a per-node storage manager
that enforces retention (global → per-node → per-camera, `Admin → Retention`), per-camera quota, and
a global watermark backstop — all verified end-to-end against real Amcrest cameras and real recorded
segments, including killing the recording process and the node process mid-recording and confirming
both recover cleanly. Live view, playback, and object detection are not implemented yet — see
[CHANGELOG.md](CHANGELOG.md) for what's shipped and the architecture plan for the full milestone
roadmap (live view → views/layout → playback/timeline → motion/events → PTZ/audio →
export/investigation → operations).

Recorder nodes require **FFmpeg** on the machine they run on (LGPL "shared" build recommended — see
the plan's licensing note). Point a node at it with `--ffmpeg-path` or `RCORDR_FFMPEG_PATH`, or put
`ffmpeg` on `PATH`. Bundling FFmpeg with node deploys is `build-node.ps1`'s job, not yet implemented.

> **`web.config` currently runs `ASPNETCORE_ENVIRONMENT=Development`**, on purpose, for this
> milestone-by-milestone development phase — it surfaces full exceptions in the browser instead of
> the generic error page. **Switch it to `Production`** before any milestone that records real
> footage or is reachable outside a trusted dev network; Development's error pages can leak
> connection strings and internal file paths.

## Quick start

1. Create an IIS site pointing at an empty folder (e.g. `E:\Sites\Rcordr`)
2. Create an app pool set to **No Managed Code**
3. Set the app pool identity to a service account with access to your SQL Server (permissions are
   granted automatically on database creation, via `db_owner`)
4. Run the deploy script (must be Administrator):

   ```powershell
   .\deploy.ps1 -IISSiteName "Rcordr" -IISAppPoolName "Rcordr"
   ```

5. Browse to the site — the setup wizard opens automatically and walks through database, admin
   account, storage location, recorder node registration, and branding

## Deploy script

```powershell
# By IIS site name (reads the connection string from setup-generated.json at the site root)
.\deploy.ps1 -IISSiteName "Rcordr"

# By full URL (also resolves virtual applications under a site)
.\deploy.ps1 -IISSiteUrl "https://rcordr.example.com"

# Skip migrations (e.g. before the wizard has run)
.\deploy.ps1 -IISSiteName "Rcordr" -SkipMigrations
```

`deploy.ps1` never deletes recordings: it refuses to run if the configured storage root resolves
under the IIS site directory, and excludes `recordings/`, `spool/`, `exports/`, and
`data-protection-keys/` from its mirror regardless.

## Data at rest

- Camera credentials, SMB credentials, and node media signing keys are encrypted at rest
  (`Rcordr.Infrastructure.Security.SecretProtection`), keyed off a Data Protection key ring at
  `%ProgramData%\Rcordr\keys`. Losing this key ring makes every encrypted value unrecoverable —
  include it in whatever backs up the server, and never delete it as part of a deploy.
- `setup-generated.json` (site root) holds the plaintext database connection string and branding.
  It is machine-specific, gitignored, and must never be committed.

## Solution layout

```
src/
  Rcordr.Core            domain entities, enums, interfaces
  Rcordr.Onvif            ONVIF SOAP clients, WS-Discovery
  Rcordr.Media            FFmpeg process supervision, segment detection
  Rcordr.Infrastructure   EF Core, auth, setup, settings resolution, node control plane
  Rcordr.Web              Razor Pages host (IIS) + node control plane API
  Rcordr.Node              recorder Windows Service — 24/7 recording
  Rcordr.NodeUpdater       node binary-swap helper (not yet implemented)
tests/Rcordr.Tests        xUnit
```

## Development

```powershell
dotnet tool restore
dotnet build
dotnet test
dotnet ef migrations add <Name> --project src\Rcordr.Infrastructure --startup-project src\Rcordr.Web
```

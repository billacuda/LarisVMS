# LarisVMS

Open-source video management software (VMS/NVR) for ONVIF cameras. It records video, audio and
events to local disks or network shares on one or more Windows recorder nodes. Live view, playback
and administration all run in the browser, on desktop, tablet and phone.

**Current version: [0.209.0](CHANGELOG.md)**

> **A note on AI-assisted development.** This project is built with the help of AI tooling (Claude
> Code). Features are planned in detail before implementation, generated code is reviewed as it's
> written, and changes are tested as they land. If you notice something odd, please open an issue.

---

## Features

**Cameras**
- ONVIF discovery (WS-Discovery) and capability probing (Profiles S/T/G/M), with optional daily re-probing.
- Main and Sub stream selection, per-stream enable and rename, H.264 and HEVC, with audio.
- Multi-lens cameras split into one camera per lens.
- Vendor integrations matched automatically by make and model (for example, Dahua / Amcrest smart events).
- Groups for sites, buildings and floors.

**Recording**
- One Windows-service recorder node per machine, with as many nodes as you need. Recording is `ffmpeg -c copy`, so no re-encoding.
- Recording modes: Continuous, Motion, Schedule and Event, set globally or per camera, with pre/post-roll.
- Retention set globally, per node and per camera, plus per-camera quotas and a disk watermark backstop.
- An optional archive volume (SMB share or USB drive) that receives aged-out footage instead of deleting it.
- Recording failover to a backup node, and a maintenance mode.
- Self-updating nodes: each build is approved once, and then every node installs it.

**Detection & events**
- Server-side motion detection with polygon zones or a mask grid.
- ONVIF event ingestion, with user-defined event tag rules and timeline colors.
- Built-in AI object detection (YOLOX, D-FINE, or your own ONNX model) on NVIDIA, AMD, Intel or CPU, or an external HTTP inference service.
- Live bounding boxes, object badges on live tiles, and a cropped snapshot for every detected object.

**Viewing**
- Saved camera-wall views with drag-and-drop layout, a fullscreen kiosk mode and rotation.
- Adaptive live streaming that uses Sub streams for small tiles.
- Synchronized multi-camera playback on a zoomable timeline, with preview thumbnails and 1/32× to 32× speed.
- Exports, bookmarks and a snapshots browser.
- Basic PTZ controls.
- Video relayed through the server, through media proxies, or sent directly from node to browser.

**Administration**
- Roles, a permissions matrix and per-camera/group access control.
- Optional Microsoft Entra ID sign-in.
- Alerts by email (SMTP, Microsoft Graph, Gmail), webhook, ntfy, Pushover, Slack and Teams.
- Audit log, system logs, a health dashboard and scheduled database backups.
- IP allow lists, an optional separate port for video traffic, and secrets encrypted at rest.
- Branding: app name, colors, font and logo.
- Built-in **Help** with documentation for every feature.

## Architecture

| Component | Runs as | Role |
|---|---|---|
| **LarisVMS Web** | Windows service (Kestrel, HTTPS) | Web UI, REST API and node control plane. Stores configuration in SQL Server. |
| **LarisVMS Node** | Windows service, one per recording machine | Ingests RTSP, writes segments, enforces retention, runs motion detection, serves live and playback video. |
| **LarisVMS Vision Service** | Child process of a node (optional) | GPU/CPU object detection. It's isolated, so a driver fault never stops recording. |
| **LarisVMS Proxy** | Windows service (optional) | Relays video between browsers and nodes, for remote sites or low-bandwidth links. |

Settings are inherited **global → node → camera**, and the most specific value wins.

## Requirements

- **Operating system:** any 64-bit Windows version supported by [.NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), for both the server and the recorder nodes.
- **Server:** the [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build), and SQL Server (Express works) using SQL or Integrated authentication.
- **Each recorder node:** [FFmpeg](https://ffmpeg.org/) (`winget install ffmpeg --scope machine`).
- **AI detection (optional):** a GPU and its driver, or CPU only. NVIDIA additionally needs CUDA Toolkit 12.x and cuDNN 9.x (see [AI object detection](#ai-object-detection)).
- **Cameras:** ONVIF Profile S or T.
- **Browser:** a current Chrome, Edge, Firefox or Safari. HEVC playback depends on browser support.

Performance depends entirely on your hardware: how many cameras and AI detection streams a node can
handle comes down to its CPU, GPU, disks and network.

## Quick start

### 1. Install the web server

From an elevated PowerShell prompt in the repository root:

```powershell
.\install-web.ps1
# or, to run the service under a domain/service account (for SQL Integrated Security):
.\install-web.ps1 -ServiceCredential (Get-Credential)
```

`install-web.ps1` builds the web app and the node package, applies database migrations, and
installs and starts the **LarisVMS Web** service on port 8444 (change it with `-HttpsPort`).

### 2. Run the setup wizard

Browse to `https://<server>:8444/`. Until you configure a certificate, the server uses a
self-signed one. The wizard sets up the database connection, the first admin account and the
branding, and shows the **node registration key**. You can find the key again later under
**Settings → Node defaults**.

### 3. Add a certificate

Edit `C:\Program Files\LarisVMS\Web\appsettings.Production.json` and set
`Kestrel:Certificates:Default:Path` and `:Password` to your `.pfx` file, then restart the service:

```powershell
Restart-Service LarisVMSWeb
```

After that, a renewed certificate file is picked up automatically within a minute.

### 4. Install recorder nodes

Copy `publish\LarisVMS.Node\win\` (built in step 1) to each recording machine. Then, as Administrator:

```powershell
winget install ffmpeg --scope machine
.\install-node.ps1 -ServerUrl https://<server>:8444 -RegistrationKey <key> -StorageRoot D:\Recordings
```

The node registers itself and appears under **Settings → Nodes**. Optional flags:

| Flag | Purpose |
|---|---|
| `-ArchiveRoot` | Second volume that receives aged-out footage. |
| `-ClientPort`, `-ClientPfxPath`, `-ClientEndpointHost` | Lets browsers stream directly from this node. |
| `-ServiceCredential` | Run the service as an account that can reach SMB storage. |

### 5. Add cameras

Use **Cameras → Discover**, or add a camera by its ONVIF device service URL. Then assign each
camera to a recorder node. For everything else, open **Help** in the sidebar.

## Upgrading

Pull the new release and run `.\install-web.ps1` again. It never deletes recordings, and never
overwrites `appsettings.Production.json`, `setup-generated.json` or the data-protection keys.

Recorder nodes and media proxies **update themselves**. Each run registers a new build as
*Pending* under **Settings → Node builds**. Once you approve it, every older node downloads it,
verifies its SHA-256 and installs it on its next check-in. You can turn this off under **Settings →
Node defaults**.

> A node's first AI detection install needs one manual `install-node.ps1` run. Auto-update only
> replaces files that are already present.

`deploy.ps1` (the old IIS-based deploy) is deprecated. Use `install-web.ps1`.

## AI object detection

AI detection is optional and per camera. A node without it still records, and still uses server
motion, camera events and vendor integrations. The node package includes the CPU, DirectML and CUDA
backends, and picks one at startup:

| Hardware | Backend | Needed on the node |
|---|---|---|
| NVIDIA GPU | CUDA (falls back to DirectML) | [CUDA Toolkit 12.x](https://developer.nvidia.com/cuda-toolkit-archive) and [cuDNN 9.x](https://developer.nvidia.com/cudnn). The large CUDA provider library is downloaded from the server once. |
| AMD / Intel GPU | DirectML | A current GPU driver |
| No GPU | CPU | Nothing |

- **cuDNN** can be installed with
  `pip install --extra-index-url https://pypi.nvidia.com nvidia-cudnn-cu12`. Then point
  `Vision:CudnnPath` in the node's configuration at the package's `bin` folder.
- **TensorRT 10.x** is optional, for extra speed. Enable it with `Vision:EnableTensorRt` and
  `Vision:TensorRtEngineCachePath`.
- **Models are never bundled.** The built-in YOLOX models are downloaded from the server on first
  use. For D-FINE, export one with [`tools/export-models`](tools/export-models/). For a custom model,
  place any `.onnx` file in `C:\ProgramData\LarisVMS\models` on the node, with an optional
  same-named `.json` file describing its decoder and labels.

**Frame rate:** 5–7 fps per camera is enough for accurate object detection and tracking. Cap
detection there (**Settings → AI detection → Max detection frame rate**, or per camera) rather than
sending every frame; higher rates add GPU load without improving results.

`build-node.ps1 -SkipVision` produces a recording-only node package.

## Security

- **Encrypted secrets.** Camera, SMB, node and email credentials are encrypted at rest with the
  ASP.NET Core data-protection keys in `%ProgramData%\LarisVMS\keys`. **Back this folder up.** Without
  it, encrypted values can't be recovered.
- **Connection string.** `setup-generated.json` in the install folder holds the database connection
  string. It's machine-specific, and must never be committed.
- **Network controls.** **Settings → Security** has IP allow lists for management and API traffic,
  and separately for video traffic. **Settings → Live view** can move video traffic to its own port.
- **Secret handling.** Secrets are write-only in the UI, and the audit log records that a secret
  changed but never its value.

## Documentation

- **In-app Help:** the **Help** item in the sidebar documents every feature and setting, including
  troubleshooting.
- **[CHANGELOG.md](CHANGELOG.md):** release notes. Older releases are in [`changelog-archive/`](changelog-archive/).

## Building from source

```powershell
dotnet tool restore
dotnet build
dotnet test
```

Add a database migration with:

```powershell
dotnet ef migrations add <Name> --project src\LarisVMS.Infrastructure --startup-project src\LarisVMS.Web
```

A release needs the same version in every project's `<Version>` (Web, Node, NodeUpdater, Proxy,
Core), a `BumpVersionX_Y_Z` migration that inserts into `AppVersions`, and a matching `CHANGELOG.md`
heading. `install-web.ps1` refuses to deploy if any of these disagree.

### Project layout

```
src/
  LarisVMS.Core             Domain entities, enums, interfaces
  LarisVMS.Infrastructure   EF Core, identity, settings resolution, node control plane
  LarisVMS.Onvif            ONVIF SOAP clients, WS-Discovery
  LarisVMS.Media            FFmpeg supervision, segment handling
  LarisVMS.Web              Web UI (Blazor static SSR + Razor Pages) and API
  LarisVMS.Node             Recorder node service
  LarisVMS.NodeUpdater      Swaps a node's binaries during auto-update
  LarisVMS.Vision           Detection capture, inference and tracking (GPU/ONNX Runtime)
  LarisVMS.Vision.Service   Detection child process supervised by the node
  LarisVMS.Proxy            Media relay
tests/LarisVMS.Tests        xUnit tests
tools/export-models         Python: exports detection models to ONNX
```

## Known limitations

- Privacy-mask burn-in and camera-side motion zones can be configured, but are **disabled** pending fixes.
- Bounding boxes from the camera's own analytics aren't shown; only LarisVMS's own detection draws boxes.
- PTZ has not been tested against real PTZ hardware.
- Windows only (64-bit), for both the server and the nodes.

## License

LarisVMS is licensed under the [Apache License 2.0](LICENSE). Third-party components and their
licenses are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt). FFmpeg is not bundled;
it runs as a separate process.

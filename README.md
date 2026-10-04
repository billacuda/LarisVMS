# LarisVMS

Open-source video management software (VMS/NVR) for ONVIF cameras. It records video, audio and
events to local disks or network shares on one or more Windows recorder nodes. Live view, playback
and administration all run in the browser, on desktop, tablet and phone.

<p align="center">
  <a href="https://www.buymeacoffee.com/billacuda"><img src="https://img.buymeacoffee.com/button-api/?text=Buy%20me%20a%20coffee&emoji=%E2%98%95&slug=billacuda&button_colour=5F7FFF&font_colour=ffffff&font_family=Cookie&outline_colour=000000&coffee_colour=FFDD00" alt="Buy me a coffee" height="50" /></a>
</p>

**Current version: [0.211.0](CHANGELOG.md)**

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
- Nodes keep recording through a server or database outage, even across a reboot.

**Detection & events**
- Server-side motion detection with polygon zones or a mask grid.
- ONVIF event ingestion, with user-defined event tag rules and timeline colors.
- Built-in LarisVision AI detection (YOLOX, D-FINE, or your own ONNX model) on NVIDIA, AMD, Intel or CPU, or an external HTTP inference service.
- Live bounding boxes, object badges on live tiles, and a cropped snapshot for every detected object.
- Object tracking (ByteTrack), confidence scores, optional high-resolution snapshots, and a Slice mode that tiles wide or panoramic cameras so small objects are still detected.

**Viewing**
- Saved camera-wall views with drag-and-drop layout, a fullscreen kiosk mode and rotation.
- Adaptive live streaming that uses Sub streams for small tiles.
- Synchronized multi-camera playback on a zoomable timeline, with preview thumbnails and 1/32× to 32× speed. Switch any live tile to playback in place.
- Multi-camera exports, bookmarks and a snapshots browser.
- Basic PTZ controls.
- Video relayed through the server, through media proxies, or sent directly from node to browser.
- Phone-friendly layout, installable as an app from Chrome on Android or Safari on iOS, with pinch-to-zoom on timelines and in fullscreen. See [Using it on a phone](#6-use-it-on-your-phone-optional).
- Dark mode.

**Administration**
- Roles, a permissions matrix and per-camera/group access control, with auto-expiring role assignments and PTZ priority between roles.
- Optional Microsoft Entra ID sign-in.
- Alert rules that watch a camera or node, sent by email (SMTP, Microsoft Graph, Gmail), webhook, ntfy, Pushover, Slack and Teams.
- Audit log, system logs, scheduled database backups, and a health dashboard with per-camera fps, bitrate and reconnects.
- API keys for automation, and a monitoring status endpoint.
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
- **Server:** SQL Server (Express works) using SQL or Integrated authentication. For SQL Server Express, the server name is `.\SQLEXPRESS`. Building from source also needs the [.NET 10 SDK](https://dotnet.microsoft.com/download); the MSIs don't need any .NET runtime.
- **Each recorder node:** [FFmpeg](https://ffmpeg.org/) (`winget install ffmpeg --scope machine`).
- **LarisVision (optional):** a GPU and its driver, or CPU only. NVIDIA additionally needs CUDA Toolkit 12.x and cuDNN 9.x (see [LarisVision](#larisvision-ai-detection)) if you want to use CUDA or TensorRT acceleration, otherwise DirectML works out of the box.
- **Cameras:** ONVIF Profile S or T.
- **Browser:** a current Chrome, Edge, Firefox or Safari. HEVC playback depends on browser support. On phones, Chrome (Android) or Safari (iOS).

Performance depends entirely on your hardware: how many cameras and LarisVision streams a node can handle comes down to its CPU, GPU, disks and network.

## Installing

Download the installers from the [Releases](https://github.com/billacuda/LarisVMS/releases) page:

| Installer | Install on |
|---|---|
| `LarisVMS-Web-<version>-x64.msi` | The server. Self-contained, so no .NET runtime is needed. It also carries the node and proxy builds for auto-update. |
| `LarisVMS-Node-<version>-x64.msi` | Each recording machine. |
| `LarisVMS-Proxy-<version>-x64.msi` | Optional relay machines, for remote sites or low-bandwidth links. |

Double-click an installer to be prompted for its settings, or pass them on the command line. Anything not given on the command line is asked for. In a silent install (`/qn`), a missing required value stops the install with a message naming the property.

### 1. Install the web server

```powershell
msiexec /i LarisVMS-Web-0.211.0-x64.msi
# silent, with a certificate:
msiexec /i LarisVMS-Web-0.211.0-x64.msi HTTPSPORT=8444 CERTPATH=C:\certs\vms.pfx CERTPASSWORD=secret /qn
```

| Property | Default | Purpose |
|---|---|---|
| `HTTPSPORT` | 8444 | HTTPS port, plus its firewall rule. |
| `CERTPATH`, `CERTPASSWORD` | blank | Server certificate (`.pfx`). Blank uses a self-signed certificate; a renewed file at the same path is picked up automatically. A certificate your devices trust is also needed to install LarisVMS as a phone app. |
| `SERVICEACCOUNT`, `SERVICEPASSWORD` | LocalSystem | Service account, for example one with SQL Integrated Security rights. |
| `INSTALLFOLDER` | `C:\Program Files\LarisVMS\Web` | Install location. |

### 2. Run the setup wizard

Browse to `https://<server>:8444/`. The wizard sets up the database connection, the first admin account and the branding, and shows the **node registration key**. You can find the key again later under **Settings → Node defaults**.

### 3. Install recorder nodes

Install [FFmpeg](https://ffmpeg.org/) on each recording machine first (`winget install ffmpeg --scope machine`), then:

```powershell
msiexec /i LarisVMS-Node-0.211.0-x64.msi SERVERURL=https://<server>:8444 REGISTRATIONKEY=<key> STORAGEROOT=D:\Recordings /qn
```

| Property | Default | Purpose |
|---|---|---|
| `SERVERURL`, `REGISTRATIONKEY` | (required on first install) | Where the node registers. Not needed again once it's registered. |
| `STORAGEROOT` | | Where the node records. |
| `ARCHIVEROOT` | | Second volume that receives aged-out footage. |
| `FFMPEGPATH` | auto-detected | Path to `ffmpeg.exe`. |
| `LIVEPORT` | 8554 | Live video port, plus its firewall rule. |
| `INSECURETLS` | | `1` accepts the server's self-signed certificate. |
| `CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`, `CLIENTALLOWINSECURE` | | Lets browsers stream directly from this node. |
| `SERVICEACCOUNT`, `SERVICEPASSWORD` | LocalSystem | Run as an account that can reach SMB storage. |
| `INSTALLFOLDER` | `C:\Program Files\LarisVMS\Node` | Install location. |

The node registers itself and appears under **Settings → Nodes**.

### 4. Install media proxies (optional)

```powershell
msiexec /i LarisVMS-Proxy-0.211.0-x64.msi SERVERURL=https://<server>:8444 REGISTRATIONKEY=<key> CLIENTPORT=4443 /qn
```

Properties: `SERVERURL`, `REGISTRATIONKEY`, `CLIENTPORT` (default 4443), `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`, `CLIENTALLOWINSECURE`, `INSECURETLS`, `SERVICEACCOUNT`, `SERVICEPASSWORD`, `INSTALLFOLDER`, with the same meanings as for the node.

### 5. Add cameras

Use **Cameras → Discover**, or add a camera by its ONVIF device service URL. Then assign each camera to a recorder node. For everything else, open **Help** in the sidebar.

### 6. Use it on your phone (optional)

Open the same address in your phone's browser. To install it as an app, choose **Install app** from the ⋮ menu in Chrome on Android, or **Share → Add to Home Screen** in Safari on iOS. Phones won't install a site that uses the self-signed certificate a fresh install starts with, so set `CERTPATH` to a certificate your devices trust first.

## Upgrading

Run the new version's MSI. Its settings are remembered from the previous install, so no properties are needed; secrets aren't stored, so a custom service account's password is asked for again. The web app applies database migrations itself when it starts. Upgrades never touch recordings, `appsettings.Production.json`, `setup-generated.json`, node registration or the data-protection keys.

An installation made with the PowerShell scripts is upgraded the same way: the MSI takes over the existing service and keeps its settings and registration.

Recorder nodes and media proxies also **update themselves**. A new web install registers the node and proxy builds it carries as *Pending* under **Settings → Node builds**. Once you approve one, every older node downloads it, verifies its SHA-256 and installs it on its next check-in. You can turn this off under **Settings → Node defaults**.

Uninstalling removes the program, its service and firewall rules. Recordings, configuration and everything under `%ProgramData%\LarisVMS` are kept.

## Installing from source

The PowerShell scripts build from the repository and install on the same machine; they take the same settings as the MSIs (`-HttpsPort`, `-ServiceCredential`, `-ServerUrl`, `-RegistrationKey`, `-StorageRoot`, …).

```powershell
.\install-web.ps1                       # build, then install or upgrade the web server
.\build-node.ps1; .\build-proxy.ps1     # node and proxy packages in publish\, each with its install script
.\build-installers.ps1                  # build all three MSIs into publish\installers\
```

`deploy.ps1` (the old IIS-based deploy) is deprecated.

## LarisVision AI detection

LarisVision is optional and per camera. A node without it still records, and still uses server
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

**Tested hardware:** LarisVMS has been run in real environments on:
- **Windows:** Windows 11, Windows Server 2022, and a Windows Server 2022 guest VM on Hyper-V 2022 with RTX 2070 GPU passthrough
- **Recording storage:** NVMe, Hyper-V virtual disks (on NVMe), a Windows Storage Pool passed through to a Hyper-V VM, and SMB file shares
- AMD Ryzen 7 5800X3D
- Intel Core i5-12600K, including LarisVision on its integrated GPU via DirectML
- NVIDIA GeForce RTX 2070: the main testing GPU and a dedicated recording node (DirectML, CUDA, and TensorRT FP32)
- NVIDIA GeForce RTX 4080 Super (DirectML, CUDA, and TensorRT FP16)

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

Release installers are built with `.uild-installers.ps1` (WiX v5, restored from NuGet).

A release needs the same version in every project's `<Version>` (Web, Node, NodeUpdater, Proxy,
Core), a `BumpVersionX_Y_Z` migration that inserts into `AppVersions`, and a matching `CHANGELOG.md`
heading. `install-web.ps1` and `build-installers.ps1` refuse to run if any of these disagree.

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
- Bounding boxes from the camera's own analytics aren't shown; only LarisVision draws boxes.
- Face detection and object appeared/missing events come from the camera's own analytics. LarisVision doesn't detect these on its own yet.
- PTZ has not been tested against real PTZ hardware.
- Windows only (server and nodes).
- Entra SSO has not been tested (I don't have a tenant to test it against, but in theory it should work)
- AD integration not implemented yet, but is in the works.
- Some features listed may have placeholders (webhooks, Teams/Slack integration)
- Basic email functionality should work with SMTP. Graph API email has not been tested yet.

## License

LarisVMS is licensed under the [Apache License 2.0](LICENSE). Third-party components and their
licenses are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt). FFmpeg is not bundled;
it runs as a separate process.

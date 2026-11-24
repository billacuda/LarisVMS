# LarisVMS

Open-source video management software (VMS/NVR) for ONVIF cameras. It records video, audio and
events to local disks or network shares on one or more recorder nodes, on Windows, Linux, or a mix
of both. Live view, playback, and administration all run in the browser, on desktop, tablet, and phone.

<p align="center">
  <a href="https://www.buymeacoffee.com/billacuda"><img src="https://img.buymeacoffee.com/button-api/?text=Buy%20me%20a%20coffee&emoji=%E2%98%95&slug=billacuda&button_colour=5F7FFF&font_colour=ffffff&font_family=Cookie&outline_colour=000000&coffee_colour=FFDD00" alt="Buy me a coffee" height="50" /></a>
</p>

**Current version: [0.223.0](CHANGELOG.md)**

📖 **[Documentation](https://www.larisvms.com/docs/)**: how to install, set up, and use LarisVMS. Worth a
read before you install.

> **A note on AI-assisted development.** This project is built with the help of AI tooling (Claude
> Code). Features are planned in detail before implementation, generated code is reviewed as it's
> written, and changes are tested as they land. If you notice something odd, please open an issue.

---

## ✨ Features

**Cameras**
- ONVIF discovery (WS-Discovery) and capability probing (Profiles S/T/G/M), with optional daily re-probing.
- Main and Sub stream selection, per-stream enable and rename, H.264 and HEVC, with audio.
- Change a camera's own stream, image, and audio settings over ONVIF (codec, resolution, frame rate, bitrate, CBR/VBR, day/night, WDR, and more), one camera at a time or in bulk for cameras of the same model.
- A camera list that can be filtered by any column, with per-user column choices.
- Multi-lens cameras split into one camera per lens.
- Vendor integrations matched automatically by make and model (for example, Dahua / Amcrest smart events).
- Groups for sites, buildings, and floors.

**Recording**
- One recorder node per machine (a Windows service, or a systemd service on Linux), with as many nodes as you need, on either OS or both. Recording is `ffmpeg -c copy`, so no re-encoding (except cameras with a privacy mask, which are re-encoded in their own codec and bitrate).
- Recording modes: Continuous, Motion, Schedule, Event, and Manual only, set globally or per camera, with pre/post-roll.
- Retention set globally, per node, and per camera, plus per-camera quotas and a disk watermark backstop. Footage left behind when a camera or storage path moves is still aged out and confirmed deleted.
- Evidence locks keep chosen footage from retention for days, until a date, or indefinitely, with a 24-hour grace period after removal and a full audit trail.
- An optional archive volume (SMB share or USB drive) that receives aged-out footage instead of deleting it.
- Recording failover to a backup node, and a maintenance mode.
- Self-updating nodes: each build is approved once, and then every node installs it.
- Nodes keep recording through a server or database outage, even across a reboot.
- Optional footage encryption on the recorders' disks (AES-256-GCM), per camera, for video or for video, snapshots, and thumbnails, with a passphrase-protected master key backup.
- Tamper evidence: every segment is fingerprinted and linked into a per-camera chain the server signs hourly, and every export gets a signed manifest you can check in the browser or with openssl.

**Detection & events**
- Server-side motion detection with polygon zones or a mask grid, with a live motion overlay synced to the video while you set them up.
- ONVIF event ingestion, with user-defined event tag rules and timeline colors.
- Built-in LarisVision AI detection (YOLOX, D-FINE, or your own ONNX model) on NVIDIA, AMD, Intel, or CPU, or an external HTTP inference service.
- Live bounding boxes synced to the video, object badges on live tiles, and a cropped snapshot for every detected object.
- Object tracking (ByteTrack), confidence scores, optional high-resolution snapshots, and a Slice mode that tiles wide or panoramic cameras so small objects are still detected. An object that passes behind a tree or post and comes out further along its line, with the same color, stays the same object.
- Movement judged from each object's path within a few frames, so swaying plants, box wobble, and parked cars don't count, with Low, Normal, or High sensitivity per camera and a choice of which kinds of object can count as moving.
- Object colors, including a person's shirt and pants colors, as badges on snapshots and as Snapshots filters.
- Speed traps: two lines across a road and the distance between them. Speeds show on live boxes, snapshots, and the Playback timeline, in a speed log report, and as a speed heatmap, with an optional speed limit.
- Privacy masks that black out areas in recordings, live view, snapshots, and exports, plus per-export redaction.
- Activity heatmaps of when events happen on a camera or across all cameras, from 24 hours to a year.
- Scene heatmaps of where objects moved, or where motion happened, laid over the camera's picture (or one you upload), for one camera or every camera in a view.

**Viewing**
- Saved camera-wall views with drag-and-drop layout, a fullscreen kiosk mode, and rotation, kept in folders: your Private views, and a folder for each role, with a right-click menu and drag and drop. Admins can let one role open another role's views.
- Adaptive live streaming that uses Sub streams for small tiles.
- Synchronized multi-camera playback on a zoomable timeline, with preview thumbnails and 1/32× to 32× speed. Switch any live tile to playback in place.
- Digital zoom on any live or playback tile: drag a box to zoom in, right-click to reset.
- Multi-camera exports, bookmarks, and a snapshots browser.
- An optional standalone player with exports, for handing footage to investigators, police, or attorneys: one zip that opens from disk in any browser, with a camera grid, a shared timeline, the events, and a check that nothing was changed. H.265 video can get an H.264 copy that plays everywhere.
- Live overlay switches for travel paths, speed, color, and confidence on each box.
- 🔴 and Ⓜ️ indicators on live tiles show when footage is being kept, and when a manual recording is on.
- Basic PTZ controls, and two-way audio: hold 🎙️ on a live tile to talk through the camera's speaker, with both directions recorded.
- A camera map on a street map or uploaded floorplans, showing where each camera points and what it sees.
- Video relayed through the server, through media proxies, or sent directly from node to browser.
- Phone-friendly layout, installable as an app from Chrome on Android or Safari on iOS, with pinch-to-zoom on timelines and in fullscreen. See [Using it on a phone](#-6-use-it-on-your-phone-optional).
- Dark mode.
- Keyboard and screen reader support across the app, including the timeline, PTZ, and drawing tools, and it follows your browser's font size, reduced motion, and high contrast settings.

**Administration**
- Roles, a permissions matrix, and per-camera/group access control, with auto-expiring role assignments and PTZ priority between roles.
- Sign-in with local accounts, Active Directory (LDAPS, with AD groups synced to roles), or Google (Gmail or Google Workspace accounts an admin links). Microsoft Entra ID sign-in is also available but hasn't been tested yet.
- Optional two-factor sign-in for local and Active Directory accounts.
- Alert rules that watch a camera or node, or fire on detected objects and speeds, sent by email (SMTP, Microsoft Graph, Gmail), webhook, ntfy, Pushover, Slack, and Teams.
- Personal notifications: people subscribe to shared rules or make their own (a camera, a node, or everything; certain hours), delivered by email or to the browser while LarisVMS is open (on-premises), through Web Push, or through Firebase Cloud Messaging.
- Audit log, system logs, scheduled database backups, and a customizable dashboard of widgets, each with a full report: per-camera fps, bitrate, and reconnects, per-node CPU, memory, network, fps, and detection counts, recent alerts, latest detections, and more.
- Manual recording from a live tile or the API, for one camera or a whole view, including cameras that otherwise only show live video.
- A REST API for monitoring, recording, PTZ, playback data, bookmarks, exports, evidence locks, and live video streams, with API keys that can each have different permissions.
- Home Assistant over MQTT (experimental): cameras appear in Home Assistant on their own, with motion, person, vehicle, and animal sensors, the latest detection's picture, a manual recording switch, and a bookmark button, limited to what a chosen role may see and do.
- A warning on the Nodes page, and a notification, when a node's AI detection falls back to a slower backend than the one asked for.
- Nightly database index and statistics upkeep, with no SQL Agent job needed.
- IP allow lists, an optional separate port for video traffic, and secrets encrypted at rest.
- Branding: app name, colors, font, and logo.
- Drop-in languages: translate one text file and upload it, or copy it into a folder, to show the interface in that language. Each person picks theirs, and alerts reach them in it.
- Ships in English, German, French, Italian, Portuguese (Brazil), Japanese, Korean, Chinese (Simplified and Traditional), and Spanish (Argentina). Apart from Spanish, these are machine-translated drafts, and corrections from native speakers are welcome (see [CONTRIBUTING.md](CONTRIBUTING.md#translations)).
- Built-in **Help** with documentation for every feature.

## 🏗️ Architecture

| Component | Runs as | Role |
|---|---|---|
| **LarisVMS Web** | Windows service or systemd service (Kestrel, HTTPS) | Web UI, REST API, and node control plane. Stores configuration in SQL Server or MariaDB. |
| **LarisVMS Node** | Windows service or systemd service, one per recording machine | Ingests RTSP, writes segments, enforces retention, runs motion detection, serves live and playback video. |
| **LarisVMS Vision Service** | Child process of a node (optional) | GPU/CPU object detection. It's isolated, so a driver fault never stops recording. |
| **LarisVMS Proxy** | Windows service or systemd service (optional) | Relays video between browsers and nodes, for remote sites or low-bandwidth links. |

Settings are inherited **global → node → camera**, and the most specific value wins.

**Mixed fleets.** Each part can run on either OS: a Windows server can manage Linux nodes and proxies,
and the other way round. The server keeps update builds for both platforms and offers each node and
proxy the one for its own OS.

## 📋 Requirements

- **Operating system:** for the server, recorder nodes, and proxies, either:
  - any 64-bit Windows version supported by [.NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), or
  - 64-bit (x86_64) Linux with systemd: Ubuntu 22.04, 24.04, or 26.04, Debian 12 or 13, or Rocky Linux 9 or 10. Other RHEL 9 or 10 rebuilds should work the same way as Rocky.
- **Server:** SQL Server (Express works) using SQL or Integrated authentication. A Linux server must use a SQL login. Building from source also needs the [.NET 10 SDK](https://dotnet.microsoft.com/download); the MSIs and Linux packages don't need any .NET runtime.
- **MariaDB:** MariaDB 10.11 or later can be used instead of SQL Server, with a MariaDB user and password. Built-in backups need the MariaDB client tools (`mariadb-dump`) on the LarisVMS server.
- **.NET version:** developed with the .NET SDK 10.0.404. The 0.223.0 installers were built with it and include the .NET 10.0.15 runtime (ASP.NET Core 10.0.15).
- **Each recorder node:** [FFmpeg](https://ffmpeg.org/). On Windows, `winget install ffmpeg --scope machine`. On Linux, the node package installs it from your distro (on Rocky, from RPM Fusion; see [Installing on Linux](#-installing-on-linux)).
- **LarisVision (optional):** a GPU and its driver, or CPU only. NVIDIA additionally needs CUDA 12.8 or newer 12.x and cuDNN 9.x if you want CUDA or TensorRT acceleration. Otherwise DirectML (Windows) and OpenVINO (Intel) work out of the box. See [GPU requirements](#gpu-requirements).
- **Cameras:** ONVIF Profile S or T.
- **Browser:** a current Chrome, Edge, Firefox, or Safari. HEVC playback depends on browser support. On phones, Chrome (Android) or Safari (iOS).

### 🖥️ Hardware

Windows 10 or later, Windows Server 2019 or later, or one of the Linux distributions above.

**Minimum** (physical or virtual):

- 2 CPU cores
- 8 GB RAM for the Web, Node, and database services all on one machine
- 12 GB RAM and 4 GB VRAM if the LarisVision service (optional) also runs on that machine

**Recommended** (6 cameras at ~4K with hardware-accelerated object detection):

- Windows 11, Windows Server 2022 or later, or Ubuntu 24.04 or later
- 4 CPU cores
- Separate machines for Web/database and Node/Vision
- 8 GB RAM for Web/database, and 8 GB RAM for Node/Vision
- 8 GB VRAM for the LarisVision service (optional)
- See the GPU chart below for minimum and equivalent hardware for LarisVision acceleration

AMD GPUs accelerate LarisVision on Windows only (DirectML). On Linux, an AMD machine runs LarisVision
on the CPU, though it can still use the GPU for video encoding.

| Generation / Tier | NVIDIA (CUDA / TensorRT) | AMD (DirectML, Windows) | Intel Arc (OpenVINO) | Intel iGPU (OpenVINO) | Typical VRAM / shared memory |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Baseline** | **RTX 2070** | *N/A (Turing era)* | *N/A* | *N/A* | **8 GB dedicated** |
| **Gen 1** | **RTX 3060** | **RX 6600 / 6600 XT** | **Arc A580 / A750** | **Intel UHD 770** (e.g. i5-12600K) | **8–12 GB** (system shared for iGPU) |
| **Gen 2** | **RTX 4060** | **RX 7600** | **Arc B560** | **Intel Arc Graphics** (Meteor Lake / Core Ultra) | **8 GB dedicated** / dynamic shared |
| **Gen 3** | **RTX 5050** | **RX 9050 / 9060** | *Future gen* | **Intel Arc Graphics** (Lunar Lake / Battlemage Xe2) | **8 GB dedicated** / unified system memory |

Without object detection, a node can easily handle 20 or more cameras.

Performance depends entirely on your hardware: how many cameras and LarisVision streams a node can handle comes down to its CPU, GPU, disks, and network.

## 🚀 Installing

Download the installers from the [Releases](https://github.com/billacuda/LarisVMS/releases) page:

| Windows | Linux (.deb / .rpm) | Install on |
|---|---|---|
| `LarisVMS-Web-<version>-x64.msi` | `larisvms-web` | The server. Self-contained, so no .NET runtime is needed. It also carries the Windows and Linux node and proxy builds for auto-update. |
| `LarisVMS-Node-<version>-x64.msi` | `larisvms-node` | Each recording machine. |
| `LarisVMS-Proxy-<version>-x64.msi` | `larisvms-proxy` | Optional relay machines, for remote sites or low-bandwidth links. |

The steps below are for Windows. For Linux, see [Installing on Linux](#-installing-on-linux), and for
containers, [Docker](#-docker).

Double-click an installer to be prompted for its settings, or pass them on the command line. Anything not given on the command line is asked for. In a silent install (`/qn`), a missing required value stops the install with a message naming the property.

### 🌐 1. Install the web server

```powershell
msiexec /i LarisVMS-Web-0.223.0-x64.msi
# silent, with a certificate:
msiexec /i LarisVMS-Web-0.223.0-x64.msi HTTPSPORT=8444 CERTPATH=C:\certs\vms.pfx CERTPASSWORD=secret /qn
```

| Property | Default | Purpose |
|---|---|---|
| `HTTPSPORT` | 8444 | HTTPS port, plus its firewall rule. |
| `CERTPATH`, `CERTPASSWORD` | blank | Server certificate (`.pfx`). Blank uses a self-signed certificate; a renewed file at the same path is picked up automatically. The installer checks that the file opens with the password before it continues. A file share must be readable by the service account. A certificate your devices trust is also needed to install LarisVMS as a phone app. |
| `SERVICEACCOUNT`, `SERVICEPASSWORD` | LocalSystem | Service account, for example one with SQL Integrated Security rights. Not needed for MariaDB, which always uses a MariaDB user and password. |
| `INSTALLFOLDER` | `C:\Program Files\LarisVMS\Web` | Install location. |

### 🧙 2. Run the setup wizard

Browse to `https://<server>:8444/`. The wizard sets up the database connection, the first admin account, and the branding, and shows the **node registration key**. You can find the key again later under **Nodes → Updates and registration**.

The wizard asks which database to use: **SQL Server** or **MariaDB**. A SQL login or MariaDB password is stored encrypted on the server (DPAPI, this machine only).

Pointing the wizard at an existing LarisVMS database, for example when reinstalling, uses that database as it is: it's upgraded if needed, nothing is seeded or overwritten, and the wizard's remaining steps are skipped. A database that isn't a LarisVMS one is refused. Once setup is complete, the wizard is closed.

### 📹 3. Install recorder nodes

Install [FFmpeg](https://ffmpeg.org/) on each recording machine first (`winget install ffmpeg --scope machine`), then:

```powershell
msiexec /i LarisVMS-Node-0.223.0-x64.msi SERVERURL=https://<server>:8444 REGISTRATIONKEY=<key> STORAGEROOT=D:\Recordings /qn
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

The node registers itself and appears under **Nodes**.

### 🔀 4. Install media proxies (optional)

```powershell
msiexec /i LarisVMS-Proxy-0.223.0-x64.msi SERVERURL=https://<server>:8444 REGISTRATIONKEY=<key> CLIENTPORT=4443 /qn
```

Properties: `SERVERURL`, `REGISTRATIONKEY`, `CLIENTPORT` (default 4443), `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`, `CLIENTALLOWINSECURE`, `INSECURETLS`, `SERVICEACCOUNT`, `SERVICEPASSWORD`, `INSTALLFOLDER`, with the same meanings as for the node.

### 📷 5. Add cameras

Use **Cameras → Discover**, or add a camera by its ONVIF device service URL. Then assign each camera to a recorder node. For everything else, open **Help** in the sidebar.

### 📱 6. Use it on your phone (optional)

Open the same address in your phone's browser. To install it as an app, choose **Install app** from the ⋮ menu in Chrome on Android, or **Share → Add to Home Screen** in Safari on iOS. Phones won't install a site that uses the self-signed certificate a fresh install starts with, so set `CERTPATH` to a certificate your devices trust first.

## 🐧 Installing on Linux

The `.deb` and `.rpm` packages install each part as a systemd service running as the `larisvms` user,
with the program in `/opt/larisvms`, settings in `/etc/larisvms`, data in `/var/lib/larisvms`, and logs
in `/var/log/larisvms`. They're self-contained, so no .NET runtime is needed. Installing opens the
service's ports in firewalld or ufw if one is active, and removing the package closes them again.

On **Rocky Linux** (and other RHEL rebuilds), the node needs ffmpeg from RPM Fusion. Enable it once
before installing a node:

```bash
sudo dnf install -y epel-release dnf-plugins-core
sudo crb enable
sudo dnf install -y "https://mirrors.rpmfusion.org/free/el/rpmfusion-free-release-$(rpm -E %rhel).noarch.rpm"
```

### 🌐 1. Install the web server

```bash
sudo apt install ./larisvms-web_0.223.0.*-1_amd64.deb      # Ubuntu, Debian
sudo dnf install ./larisvms-web-0.223.0.*-1.x86_64.rpm     # Rocky
```

The service starts straight away. Open `https://<server>:8444/` and run the
[setup wizard](#-2-run-the-setup-wizard), as on Windows. On Linux, SQL Server needs a SQL login
(Integrated authentication isn't available). The database password is stored encrypted with a key
derived from this machine's ID, in a file only the larisvms user can read, so a copy taken off the
machine is useless elsewhere.

The web server's own settings are in `/etc/larisvms/web.env`, for example a different port or your
own certificate (the larisvms user must be able to read it):

```bash
Kestrel__HttpsPort=8444
Kestrel__Certificates__Default__Path=/etc/larisvms/vms.pfx
Kestrel__Certificates__Default__Password=secret
```

Then run `sudo systemctl restart larisvms-web`. `appsettings.Production.json` and the wizard's
`setup-generated.json` are in `/etc/larisvms` too.

### 📹 2. Install recorder nodes

```bash
sudo apt install ./larisvms-node_0.223.0.*-1_amd64.deb     # or: sudo dnf install ./larisvms-node-0.223.0.*-1.x86_64.rpm
sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>
```

The **Add node** page on the server shows this command with the key and the server's certificate
thumbprint already filled in. `setup` writes `/etc/larisvms/node.env`, prepares the recordings
folder, opens the live port in the firewall, and starts the service, which registers and appears
under **Nodes**. `sudo larisvms-node setup --help` lists every option. The main ones:

| Option | Default | Purpose |
|---|---|---|
| `--storage-root` | `/var/lib/larisvms/recordings` | Where the node records. A network share must be mounted first (for example in `/etc/fstab`), writable by the larisvms user. |
| `--archive-root` | | Second folder that receives aged-out footage. |
| `--server-cert-thumbprint` | | Pins the server's certificate before registering. Without it, the first certificate the node sees is trusted from then on. |
| `--live-port`, `--live-allow-from` | 8554 | Live video port, and the addresses allowed to reach it. |
| `--client-port`, `--client-pfx-path`, `--client-endpoint-host` | | Lets browsers stream directly from this node. |
| `--ffmpeg-path` | from `PATH` | ffmpeg, if it's somewhere else. |

Run `setup` again with any option to change it; the service restarts with the new settings.
AI detection models go in `/var/lib/larisvms/models`.

### 🔀 3. Install media proxies (optional)

```bash
sudo apt install ./larisvms-proxy_0.223.0.*-1_amd64.deb    # or: sudo dnf install ./larisvms-proxy-0.223.0.*-1.x86_64.rpm
sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443
```

Options: `--server-cert-thumbprint`, `--client-port` (default 4443), `--client-pfx-path`,
`--client-pfx-password`, `--client-allow-insecure`, `--client-endpoint-host`, and `--reregister`, with
the same meanings as for the node.

### 🩺 Checking on a service

```bash
systemctl status larisvms-node            # or larisvms-web, larisvms-proxy
journalctl -u larisvms-node -f            # follow its output
ls /var/log/larisvms                      # the log files the Logs page reads
larisvms-node --version
```

## 🐳 Docker

Every release is also published as Docker images on GitHub's container registry, built from that
release's Linux packages:

| Image | Runs |
|---|---|
| `ghcr.io/billacuda/larisvms-web` | The web server. Bring your own database: MariaDB 10.11 or later, or SQL Server with a SQL login. |
| `ghcr.io/billacuda/larisvms-node` | A recorder node, with AI detection on an Intel GPU or the CPU. |
| `ghcr.io/billacuda/larisvms-node` `:<version>-cuda` | The same, plus CUDA 12 and cuDNN 9 for NVIDIA GPUs (about 1.3 GB larger). |
| `ghcr.io/billacuda/larisvms-node` `:<version>-tensorrt` | The same as `-cuda`, plus TensorRT 10 (larger again). |
| `ghcr.io/billacuda/larisvms-proxy` | A media proxy. |

Each is tagged with its version (`0.223.0`), its minor version (`0.223`), and `latest`; the node's GPU
images also have `cuda` and `tensorrt` for the newest release. `edge`, `edge-cuda`, and `edge-tensorrt`,
where present, are test builds made between releases.

### 🗄️ 1. Prepare a database

The images don't include one. For MariaDB on the same machine, make an empty database and an account for it
(`sudo mariadb`):

```sql
CREATE DATABASE larisvms CHARACTER SET utf8mb4;
CREATE USER 'larisvms'@'localhost' IDENTIFIED BY 'choose-a-password';
GRANT ALL PRIVILEGES ON larisvms.* TO 'larisvms'@'localhost';
```

The web container uses the host's network, so in the setup wizard the database's host is `127.0.0.1`. For
MariaDB on another machine, use that machine's address, and the Docker host's address instead of `localhost`
in the SQL.

### 🚀 2. Install

Choose one way. All of them use the same folders under `/srv/larisvms`; change those to suit. Docker makes
folders that don't exist, but mount a separate recordings disk first. Host networking doesn't open any
ports: if the machine has a firewall, allow TCP 8444 (web server), 8554 (node),
and 4443 (proxy), and UDP 8444 if you use HTTP/3. The containers run as user and group 8444, which owns their
folders on the host.

**Docker Compose.** [`docker/compose.yml`](docker/compose.yml) has the web server, with a node and a proxy
commented out, since their registration key doesn't exist until the setup wizard makes it:

```bash
mkdir larisvms && cd larisvms
curl -fsSLO https://raw.githubusercontent.com/billacuda/LarisVMS/main/docker/compose.yml
docker compose up -d
```

1. Open `https://<server>:8444/` and run the [setup wizard](#-2-run-the-setup-wizard). Its Node step shows
   the registration key.
2. In `compose.yml`, uncomment `larisvms-node` (and `larisvms-proxy`, if you want one), fill in the server's
   address, the key, and the recordings folder, then run `docker compose up -d` again.
3. Approve the node under **Nodes**. A proxy starts turned off: turn on **Enabled** for it under
   **Settings → Media proxies**.

**Portainer stack.** Under **Stacks → Add stack → Web editor**, paste the same
[`compose.yml`](docker/compose.yml) and choose **Deploy the stack**. Then follow the same three steps, changing
the stack in its **Editor** tab and choosing **Update the stack**. Use a standalone Docker environment: a Swarm
stack (`docker stack deploy`) ignores settings these files need.

**Plain Docker.** Start the web server:

```bash
docker run -d --name larisvms-web --network host --restart unless-stopped \
  -e Kestrel__HttpsPort=8444 \
  -v /srv/larisvms/web/data:/var/lib/larisvms \
  -v /srv/larisvms/web/config:/etc/larisvms \
  -v /srv/larisvms/web/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-web:latest
```

Run the [setup wizard](#-2-run-the-setup-wizard), then start a node with its key, and a proxy if you want one:

```bash
docker run -d --name larisvms-node --hostname larisvms-node-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_STORAGE_ROOT=/recordings \
  -v /srv/larisvms/node/data:/var/lib/larisvms \
  -v /srv/larisvms/node/config:/etc/larisvms \
  -v /srv/larisvms/node/logs:/var/log/larisvms \
  -v /srv/larisvms/recordings:/recordings \
  ghcr.io/billacuda/larisvms-node:latest

docker run -d --name larisvms-proxy --hostname larisvms-proxy-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_CLIENT_PORT=4443 \
  -v /srv/larisvms/proxy/data:/var/lib/larisvms \
  -v /srv/larisvms/proxy/config:/etc/larisvms \
  -v /srv/larisvms/proxy/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-proxy:latest
```

Add `--device /dev/dri` to the node for an Intel GPU or VAAPI, or `--gpus 'all,"capabilities=gpu,video"'`
with the `cuda` or `tensorrt` tag for an NVIDIA GPU.

For a node or proxy **on another machine**, use the plain Docker command, or
[`compose.node.yml`](docker/compose.node.yml) or [`compose.proxy.yml`](docker/compose.proxy.yml). The setup
wizard's Node step and the **Media proxies** page also show a stack with your server's address, registration
key, and certificate already filled in. [`compose.web.yml`](docker/compose.web.yml) is the web server alone.

### 🔧 3. Settings and updates

- **Server address.** Use the server's LAN address or name for `LARISVMS_SERVER_URL`, even on the same machine,
  not `localhost`: the server records the address a node connects from.
- **Host networking.** The stacks use `network_mode: host`: camera discovery (ONVIF multicast), the
  node's live port, and RTSP to the cameras all need the host's own network.
- **Settings are environment variables**, the same ones the Linux services use: `LARISVMS_SERVER_URL`,
  `LARISVMS_REGISTRATION_KEY`, `LARISVMS_SERVER_CERT_THUMBPRINT`, `LARISVMS_STORAGE_ROOT`,
  `LARISVMS_ARCHIVE_ROOT`, and `LARISVMS_CLIENT_PORT` (plus `LARISVMS_CLIENT_PFX_PATH`,
  `LARISVMS_CLIENT_PFX_PASSWORD`, and `LARISVMS_CLIENT_ENDPOINT_HOST`) for a node or proxy;
  `Kestrel__HttpsPort` and `Kestrel__Certificates__Default__Path` / `__Password` for the web server.
- **Volumes.** `/var/lib/larisvms` holds the keys, registration, models, and a machine ID that keeps
  saved passwords readable when the container is recreated: keep it on a host folder or named volume, a
  separate one for each container, and back it up. The web server's settings are in `/etc/larisvms`; logs are in `/var/log/larisvms`. Mount recordings
  from the host and point `LARISVMS_STORAGE_ROOT` at them.
- **GPUs.** For an Intel GPU (OpenVINO) or VAAPI video encoding, pass `/dev/dri` into the node. For
  NVIDIA, use the `-cuda` or `-tensorrt` image with the NVIDIA driver (570.26 or newer) and the
  [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/) on
  the host, and the GPU block in the compose file.
- **Updating.** With Compose, run `docker compose pull` and `docker compose up -d`. In Portainer, choose
  **Update the stack** with **Re-pull image and redeploy** on. With plain Docker, `docker pull` the image,
  `docker stop` and `docker rm` the container, and run its command again; the folders keep everything. A node
  or proxy in Docker isn't sent builds by the server; the Nodes page marks it **Docker** and says when a
  newer image is approved.

### 🚚 4. Moving a Linux install to Docker

A `.deb` or `.rpm` install moves with its database, settings, saved passwords, certificate, and node
registrations. What has to come along is the data folder, above all `keys`, and the machine's ID, which the
database password and node registrations are encrypted with. Without `keys`, the container can't read the
passwords and node keys in the database, and pages such as **Nodes** show an error. These steps use the folders
in [`compose.yml`](docker/compose.yml); run them **before the container's first start**, on the same machine, and
back up the database first. A Windows install can't move this way, because its keys only work on that
machine.

**Web server.** Copy its data, settings, and the machine ID:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Move any settings from `/etc/larisvms/web.env` into the stack's `environment`, since a container doesn't read
that file, then start the container. There's no wizard: it connects to the same database. Never run the old
service and the container at the same time.

**Node.** Copy its registration and models, without the recordings, which stay where they are:

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Move the settings from `/etc/larisvms/node.env` into the stack's `environment`. Mount each recordings and
archive folder **at the same path inside the container**, because the server knows the node's folders by path:

```yaml
    environment:
      LARISVMS_SERVER_URL: 'https://192.168.1.10:8444'
      LARISVMS_STORAGE_ROOT: '/var/lib/larisvms/recordings'
    volumes:
      - /srv/larisvms/node/data:/var/lib/larisvms
      - /srv/larisvms/node/config:/etc/larisvms
      - /srv/larisvms/node/logs:/var/log/larisvms
      - /var/lib/larisvms/recordings:/var/lib/larisvms/recordings
```

The node comes back as itself, with its cameras and recordings, and needs no registration key or approval. It
keeps the server address it registered with.

**Proxy.** The same as a node, without the recordings: copy `proxy.config`, `proxy-endpoint.*`, and the machine
ID into `/srv/larisvms/proxy/data`.

Once everything works, you can remove the old packages (`sudo apt remove larisvms-web larisvms-node`). That keeps
`/var/lib/larisvms`, but closes the ports in the firewall, so open them again for the containers.

**If you already ran the wizard in the container** against the old database, only the keys are missing. Copy
them in next to the container's own, and keep both:

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

## ⬆️ Upgrading

Run the new version's MSI. It finds the existing install and offers the upgrade straight away, without the settings pages: its settings are remembered, so no properties are needed. Secrets aren't stored, so a service running as a domain account asks for that password again. The web app applies database migrations itself when it starts. Upgrades never touch recordings, `appsettings.Production.json`, `setup-generated.json`, node registration, or the data-protection keys. If an install fails, it's rolled back, including any change it made to `appsettings.Production.json`.

An installation made with the PowerShell scripts is upgraded the same way: the MSI takes over the existing service and keeps its settings and registration, including a node's or proxy's direct-streaming endpoint and its firewall rule.

Recorder nodes and media proxies also **update themselves**. A new web install registers the node and proxy builds it carries as *Pending* in the **Builds** section of **Nodes**. Only the newest node build and the newest proxy build wait for approval; older ones are *Superseded*. Once you approve one, every older node downloads it, verifies its SHA-256, and installs it on its next check-in. You can turn this off under **Nodes → Updates and registration**.

Uninstalling removes the program, its service, and its firewall rules. Recordings, configuration, and everything under `%ProgramData%\LarisVMS` are kept.

**On Linux,** upgrade the web server with the new package (`sudo apt install ./larisvms-web_<version>-1_amd64.deb`
or `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`); it keeps `/etc/larisvms` and
`/var/lib/larisvms` and restarts the service. Nodes and proxies update themselves from the server the
same way as on Windows, once you approve the build: a small root helper (`larisvms-node-update.path`)
installs the update the service downloaded and checked. Upgrading a node's package by hand also works.
Removing a package with `apt remove` or `dnf remove` closes its firewall ports and keeps recordings,
settings, and the larisvms account, as on Windows. To remove everything, delete `/var/lib/larisvms`,
`/var/log/larisvms`, and `/etc/larisvms` by hand.

## 🧩 Installing from source

On Windows, the PowerShell scripts build from the repository and install on the same machine (for Linux, see [Building on Linux](#-building-on-linux)); they take the same settings as the MSIs (`-HttpsPort`, `-ServiceCredential`, `-ServerUrl`, `-RegistrationKey`, `-StorageRoot`, …).

```powershell
.\install-web.ps1                       # build, then install or upgrade the web server
.\build-node.ps1; .\build-proxy.ps1     # node and proxy packages in publish\, each with its install script
.\build-installers.ps1                  # build all three MSIs into publish\installers\
```


## 🧠 LarisVision AI detection

LarisVision is optional and per camera. A node without it still records, and still uses server
motion, camera events, and vendor integrations. The node package includes the CPU, DirectML
(Windows), OpenVINO, and CUDA backends, and picks one at startup:

| Hardware | Windows | Linux | Needed on the node |
|---|---|---|---|
| NVIDIA GPU | CUDA (falls back to DirectML) | CUDA (falls back to CPU) | CUDA 12 and cuDNN 9 (see [GPU requirements](#gpu-requirements)). The large CUDA provider library is downloaded from the server once. |
| Intel GPU (Arc or integrated) | OpenVINO (falls back to DirectML) | OpenVINO (falls back to CPU) | A current Intel graphics driver; on Linux, Intel's compute runtime (`intel-opencl-icd`) |
| AMD GPU | DirectML | CPU | A current GPU driver |
| No GPU | CPU | CPU | Nothing |

- **cuDNN** can be installed with
  `pip install --extra-index-url https://pypi.nvidia.com nvidia-cudnn-cu12`. Then point
  `Vision:CudnnPath` in the node's configuration at the package's `bin` folder (on Linux, set
  `Vision__CudnnPath=<folder>` in `/etc/larisvms/node.env`). On Linux, NVIDIA's apt or dnf
  repository is simpler: `sudo apt install cuda-libraries-12-8 libcudnn9-cuda-12` (or the same
  names with `dnf`). If the node still reports that it can't load CUDA, point `Vision__CudnnPath` at
  the folder holding the libraries. CUDA on Linux hasn't been tested on real hardware yet.
- **Intel GPUs on Debian 13:** Debian 13 has no `intel-opencl-icd` package. Install the one from
  Intel's releases, with the steps on the release's page:
  [26.27.39122.11](https://github.com/intel/compute-runtime/releases/tag/26.27.39122.11) for 11th-gen Core
  (Tiger Lake), Arc, and newer, or
  [24.35.30872.36](https://github.com/intel/compute-runtime/releases/tag/24.35.30872.36) for older
  graphics. The Docker node image already has both.
- **TensorRT 10.x** is optional, for extra speed. Enable it with `Vision:EnableTensorRt` and
  `Vision:TensorRtEngineCachePath`.
- **Video encoding** (only for cameras with a privacy mask, and export conversion) uses the GPU when
  it can: NVENC on NVIDIA, Quick Sync or AMF on Windows, and VAAPI on Linux, which needs the VAAPI
  driver (`intel-media-va-driver` or `mesa-va-drivers` on Ubuntu and Debian; `intel-media-driver` or
  `mesa-va-drivers` on Rocky).
- **Models are never bundled.** The built-in YOLOX models are downloaded from the server on first
  use. For D-FINE, export one with [`tools/export-models`](tools/export-models/). For a custom model,
  place any `.onnx` file in `C:\ProgramData\LarisVMS\models` (Windows) or `/var/lib/larisvms/models`
  (Linux) on the node, with an optional same-named `.json` file describing its decoder and labels.

### GPU requirements

The CUDA backend is ONNX Runtime 1.24.4, built with CUDA 12.8.

| Item | Linux x86_64 | Windows x64 | Notes |
|---|---|---|---|
| NVIDIA driver (minimum) | 570.26 | 570.65 | What CUDA 12.8 needs ([CUDA release notes](https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html)). The Nodes page warns when a node's driver is older. |
| CUDA | 12.8 or newer 12.x | 12.8 or newer 12.x | ONNX Runtime's CUDA 12 builds ([CUDA execution provider](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)). CUDA 13 isn't used yet. |
| cuDNN | 9.x | 9.x | cuDNN 8 doesn't work. |
| GPU | Turing (RTX 20xx, GTX 16xx) or newer | same | Current cuDNN 9 releases support Turing and newer only. Pascal (GTX 10xx) and Maxwell need cuDNN 9.10.2 or earlier, the last release that lists them ([cuDNN 9.10.2 support matrix](https://docs.nvidia.com/deeplearning/cudnn/backend/v9.10.2/reference/support-matrix.html)). |
| TensorRT (optional) | 10.x | 10.x | ONNX Runtime's TensorRT builds use TensorRT 10.9 with CUDA 12.0–12.8 ([TensorRT execution provider](https://onnxruntime.ai/docs/execution-providers/TensorRT-ExecutionProvider.html)). |
| Intel GPU | Intel compute runtime (Level Zero / OpenCL) | Current Intel graphics driver | Used through OpenVINO. |
| AMD GPU | Not used for detection | DirectML: Windows 10 1903 or later with a DirectX 12 GPU | |

**Frame rate:** 5–7 fps per camera is enough for accurate object detection and tracking. Cap
detection there (**Settings → AI detection → Max detection frame rate**, or per camera) rather than
sending every frame; higher rates add GPU load without improving results.
`build-node.ps1 -SkipVision` produces a recording-only node package.

**Tested hardware:** LarisVMS has been run in real environments on:
- **Windows:** Windows 11, Windows Server 2022, and a Windows Server 2022 guest VM on Hyper-V 2022 with RTX 2070 GPU passthrough
- **Linux:** a Linux VM running the web server (MariaDB) and a node with LarisVision on the CPU, alongside Windows nodes. Every release's packages are also installed on Ubuntu 22.04, 24.04, and 26.04, Debian 12 and 13, and Rocky Linux 9 and 10 in CI. GPUs on Linux haven't been tested yet.
- **Recording storage:** NVMe, Hyper-V virtual disks (on NVMe), a Windows Storage Pool passed through to a Hyper-V VM, and SMB file shares
- AMD Ryzen 7 5800X3D
- Intel Core i5-12600K, including LarisVision on its integrated GPU via DirectML
- NVIDIA GeForce RTX 2070: the main testing GPU and a dedicated recording node (DirectML, CUDA, and TensorRT FP32)
- NVIDIA GeForce RTX 4080 Super (DirectML, CUDA, and TensorRT FP16)

## 🔒 Security

- **Encrypted secrets.** Camera, SMB, node, and email credentials, and two-factor sign-in keys, are
  encrypted at rest with the ASP.NET Core data-protection keys in `%ProgramData%\LarisVMS\keys`
  (`/var/lib/larisvms/keys` on Linux). **Back this folder up.** Without it, encrypted values can't be
  recovered.
- **Connection string.** `setup-generated.json` in the install folder (`/etc/larisvms` on Linux)
  holds the database connection string. It's machine-specific, and must never be committed.
- **Network controls.** **Settings → Security** has IP allow lists for management and API traffic,
  and separately for video traffic. **Settings → Live view** can move video traffic to its own port.
- **Secret handling.** Secrets are write-only in the UI, and the audit log records that a secret
  changed but never its value.
- **Footage encryption.** Optional, per camera, under **Settings → Encryption and signing**. The
  master key is kept on the server and must be backed up to a passphrase-protected file before
  encryption can be turned on. **Keep that file and passphrase somewhere other than the server.**
  Without them, encrypted footage can't be recovered after the server is rebuilt.
- **Tamper evidence.** Recordings are fingerprinted and chained, and exports come with a signed
  manifest, so edited or missing footage can be detected.

## 🕶️ Privacy

LarisVMS is self-hosted and private by design. Your video, recordings, and settings stay on your own
machines, and you own them: nobody else can see them.

- **No telemetry, no calling home.** There's no analytics, usage tracking, crash reporting or
  account, and nothing about your install is collected or shared.
- **One routine outbound check.** Once a day the server asks GitHub whether a newer LarisVMS release
  is out, and shows a notice in the top bar when there is one. It's a plain request for the public
  releases list that sends nothing about your install. Turn it off under **Nodes → Updates and registration →
  New versions**.
- **One other download.** The first time AI detection uses a built-in YOLOX model, the server
  downloads it from the YOLOX project's GitHub releases. Place the model files on the server yourself
  to avoid it.
- **Everything else stays local.** Otherwise LarisVMS only talks to your cameras, your own server
  with its nodes and proxies, and services you set up yourself, such as email, alert channels, Entra ID
  or Google sign-in, and an MQTT broker for Home Assistant.

## 📚 Documentation

- **In-app Help:** the **Help** item in the sidebar documents every feature and setting, including
  troubleshooting, the [API](https://www.larisvms.com/docs/api/), [languages](https://www.larisvms.com/docs/languages/),
  and [accessibility](https://www.larisvms.com/docs/accessibility/). The same pages are on the
  [website](https://www.larisvms.com/docs/).
- **[CHANGELOG.md](CHANGELOG.md):** release notes. Older releases are in [`changelog-archive/`](changelog-archive/).

## 🔨 Building from source

Developed and built with the .NET SDK 10.0.404. `global.json` sets that as the minimum: it or any newer .NET 10 SDK builds LarisVMS, and the newest one installed is used. A newer major version (.NET 11) isn't picked up on its own, so a build doesn't change under you. Get it from the [.NET 10 downloads](https://dotnet.microsoft.com/download/dotnet/10.0); `dotnet --list-sdks` shows which you have.

```powershell
dotnet tool restore
dotnet build
dotnet test
```

Add a database migration with:

```powershell
.\tools\add-migration.ps1 <Name>
```

It adds the migration twice: once for SQL Server (`src\LarisVMS.Infrastructure\Migrations`) and once for
MariaDB (`src\LarisVMS.Infrastructure.MariaDb\Migrations`). Neither needs a database server. Raw SQL in
a migration (`migrationBuilder.Sql`) goes in the right provider's copy; `MariaDbModelTests` fails if
the MariaDB migrations fall behind the model.

Release installers are built with `.\build-installers.ps1` (WiX v5, restored from NuGet). It builds
the Linux `.deb` and `.rpm` packages too, which needs [nFPM](https://nfpm.goreleaser.com/) on `PATH`;
`-Platforms win-x64` or `-Platforms linux-x64` builds one platform only.

### 🐧 Building on Linux

Building, testing, and packaging also work on Linux (x86_64), which is how CI builds the Linux
packages. You need:

- the [.NET SDK 10.0.404](https://learn.microsoft.com/dotnet/core/install/linux) or a newer 10.0 SDK,
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-linux)
  (`pwsh`) for the build scripts (version checks, signing, and the language file checks),
- [nFPM](https://nfpm.goreleaser.com/install/) to package,
- and, to run what you built, the same packages the `.deb`/`.rpm` depend on: ffmpeg for a node, and
  libssl and zlib (ICU is bundled with the web server).

```bash
dotnet tool restore
dotnet build
dotnet test
pwsh ./build-installers.ps1 -Platforms linux-x64    # .deb and .rpm into publish/installers/
```

Without root, point the programs' data, log, and settings folders somewhere you can write with
`LARISVMS_DATA_DIR`, `LARISVMS_LOG_DIR`, and `LARISVMS_CONFIG_DIR` (the tests in CI do the same).
`install-web.ps1` is for Windows only; on Linux, install the package you built.

Release builds restore only the package versions and checksums recorded in the `packages.lock.json`
files. After changing a package in `Directory.Packages.props`, build once in Debug so the lock files
update, and commit them with the change.

**Publisher key.** `.\tools\publisher-key.ps1 -New -PrivateKeyPath <path outside the repo>` makes the
key that signs node and proxy updates (PowerShell 7). Commit the public key it writes to
`src\LarisVMS.Core\Security\publisher-key.pem`, and keep the encrypted private key and its password
offline. Pass `-PublisherKeyPath` to `install-web.ps1`, `build-installers.ps1`, `build-node.ps1`, or `build-proxy.ps1` to sign a
build: nodes and proxies built that way install only updates the publisher signed, so a compromised
server can't push its own code to them. It's optional: without `-PublisherKeyPath`, a build is
unsigned and carries no key, as before, so building from source never needs the private key.

A release needs the same version in every project's `<Version>` (Web, Node, NodeUpdater, Proxy,
Core), the same version and one-line release notes in `src\LarisVMS.Infrastructure\AppRelease.cs` (the
app records it in `AppVersions` at startup), and a matching `CHANGELOG.md` heading. `install-web.ps1`
and `build-installers.ps1` refuse to run if any of these disagree. Releases up to 0.218.0 used a
`BumpVersionX_Y_Z` migration instead.

### 🗂️ Project layout

```
src/
  LarisVMS.Core             Domain entities, enums, interfaces
  LarisVMS.Infrastructure   EF Core, identity, settings resolution, node control plane
  LarisVMS.Infrastructure.MariaDb  EF Core migrations for MariaDB
  LarisVMS.Onvif            ONVIF SOAP clients, WS-Discovery
  LarisVMS.Media            FFmpeg supervision, segment handling
  LarisVMS.Web              Web UI (Blazor static SSR + Razor Pages) and API
  LarisVMS.Node             Recorder node service
  LarisVMS.NodeUpdater      Swaps a node's binaries during auto-update
  LarisVMS.Vision           Detection capture, inference, and tracking (GPU/ONNX Runtime)
  LarisVMS.Vision.Service   Detection child process supervised by the node
  LarisVMS.Proxy            Media relay
tests/LarisVMS.Tests        xUnit tests
tools/export-models         Python: exports detection models to ONNX
```

## ⚠️ Known limitations

- Camera-side motion zones can be configured, but aren't active yet.
- Cameras with a privacy mask are re-encoded, which uses CPU or GPU time on their node.
- Speeds from speed traps are estimates. Their accuracy depends on the measured distance between the lines, the camera's angle, and its frame rate.
- Object colors are best-effort guesses. Complex scenes and lighting (shade, headlights, white balance) can give inaccurate colors, and none are recorded while a camera is in black-and-white night mode.
- Exported files aren't encrypted, even from cameras whose footage is.
- Bounding boxes from the camera's own analytics aren't shown; only LarisVision draws boxes.
- Face detection and object appeared/missing events come from the camera's own analytics. LarisVision doesn't detect these on its own yet.
- A moving object hidden for more than about 30 analysed frames (around 5 seconds at 6 fps), or one that comes back out going a different way, is counted as a new object. A parked one is remembered about ten times longer.
- Scene heatmaps use the camera's current framing. After a camera is moved or re-aimed, older heat no longer lines up with the picture until new history builds up.
- HTTP/3 needs Windows 11, Windows Server 2022, or later; older versions use HTTP/2. On Linux, HTTP/3 needs Microsoft's `libmsquic` package; without it, the web server uses HTTP/2.
- PTZ has not been tested against real PTZ hardware.
- Accessibility hasn't been independently audited, and the player has no captions or audio description, because cameras don't record them.
- Linux and the Docker images run on x86_64 only (no ARM yet), and Linux installs from downloaded packages (there's no apt or dnf repository yet, so the web server is upgraded by hand).
- The Docker images haven't been tested with an NVIDIA GPU yet.
- On Linux, AMD GPUs don't accelerate LarisVision (it runs on the CPU there), and NVIDIA and Intel GPU acceleration haven't been tested on real hardware yet.
- A Linux web server can't use Integrated authentication for SQL Server (use a SQL login), and Active Directory sign-in on Linux needs LDAPS and a service account.
- Microsoft Entra ID sign-in remains to be tested (there's no tenant to test it against), but should work. Local accounts, Active Directory (LDAPS), and Google sign-in are tested.
- Slack and Teams alert channels haven't been tested against real workspaces yet.
- Basic email works with SMTP. Graph API email hasn't been tested yet.
- Home Assistant support is experimental. [Feedback](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback) on what you use it for and what goes wrong is welcome.
- The standalone export player can't save a still frame: browsers block it for video opened from disk. Use a screenshot instead.
- Some browsers can't play H.265 in the standalone export player; ask for the H.264 copy when exporting.

## 📄 License

LarisVMS is licensed under the [Apache License 2.0](LICENSE). Third-party components and their
licenses are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt). FFmpeg is not bundled;
it runs as a separate process.

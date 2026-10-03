# Demo site

A throwaway LarisVMS with **9 fake ONVIF cameras** looping free stock footage, on a private virtual
network. Use it for screenshots and testing without real cameras, and without showing anything about
your own site.

| Piece | What it is |
|---|---|
| `setup-network.ps1` | Hyper-V internal switch `LarisDemo`: host `10.77.0.1`, cameras `10.77.0.11`–`.19`. Can't be reached from your LAN. |
| `FakeOnvif/` | .NET app that acts as the cameras in `cameras.json`: ONVIF device, media and events (PullPoint motion events), JPEG snapshots, and WS-Discovery replies. |
| MediaMTX | RTSP server (`rtsp://10.77.0.1x:554/camNN_main` and `_sub`) looping each camera's clip with `ffmpeg -c copy`. |
| `fetch-clips.ps1` | Downloads the clips and encodes camera-like Main (1080p25) and Sub (360p15) streams. |
| LarisVMS | Web from this repo's source (`dotnet run`). The recorder node and Vision Service are unpacked from the release MSI (see [Why the node comes from the MSI](#why-the-node-comes-from-the-msi)). |

Cameras (see `cameras.json`); every scene has people, vehicles or animals for AI detection:

| ID | Name | Scene | Notes |
|---|---|---|---|
| cam01 | Plaza Crosswalk | City crosswalk, pedestrians | |
| cam02 | Lobby | Building lobby, people walking | |
| cam03 | North Intersection | Busy intersection, top-down | |
| cam04 | Pasture | Cows grazing (fixed camera) | |
| cam05 | Warehouse Aisle 3 | Warehouse, people working | |
| cam06 | Main Gate | Street corner / intersection | |
| cam07 | Tram Stop | Tram line, high angle | Has audio (AAC) |
| cam08 | Avenue | Avenue at night, traffic lights | H.265 Main stream |
| cam09 | Penguin Beach | African penguins walking on a beach (fixed camera) | |

Clips are from [Pexels](https://www.pexels.com/license/), which allows free use with no attribution;
each camera's source page is in `cameras.json`. Everything downloaded or generated lives under
`dataRoot` (`G:\LarisDemo` by default), outside the repo. MediaMTX goes in `bin\`, which is gitignored.

## One-time setup

Needs Hyper-V, the .NET 10 SDK and ffmpeg (`winget install ffmpeg --scope machine`).

In an **elevated** PowerShell:

```powershell
.\tools\demo-site\setup-network.ps1
winget install Microsoft.SQLServer.2022.Express
```

The scripts talk to SQL Server through ODBC (`ODBC Driver 18 for SQL Server`, installed with SQL
Server), not `sqlcmd`. The current `sqlcmd` can't connect to a default SQL Express install, which has
only shared memory turned on.

Then in a normal PowerShell:

```powershell
.\tools\demo-site\fetch-tools.ps1     # MediaMTX + the release node, unpacked (nothing is installed)
.\tools\demo-site\fetch-clips.ps1     # ~200 MB download, a few minutes of encoding
```

For AI detection, put an ONNX model in `C:\ProgramData\LarisVMS\models` on this machine, or let the
built-in models download on first use. Models are never committed.

## First run

```powershell
.\tools\demo-site\start-demo.ps1 -NoNode
```

1. Open `https://localhost:8444/` and accept the self-signed certificate.
2. In the setup wizard, use SQL Server `.\SQLEXPRESS`, database `LarisVMS_Demo`, Integrated authentication. Create the admin user, keep the default branding, and copy the **node registration key**.
3. Start the node with that key. Only the first run needs it:

   ```powershell
   .\tools\demo-site\start-demo.ps1 -NodeOnly -RegistrationKey <key>
   ```

4. **Settings → Nodes**: open the node (it registers as this PC's hostname) and rename it, for example to `Recorder-01`.

After that, `.\tools\demo-site\start-demo.ps1` starts everything and `.\tools\demo-site\stop-demo.ps1`
stops it. Logs are in `G:\LarisDemo\logs\`.

## Staging the demo

1. **Cameras → Groups**: a site (for example "Riverside Campus"), with "Main Building" and "Grounds" under it.
2. **Cameras → Discover** finds the 9 cameras on `10.77.0.x`. Add them with username `admin` and password `demo`, and assign them to the node.
3. Turn on AI detection for the cameras you want boxes on. All 9 scenes have people, vehicles or animals. Use Continuous recording, with Motion recording on a couple of cameras.
4. Leave it running for **2–4 hours**, so the timeline, motion events, detections and snapshots fill up.
5. Add a saved camera wall, a bookmark, an export, and a second user with a restricted role.

## Screenshot privacy checklist

- [ ] Your real cameras answer Discover too if they're on this PC's LAN. **Unplug Ethernet or turn off Wi-Fi** while taking the Discover screenshot. The fake cameras only answer on the `LarisDemo` adapter.
- [ ] The node is renamed (not this PC's hostname).
- [ ] No email, alert or SSO settings are filled in.
- [ ] Use a clean browser profile (no bookmarks bar or extensions), and crop out the address bar.
- [ ] Optional: set Windows to UTC for the session if you don't want your time zone to show.

## Teardown

In an elevated PowerShell:

```powershell
.\tools\demo-site\teardown.ps1          # add -KeepClips to keep G:\LarisDemo\clips
```

This stops everything, drops `LarisVMS_Demo`, and deletes `G:\LarisDemo` and `C:\ProgramData\LarisVMS`
(only safe on a machine with no real LarisVMS install). It also removes the Web's `setup-generated.json`
and the demo network. SQL Server Express stays installed.

## Why the node comes from the MSI

`LarisVMS.Vision` needs `src\LarisVMS.Vision\Models\`, but the root `.gitignore` rule `models/`
(meant for downloaded `.onnx` files) also matches that source folder, because Git on Windows ignores
case. So the folder isn't in the repo, and `build-node.ps1` fails on a fresh clone. Until that's fixed,
`fetch-tools.ps1` unpacks the node from the release MSI with `msiexec /a`, which only extracts files.

## Testing a fake camera directly

```powershell
ffprobe -rtsp_transport tcp rtsp://admin:demo@10.77.0.11:554/cam01_main
curl.exe -X POST http://10.77.0.11/onvif/device_service -H "Content-Type: text/xml" `
  -d '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><GetDeviceInformation xmlns="http://www.onvif.org/ver10/device/wsdl"/></s:Body></s:Envelope>'
curl.exe -o snap.jpg http://10.77.0.13/snapshot.jpg
```

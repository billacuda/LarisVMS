# Overview

How LarisVMS fits together: the web server, recorder nodes, media proxies, and cameras.

## The pieces

| Piece | What it does |
|---|---|
| **Web server** | The LarisVMS web app. Stores configuration in SQL Server, serves the UI and API, and relays live and playback video unless direct streaming is on. Runs as the *LarisVMS Web* Windows service. |
| **Recorder node** | A Windows service on each recording machine. Pulls RTSP from its cameras, writes segments to its storage path, enforces retention, runs motion and AI detection, and reports health. One server can have many nodes. |
| **Vision service** | Optional child process of a node that runs AI object detection on the node's GPU (or CPU). A crash here never stops recording. |
| **Media proxy** | Optional relay between browsers and nodes, for remote sites or low-bandwidth links. |
| **Cameras** | ONVIF cameras. Each is assigned to one recorder node. |

## How video flows

1. A node opens an RTSP session to each of its cameras and records with `ffmpeg -c copy`, so no re-encoding happens.
2. Footage is written as short segment files (see [Recording](/Help/recording)) under the node's storage path.
3. Live view and playback are served by the node and relayed through this server, a media proxy, or straight to the browser (see [Nodes](/Help/nodes#direct-streaming)).

## Privacy

LarisVMS is self-hosted: your video and settings stay on your own machines, and nothing about your install is collected or shared. There's no telemetry or calling home. The only routine outbound request is a daily check of LarisVMS's GitHub releases for a new version (see [Updates](/Help/installation#updates)); it can be turned off. The other built-in download is a YOLOX AI model from that project's GitHub releases, the first time detection uses one, unless you place the files on the server yourself. Street maps on the [Map](/Help/live-and-playback#map) page load their map pictures in your browser from OpenStreetMap, or a tile server you choose; floorplans don't. Everything else only goes to your cameras, your own servers, and services you set up, such as email and alert channels.

## Settings inheritance

Most settings exist at three levels. The most specific one wins:

1. **Camera**: on the camera's own edit page.
2. **Node**: on the node's edit page under **Nodes**.
3. **Global**: on the matching **Settings** page.

A blank field (or an “inherit — …” option) uses the next level up, and shows the value it currently inherits.

## Dashboard

The dashboard is made of widgets. Each user picks their own, and the layout is saved to their account, so it follows them to other browsers.

- **Customize** (top right; hidden when the window is too narrow for the full grid) unlocks the layout. Drag a widget by its title bar, resize it from the corner, or remove it with ×. **Add widget** lists everything you can add, and **Reset layout** goes back to the default. Click **Done** when you're finished.
- Most widgets are top-10 lists of cameras or nodes, such as storage used, frame rate, bandwidth, oldest footage, offline cameras, time since last recording, and detections. Some have a time window (1h, 24h, 7d, 30d), and some can switch between highest and lowest first.
- Click a widget's title to open its full report: every camera or node, 100 per page, with a filter box, a node filter, sortable columns and a CSV download.
- **Camera activity** shows one camera's last 7 days as a heatmap by time of day; pick the camera in its title bar, and click the title for the full [Activity](/Help/live-and-playback#activity) page.
- **Latest detections** shows the newest snapshots. Its ⚙ button chooses which detection types to include; plain motion (nothing identified) is off unless you tick it.
- **Time since last recording** flags a camera set to Continuous recording that hasn't recorded for more than 5 minutes.
- Node widgets based on cameras (camera counts, offline cameras, frame rate, storage time left) leave out nodes that have no cameras.
- You only see cameras you have access to. **Recent alerts** is only offered to users who can manage alert rules.

Widgets refresh every minute.

## On a phone

The whole site works in a phone browser. The sidebar opens from the ☰ button at the top left.

To install it as an app, open the site in Chrome on Android and choose **Install app** from the ⋮ menu. On iOS, open it in Safari and choose **Share → Add to Home Screen**. The app opens full screen, without the browser's address bar.

Installing needs a certificate the phone trusts. Browsers won't install a site using the self-signed certificate a fresh install starts with; see [Certificates](/Help/security#certificates).

## Where to start

- New install: [Installation](/Help/installation)
- Add cameras: [Cameras](/Help/cameras)
- Choose what to keep: [Recording](/Help/recording) and [Storage & retention](/Help/storage)
- Something wrong: [Troubleshooting](/Help/troubleshooting)

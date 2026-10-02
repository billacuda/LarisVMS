# Overview

How LarisVMS fits together: the web server, recorder nodes, media proxies and cameras.

## The pieces

| Piece | What it does |
|---|---|
| **Web server** | This site. Stores configuration in SQL Server, serves the UI and API, and relays live and playback video unless direct streaming is on. Runs as the *LarisVMS Web* Windows service. |
| **Recorder node** | A Windows service on each recording machine. Pulls RTSP from its cameras, writes segments to its storage path, enforces retention, runs motion and AI detection, and reports health. One server can have many nodes. |
| **Vision service** | Optional child process of a node that runs AI object detection on the node's GPU (or CPU). A crash here never stops recording. |
| **Media proxy** | Optional relay between browsers and nodes, for remote sites or low-bandwidth links. |
| **Cameras** | ONVIF cameras. Each is assigned to one recorder node. |

## How video flows

1. A node opens an RTSP session to each of its cameras and records with `ffmpeg -c copy`, so no re-encoding happens.
2. Footage is written as short segment files (see [Recording](/Help/recording)) under the node's storage path.
3. Live view and playback are served by the node and relayed through this server, a media proxy, or straight to the browser (see [Nodes](/Help/nodes#direct-streaming)).

## Settings inheritance

Most settings exist at three levels. The most specific one wins:

1. **Camera**: on the camera's own edit page.
2. **Node**: on the node's edit page under **Settings → Nodes**.
3. **Global**: on the matching **Settings** page.

A blank field (or an “inherit — …” option) uses the next level up, and shows the value it currently inherits.

## Where to start

- New install: [Installation](/Help/installation)
- Add cameras: [Cameras](/Help/cameras)
- Choose what to keep: [Recording](/Help/recording) and [Storage & retention](/Help/storage)
- Something wrong: [Troubleshooting](/Help/troubleshooting)

# Cameras

Adding cameras, streams, groups, multi-lens devices and vendor integrations.

## Adding a camera

Use **Cameras → Discover** to scan the LAN for ONVIF devices, or **Cameras → Add** to enter a device service URL (for example `http://192.168.1.50/onvif/device_service`) and credentials. LarisVMS then **probes** the camera: it reads its ONVIF capabilities and streams and picks the Main and Sub streams.

Assign a **recorder node**, or the camera won't record.

## Re-probing

**Re-probe** on a camera's page reads its capabilities and streams again. Do this after changing the camera's resolution, codec or firmware. Changing the device URL re-probes automatically on save. **Settings → Camera settings** can re-probe every camera once a day. Cameras are probed one at a time, so cheap cameras don't drop their recording sessions. Stream names and enabled flags are kept across a re-probe.

## Streams

Each camera usually has a **Main** stream (full resolution, used for recording) and a **Sub** stream (lower resolution, used for small live tiles and AI detection). Expand **Device** on the camera's page to rename or disable a stream. Hover a stream to see its RTSP URL.

## Groups

Groups organize cameras into sites, buildings and floors. A **top-level group is a site**, and groups under it are buildings or floors. A camera can be in any number of groups, as long as they all belong to the same site. Groups are used for filtering, for settings overrides, and for [camera access](/Help/users#camera-access). The built-in *All cameras* group always contains every camera.

## Multi-channel

Some devices have several lenses in one housing. When a probe finds more than one video source, the camera's page offers **Split into N cameras**. Each lens then becomes its own camera with its own recording and retention. The existing camera becomes channel 1 and keeps its recordings.

## Integrations

Some features aren't available over ONVIF. Integrations (listed under **Settings → Plugins**) are matched automatically from the camera's make and model during a probe. There is nothing to switch on. For example, the **Dahua / Amcrest smart events** integration reads person and vehicle detections from the camera's own analytics. The camera's **Device** panel shows when an integration is active.

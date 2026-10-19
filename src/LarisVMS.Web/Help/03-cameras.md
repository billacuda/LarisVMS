# Cameras

Adding cameras, the camera list, streams, device settings, groups, multi-lens devices, and vendor integrations.

## Adding a camera

Use **Cameras → Discover** to scan the LAN for ONVIF devices, or **Cameras → Add** to enter a device service URL (for example `http://192.168.1.50/onvif/device_service`) and credentials. LarisVMS then **probes** the camera: it reads its ONVIF capabilities and streams and picks the Main and Sub streams.

Assign a **recorder node**, or the camera won't record.

## The camera list

**Cameras** lists every camera you can see. The row under the column headings filters by any column, such as Make, Model, Node, or Group, and the search box above the table filters by text anywhere in a row. Filters combine, and **Clear filters** resets them. **Columns** chooses which columns are shown. A filter on a hidden column is ignored.

Your filters, columns, sort order, and page size are saved to your account, so they come back when you sign in again, on any device.

Tick cameras to act on several at once: **Reassign selected to node**, or **Device settings…** for cameras of the same make and model (see [Bulk changes](#bulk-changes)). **Select all** only selects the cameras that match the current filters.

## Re-probing

**Re-probe** on a camera's page reads its capabilities and streams again. Do this after changing the camera's resolution, codec, or firmware on its own web page. Changes made in [Device settings](#device-settings) re-probe automatically. Changing the device URL re-probes automatically on save. **Settings → Camera settings** can re-probe every camera once a day. Cameras are probed one at a time, so cheap cameras don't drop their recording sessions. Stream names and enabled flags are kept across a re-probe.

## Streams

Each camera usually has a **Main** stream (full resolution, used for recording) and a **Sub** stream (lower resolution, used for small live tiles and AI detection). Expand **Device** on the camera's page to rename or disable a stream. Hover a stream to see its RTSP URL.

## Device settings

**Device settings** on a camera's page changes the camera's own settings over ONVIF, so you don't have to sign in to the camera's web page. Settings are read live from the camera, and only the ones it supports are shown.

| Section | Settings |
|---|---|
| **Video streams** | For each stream: codec (H.264, H.265, or MJPEG), encoding profile, resolution, frame rate, bitrate mode (CBR or VBR), bitrate, quality, and keyframe interval |
| **Image** | Brightness, contrast, saturation, and sharpness; day/night mode; WDR; backlight compensation; exposure mode; and white balance |
| **Audio** | Codec, bitrate, and sample rate |

Each section has its own **Apply** button, and only the settings you changed are sent. Applying a stream or audio change makes the stream reconnect, which leaves a gap of a few seconds in recording; LarisVMS then re-probes the camera and the recorder node picks up the new format right away. Image changes take effect without reconnecting.

Some cameras share one set of encoder settings between streams. The page says so, because changing one changes the other.

H.265 and the CBR/VBR choice need a camera with ONVIF Profile T (Media2). On older cameras those options don't appear.

## Bulk changes

To change several cameras of the same make and model at once:

1. On **Cameras**, filter the **Model** column, then tick the cameras (or **Select all**).
2. Click **Device settings…**. It's only available when every ticked camera is the same make and model.
3. Change what you need, and click **Apply** in that section.

You can also start from one camera's **Device settings** page and choose other cameras of the same model under **Apply to**. A *firmware differs* badge marks cameras on different firmware, which may offer slightly different options.

The page shows the first camera's current settings. Only the settings you change are sent to each camera, so the rest of each camera's own settings stay as they are. Streams are matched by role (Main, Sub, or Third). Each camera checks the change against its own options, and the results list shows, for each camera, whether it was applied, partly applied, skipped (for example, a camera with no Third stream), or failed (for example, offline). **Retry failed** sends the change again to the cameras that failed.

## Groups

Groups organize cameras into sites, buildings, and floors. A **top-level group is a site**, and groups under it are buildings or floors. A camera can be in any number of groups, as long as they all belong to the same site. Groups are used for filtering, for settings overrides, and for [camera access](/Help/users#camera-access). The built-in *All cameras* group always contains every camera.

## Multi-channel

Some devices have several lenses in one housing. When a probe finds more than one video source, the camera's page offers **Split into N cameras**. Each lens then becomes its own camera with its own recording and retention. The existing camera becomes channel 1 and keeps its recordings.

## Integrations

Some features aren't available over ONVIF. Integrations (listed under **Settings → Plugins**) are matched automatically from the camera's make and model during a probe. There is nothing to switch on. For example, the **Dahua / Amcrest smart events** integration reads person and vehicle detections from the camera's own analytics. The camera's **Device** panel shows when an integration is active.

# Live view & playback

Views, live tiles, the playback timeline, exports, bookmarks, evidence locks, snapshots, activity, and the map.

## Views

A **view** is a saved camera layout. Build views under **Views** by dragging and resizing tiles. **Live** and **Playback** both show a view. On phones, the layout collapses to one or two columns. Views can run fullscreen in kiosk mode, or rotate through several views on a timer.

## Live tiles

- Double-click a tile for fullscreen. In fullscreen, scroll or pinch to zoom and drag to pan.
- Each tile with audio has its own mute and volume control. Tiles always start muted.
- **Adaptive streaming** (under **Settings → Live view**) shows small tiles the Sub stream and switches to Main when the tile is large or fullscreen. Each tile also has a manual Auto/HD/SD choice.
- Badges show motion and detected objects. **Settings → Events** sets their colors and corner.
- The toolbar's AI switches draw a box around each detected object: **Moving**, **Idle**, or both. **Confidence** adds each box's score, and **Speed** and **Color** (on by default) add its speed, once a speed trap has timed it, and its color. **Path** (with Moving on) draws the line each moving object has taken from the centre of its box, in the box's color. The line stays while the object keeps moving. Once it stops or leaves, the line stays for the **Path linger** time (**Settings → Live view**, 5 seconds by default), then dissolves from its start.
- PTZ cameras get a direction pad and zoom.

## Talking through a camera

Cameras with a speaker show a 🎙️ button on their live tile. Hold it (or focus it and hold Space) to talk, and release to stop. **▾** next to it picks the microphone: a headset, a webcam, or any other input the browser can use.

- Your browser asks for microphone access the first time. It only allows this when the site is opened over **HTTPS**; see [Certificates](/Help/security#certificates).
- Only one person can talk through a camera at a time.
- It needs the **Talk** permission on the camera (**Settings → Camera access**). The camera must support ONVIF two-way audio (an audio output on its ONVIF profile). Dahua, Amcrest, Lorex, and EmpireTech cameras whose firmware doesn't offer that fall back to the camera's own audio API.
- Everything said is recorded. The camera's own microphone is part of the video recording as usual, and each press of the button is saved alongside the footage. Presses show on the playback timeline in teal and play back in sync with the video, using the tile's volume. Exports include them, mixed with the camera's own audio. They're kept as long as the camera's footage.
- Each session is written to the audit log.

## Timeline

Playback has two timelines: one for the selected camera and one combined across all cameras. Both are colored by motion, detections, event tags, and two-way audio.

- Scroll to zoom (from weeks down to seconds), drag to pan, and click to jump every tile to that moment.
- Click the time above the timeline to type an exact time.
- Arrow keys step one second while paused; Shift+arrow steps ten.
- Speeds up to 8× play smoothly. Faster speeds step through the footage like a slideshow.
- Hover the timeline for a preview thumbnail.

Your position and zoom are remembered between visits.

## Exports

**Export…** in Playback creates a video file for each selected camera over a time range. Exports run in the background and appear on the **Exports** page, where they can be downloaded, retried, or deleted.

- **✏ Redact** next to a camera blacks out areas in that camera's export only, such as a neighbour's window or a passer-by. Draw rectangles (drag) or polygons (click each corner; click the first point, double-click, or press Enter to finish; right-click removes the last point) over a frame from the export's start time. The footage itself is unchanged.
- A redacted export is re-encoded, so it takes longer than a plain one. It's marked **Redacted** on the Exports page, and the audit log records that areas were blacked out.
- A camera's own [privacy masks](/Help/motion) are already part of its recordings, so every export includes them.
- Any two-way audio said through the camera during the range is mixed into the export's audio.

### Verify an export

Every recorded segment is fingerprinted (SHA-256) as it's written, and each camera's segments are linked into a chain: each link covers its segment's fingerprint and the link before it, so changing, removing, or reordering recorded footage breaks the chain. Once an hour the server signs where each camera's chain has got to.

Each finished export also gets a signed **🔏 Manifest** on the Exports page. It records the exported file's fingerprint, the source footage and its place in the chain, and whether every source file still matched its fingerprint when it was exported.

To check an export, open **Exports → Verify an export** and choose the manifest and the video. The check runs in your browser and nothing is uploaded. It confirms:

- the manifest was signed by this server and hasn't been edited;
- the video is exactly the exported file;
- the source footage hadn't changed since it was recorded, and links up to a signed checkpoint.

Keep the manifest with the video when you hand an export on. Anyone can check the signature without LarisVMS: save the manifest's `publicKeyPem` to `key.pem`, its `signedPayload` text to `payload.txt`, and its base64-decoded `signatureDer` to `sig.der`, then run `openssl dgst -sha256 -verify key.pem -signature sig.der payload.txt`. Footage recorded before this feature has no fingerprints, so only the export itself can be checked.

Signing is always on. The server's signing key, with a download of its public key, is under **Settings → Encryption and signing**.

## Bookmarks

Mark a moment during Playback with a note. **Bookmarks** lists them all, with a link back to the exact moment. Bookmarks are shared with everyone who can view playback, and expire when their footage ages out.

## Evidence locks

An evidence lock keeps footage from being deleted by retention. On Playback, click **🔒 Lock…**, pick the cameras and the start and end, choose how long to keep it (1, 3, 7, 14, or 30 days, until a date, or indefinitely), and give a reason. Locked stretches show as a violet band along the top of the timeline. One lock covers up to 7 days of footage.

- **Locked footage is never deleted**, not by retention, quotas, or archive expiry, and not even when the disk is full. If a recorder fills up with locked footage, its Nodes entry shows **full of locked footage**, and recording stops once the disk is full. End or remove locks, export the footage, or add storage.
- **Long locks**: anything over 30 days shows a warning. When storage is tight, it's better to export the video (**Export this range instead**) and keep it somewhere else.
- **Removing a lock**: **Evidence locks** lists every lock with its size and expiry. The person who placed a lock, or a Super Admin, can remove it with a reason. The footage stays protected for another **24 hours** in case the removal was a mistake, and **Undo removal** restores the lock until then.
- **Logged**: placing, removing, restoring, and expiring a lock are all written to the audit log.

A camera with footage still under a lock can't be deleted. Placing locks needs the **Lock footage** permission, which roles that can export have by default. [Notifications](/Help/alerts#notifications) can tell you when a lock is about to end or was removed.

## Snapshots

**Snapshots** lists every detection and event tag as a thumbnail, with filters for camera, group, view, object type, and time. Plain motion only appears on the timeline. ▶ opens the moment in Playback.

## Activity

**Activity** shows when things happen on a camera, or on all cameras together, as a heatmap by time of day. Pick the camera (or **All cameras**) and a range: 24 hours, 7, 30, or 90 days, or a year.

- **Typical week** (the default): rows are days of the week and columns are hours, totalled over the range, so a dark cell is a time that's regularly busy, such as Saturdays 6–7 PM. Over more than a week, hovering a cell also gives the average per day (for example, "8.8 per Saturday on average").
- **By day** (30 days and up): every date runs left to right with the hours top to bottom, and a strip of daily totals underneath, so you can pick out particular days.
- The busiest day of the range and the busiest hour are named above the chart.
- For the last 24 hours, there's a row for each type of event.
- **Event types** narrows it to what you care about: a whole type (Animal) or just some of its labels (Animal → cat). Plain motion is off by default so it doesn't drown out the objects. Your choice is remembered and stays when you switch cameras.
- Hover a cell for its exact count, or turn on **Show as table** for the numbers.
- Times are in the camera's time zone (set on the camera), or the server's if it has none. All cameras uses the server's time zone.
- Activity history is kept for 400 days (setting `Activity.HistoryDays`), even after the footage itself has been deleted, so the longer ranges show the whole period. History builds up from when this was installed: events already deleted along with their footage can't be recovered.
- The view is in the page address, so you can bookmark or share it. The dashboard's **Camera activity** widget shows the last 7 days for one camera or all of them.

### Scene

**Scene** shows *where* in a camera's picture things move: a heat layer over a recent full-size frame from the camera's recordings. Red is where the most happens, then orange, yellow, green, and blue for very little. Spots with nothing stay clear.

- **Objects moving** counts how many objects passed through each spot. Narrow it to **People**, **Vehicles**, **Animals**, or **Other**.
- **Motion** shows where the camera's server-side motion detection saw motion, for cameras using it in **Grid** mode.
- Pick a range, and optionally a **Time of day** (for example 10 PM to 6 AM for nights).
- The heat is added up once an hour (motion every 10 minutes) and kept as long as the rest of the activity history. The paths behind it are kept with their events, so they go when the footage does.
- Pick a camera, or a **view** (listed under **Views** in the camera list) to see a heatmap for each camera in it side by side. Each camera has its own scale, so a quiet camera still shows where its activity is. It can't be shown for all cameras at once.
- **Background**: if you can configure the camera, you can replace its frame with your own picture (a clear daytime shot, say) with **Use my own picture…**. It has to be from the same camera with the same framing, at its full width or the same shape, so the heat lines up. **Use the camera's picture** goes back to the automatic frame.

## Map

**Map** (under Live) shows where your cameras are and what they look at. Each camera is an icon with a cone for its view: the direction it points, how wide it sees, and how far. The colour shows its state: green is recording, red is offline, grey is disabled. Click a camera for its latest picture, with links to Live and Playback. Statuses refresh every 30 seconds. The map reopens where you left it, separately for each person and each map.

There are two kinds of map, and you can have as many of each as you like:

- **Street map**: a normal map of the area (OpenStreetMap by default). Range is in metres.
- **Floorplan**: a picture you upload (PNG, JPEG, WebP, or GIF, up to 10 MB), such as a building plan or a site drawing. Range is in pixels of that picture.

People who can edit cameras can set maps up:

1. **New map → Street map** or **Floorplan**. A new street map opens where the current one is showing, or on the whole world; pan and zoom to your site. When you press **Done editing**, the map is saved to open where it's showing for everyone; **Manage → Open this map here for everyone** does the same without editing. This works for floorplans too.
2. **Edit map**, then drag cameras from **Cameras not on this map** onto the map.
3. Click a camera to select it. Drag the icon to move it, and drag the white handle at the tip of its cone to aim it (direction and range). The side panel sets the field of view, or exact direction and range, and **Take off this map** removes it. Changes save as you make them.

A camera can be on several maps, such as a street map and its building's floorplan. People only see the cameras they're allowed to view.

Street map pictures are loaded by your browser from the tile server, so a street map needs internet access from the browser. For an offline site, or another map style, set your own https tile server under **Manage → Street map tiles**, and its attribution (most tile servers' terms require it to be shown on the map).

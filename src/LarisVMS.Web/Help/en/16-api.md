# API

Every command in the REST API, with what it does, what it needs, and an example of using it.

The examples use made-up values. Replace `https://vms.example.com` with your server's address, `lvk_EXAMPLEKEY` with your API key, and IDs like `00000000-0000-0000-0000-000000000001` with real ones.

## Getting started

1. Under **Admin → API keys**, select **Add key**. Choose a role, and copy the key when it's shown. You only see it once.
2. Send the key in an `X-Api-Key` header on every request.
3. Call `/api/v1/status` to check the key works and get your camera IDs.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

Each camera in the response has a `cameraId`, which the other commands use.

Only paths starting with `/api/v1` accept API keys. Other paths are internal to the web interface and may change without notice.

## Keys and permissions

You can have as many keys as you like, each with its own permissions. A key's access comes from three things:

- **Role:** the key has the permissions of the role it's bound to.
- **Limits:** tick **Limit this key** to allow only some of those permissions. A key can never do more than its role.
- **Camera access:** the role's camera access under **Settings → Camera access** decides which cameras the key can use. For example, the **Record** action must be on for a camera before a key can record it. To limit a key to certain cameras, bind it to a role with access to only those cameras.

Each command below lists the permission it needs. A limited key must have that permission ticked.

For example, these two keys could share a role with broad access:

| Key | Limits | Can do |
|---|---|---|
| Monitoring | View the camera health dashboard | Read `/api/v1/status`, and nothing else |
| Workshop button | View cameras, Start and stop manual recording | Start, stop, and read recordings |

Edit a key's name and limits under **Admin → API keys**. The key itself doesn't change, so anything using it keeps working. A key's role and expiry can't change; create a new key instead. **Revoke** stops a key immediately.

A limited key is never treated as an administrator, even if its role is **Super Admin**.

Actions by a key appear in the audit log as `API key: {name}`.

## Conventions

- Requests and responses are JSON, except where a command returns an image or a file.
- Times are UTC, in ISO 8601 format, such as `2026-11-06T14:30:00Z`.
- Field names in JSON are camelCase.
- **Settings → Security** applies here too. The management allow list covers the API. Images and downloads count as live view and playback traffic, so they follow that allow list and, if you've set one up, the separate port.

| Status | Meaning |
|---|---|
| `200` | It worked. |
| `400` | The request isn't valid, such as an end time before the start time. |
| `401` | The key is missing, wrong, revoked, or expired. |
| `403` | The key lacks the permission, or doesn't have access to a camera. |
| `404` | That item doesn't exist. |
| `409` | The camera can't do that right now, such as recording on a disabled camera, or moving a PTZ camera someone with higher priority is using. |
| `429` | Too many requests. Wait and try again. Check the `Retry-After` header. |
| `502`, `503`, `504` | The recorder node couldn't be reached or didn't answer in time. |

## Monitoring

### GET /health

Whether the server is up. Needs no key, for use by load balancers and uptime monitors.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

Every camera's health and every node's status.

**Permission:** View the camera health dashboard. Lists only cameras the key can view.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

```json
{
  "cameras": [
    {
      "cameraId": "00000000-0000-0000-0000-000000000001",
      "cameraName": "Workshop",
      "cameraEnabled": true,
      "nodeName": "Recorder-1",
      "nodeOnline": true,
      "fps": 15,
      "bitrateKbps": 4096,
      "healthFresh": true
    }
  ],
  "nodes": []
}
```

### GET /api/v1/cameras/motion-state

The IDs of cameras seeing motion right now.

**Permission:** View cameras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

Which cameras are seeing a detected object right now, and what it is.

**Permission:** View cameras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/detection-state
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "detections": [
      { "kind": "Human", "label": "Person", "emoji": "🧍", "colorHex": "#ff9f1c" }
    ]
  }
]
```

## Recording

Start and stop manual recording, which keeps footage whatever the camera's recording mode would do. A recording runs until something stops it, including through restarts and upgrades. See [Recording](/Help/recording#manual-recording).

Starting a camera that's already recording, or stopping one that isn't, is safe and changes nothing.

The start and stop commands return which cameras `changed`, which were `unchanged`, and which were `skipped` because they're disabled or don't exist.

### POST /api/v1/cameras/{cameraId}/recording/start

Starts recording one camera. Returns `409` if the camera is disabled or doesn't exist.

**Permission:** Start and stop manual recording, plus **Record** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/start
```

```json
{
  "changed": ["00000000-0000-0000-0000-000000000001"],
  "unchanged": [],
  "skipped": []
}
```

### POST /api/v1/cameras/{cameraId}/recording/stop

Stops recording one camera.

**Permission:** Start and stop manual recording, plus **Record** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

Starts recording several cameras. Returns `403`, and changes nothing, if the key lacks access to any of them.

**Permission:** Start and stop manual recording, plus **Record** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

Stops recording several cameras.

**Permission:** Start and stop manual recording, plus **Record** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

Which cameras are keeping footage right now. Cameras with nothing happening aren't listed.

`keeping` means footage is being saved at the moment, for any reason. `manual` means a manual recording is on. A camera that's offline with a manual recording on shows `manual` without `keeping`.

**Permission:** View cameras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/recording
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "keeping": true,
    "manual": true,
    "manualStartedUtc": "2026-11-06T14:30:00Z",
    "manualStartedBy": "API key: Workshop button"
  }
]
```

## Images

### GET /api/v1/cameras/{cameraId}/snapshot

A JPEG taken from the camera now. This opens a short connection to the camera, so use the latest thumbnail below if you're checking often.

**Permission:** Add, edit, and delete cameras, plus **Configure** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

An image from the camera's most recent saved footage. A camera with no saved footage has no thumbnail.

**Permission:** View cameras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

Starts the camera moving. Values run from `-1` to `1`, where `0` is no movement. The camera keeps moving until you call stop.

| Field | Meaning |
|---|---|
| `panX` | Left (negative) or right (positive). |
| `tiltY` | Down (negative) or up (positive). |
| `zoomX` | Out (negative) or in (positive). |

**Permission:** View cameras, plus **PTZ** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

Stops the camera moving.

**Permission:** View cameras, plus **PTZ** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## Playback data

### GET /api/v1/cameras/{cameraId}/timeline

A summary of a time range, split into evenly sized buckets: whether each one has footage or motion, and the colors of any events.

| Parameter | Meaning |
|---|---|
| `from`, `to` | The time range. Required. |
| `buckets` | How many buckets. Default 200. |

**Permission:** Play back recorded footage, plus **Playback** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/timeline?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z&buckets=24"
```

```json
[
  {
    "startUtc": "2026-11-06T00:00:00Z",
    "endUtc": "2026-11-06T01:00:00Z",
    "hasRecording": true,
    "hasMotion": false,
    "tagColorHex": "#e5383b",
    "tagColorHexes": ["#e5383b"]
  }
]
```

### GET /api/v1/cameras/{cameraId}/segments

The saved footage files in a time range.

| Parameter | Meaning |
|---|---|
| `from`, `to` | The time range. Required. |

**Permission:** Play back recorded footage, plus **Playback** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/segments?from=2026-11-06T14:00:00Z&to=2026-11-06T15:00:00Z"
```

```json
[
  {
    "id": 1234,
    "startUtc": "2026-11-06T14:30:00Z",
    "endUtc": "2026-11-06T14:31:00Z",
    "isArchived": false
  }
]
```

### GET /api/v1/cameras/{cameraId}/activity

When events tend to happen on this camera, by time of day.

| Parameter | Meaning |
|---|---|
| `range` | How far back, such as `7d` (the default) or `30d`. |
| `view` | `calendar` shows it by day, for 30 days or longer. |
| `sel` | An event type or label to include, such as `Animal` or `Animal:cat`. Repeat for several. |

**Permission:** Play back recorded footage, plus **Playback** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## Bookmarks

### GET /api/v1/cameras/{cameraId}/bookmarks

The bookmarks on a camera in a time range.

| Parameter | Meaning |
|---|---|
| `from`, `to` | The time range. Required. |

**Permission:** Play back recorded footage, plus **Playback** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/bookmarks?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z"
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000b",
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "cameraName": "Workshop",
    "timestampUtc": "2026-11-06T14:30:00Z",
    "note": "Print failed here",
    "createdByUserName": "API key: Workshop button",
    "createdAt": "2026-11-06T14:35:00Z"
  }
]
```

### POST /api/v1/bookmarks

Adds a bookmark at a moment in a camera's footage.

**Permission:** Play back recorded footage, and Create bookmarks on recorded footage, plus **Playback** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## Exports

An export saves footage from one or more cameras as a file for each camera. Exports run in the background: create one, check on it, and download each file when it's finished.

### POST /api/v1/exports

Starts an export. Limited to 20 requests a minute per key.

**Permission:** Create and download exports, plus **Export** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

Lists exports and their progress. Each export has `items`, one for each camera. An item's `id` is what you use to download or retry it.

**Permission:** Create and download exports.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/exports
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000e",
    "createdUtc": "2026-11-06T14:40:00Z",
    "requestedByUserName": "API key: Workshop button",
    "fromUtc": "2026-11-06T14:00:00Z",
    "toUtc": "2026-11-06T14:30:00Z",
    "status": "Completed",
    "items": [
      {
        "id": "00000000-0000-0000-0000-00000000000f",
        "cameraId": "00000000-0000-0000-0000-000000000001",
        "cameraName": "Workshop",
        "nodeName": "Recorder-1",
        "status": "Completed",
        "errorMessage": null,
        "outputSizeBytes": 104857600,
        "redacted": false
      }
    ]
  }
]
```

### GET /api/v1/exports/items/{exportItemId}/download

Downloads one finished export file.

**Permission:** Create and download exports, plus **Export** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

Tries one failed export item again.

**Permission:** Create and download exports.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

Deletes an export and its files. A key can delete exports it created. Only an administrator can delete other people's.

**Permission:** Create and download exports.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## Evidence locks

An evidence lock stops footage being deleted. See [Evidence locks](/Help/live-and-playback#evidence-locks).

### GET /api/v1/evidence-locks

Lists locks.

| Parameter | Meaning |
|---|---|
| `cameraId` | Only this camera. Repeat for several. |
| `fromUtc`, `toUtc` | Only locks on footage in this time range. |
| `includeInactive` | `true` includes locks that have ended. |

**Permission:** Play back recorded footage, plus **Playback** camera access.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

Locks footage on one or more cameras.

| Field | Meaning |
|---|---|
| `cameraIds` | The cameras. |
| `fromUtc`, `toUtc` | The footage to lock. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom`, or `indefinite`. |
| `customUntilUtc` | When a `custom` lock ends. |
| `reason` | Why it's locked. |

**Permission:** Play back recorded footage, and Lock footage so retention can't delete it, plus **Playback** camera access.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

Removes a lock. A reason is required.

**Permission:** Play back recorded footage, and the same rules as removing a lock in the web interface.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

Restores a removed lock during its grace period.

**Permission:** Play back recorded footage, and the same rules as restoring a lock in the web interface.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## Not in the API

These aren't available to API keys:

- Live video. Use the web interface, or the images above.
- Playing back footage directly. Use an export.
- Settings, users, roles, API keys, encryption, and backups.
- Camera setup, such as zones, schedules, and device settings.

# API

REST API 中的每条命令，包括它的作用、所需条件和使用示例。

示例使用的是虚构的值。请将 `https://vms.example.com` 替换为您服务器的地址，将 `lvk_EXAMPLEKEY` 替换为您的 API 密钥，并将 `00000000-0000-0000-0000-000000000001` 之类的 ID 替换为真实的 ID。

## 入门

1. 在**管理 → API 密钥**下，选择**添加密钥**。选择一个角色，并在密钥显示时复制它。您只能看到它一次。
2. 在每个请求的 `X-Api-Key` 标头中发送该密钥。
3. 调用 `/api/v1/status` 以检查密钥是否有效，并获取您的摄像头 ID。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

响应中的每台摄像头都有一个 `cameraId`，其他命令会用到它。

只有以 `/api/v1` 开头的路径接受 API 密钥。其他路径是 Web 界面的内部路径，可能会不经通知而更改。

## 密钥和权限

您可以拥有任意数量的密钥，每个都有自己的权限。密钥的访问权限来自三点：

- **角色**：密钥拥有其所绑定角色的权限。
- **限制**：勾选**限制此密钥**，仅允许其中一部分权限。密钥的权限永远不会超过其角色。
- **摄像头访问**：在**设置 → 摄像头访问**下角色的摄像头访问权限，决定了密钥可以使用哪些摄像头。例如，必须先为某台摄像头开启**录制**操作，密钥才能录制它。要将密钥限制在某些摄像头上，请将其绑定到只能访问这些摄像头的角色。

下面的每条命令都列出了它所需的权限。受限的密钥必须勾选了该权限。

例如，下面这两个密钥可以共用一个访问范围很广的角色：

| 密钥 | 限制 | 可以做什么 |
|---|---|---|
| 监控 | 查看摄像头健康状况仪表板 | 读取 `/api/v1/status`，除此之外什么也不能做 |
| 车间按钮 | 查看摄像头、开始和停止手动录制 | 开始、停止和读取录制 |

在**管理 → API 密钥**下编辑密钥的名称和限制。密钥本身不会改变，所以使用它的任何内容都会继续工作。密钥的角色和有效期无法更改；请改为创建一个新密钥。**吊销**会立即使密钥失效。

受限的密钥永远不会被视为管理员，即使它的角色是**超级管理员**。

密钥的操作在审计日志中显示为 `API key: {name}`。

## 约定

- 请求和响应均为 JSON，除非命令返回图像或文件。
- 时间为 UTC，采用 ISO 8601 格式，例如 `2026-11-06T14:30:00Z`。
- JSON 中的字段名为 camelCase。
- **设置 → 安全**在这里同样适用。管理允许列表涵盖 API。图像和下载算作实时查看和回放流量，因此它们遵循该允许列表，如果您设置了独立端口，也遵循该端口。

| 状态 | 含义 |
|---|---|
| `200` | 成功。 |
| `400` | 请求无效，例如结束时间早于开始时间。 |
| `401` | 密钥缺失、错误、已吊销或已过期。 |
| `403` | 密钥缺少权限，或无权访问某台摄像头。 |
| `404` | 该项目不存在。 |
| `409` | 摄像头此刻无法执行此操作，例如在已禁用的摄像头上录制，或移动正被更高优先级用户使用的云台摄像头。 |
| `429` | 请求过多。请稍候再试。检查 `Retry-After` 标头。 |
| `502`、`503`、`504` | 无法连接录制节点，或其没有及时响应。 |

## 监控

### GET /health

服务器是否在运行。无需密钥，供负载均衡器和运行时间监控使用。

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

每台摄像头的健康状况和每个节点的状态。

**权限**：查看摄像头健康状况仪表板。只列出密钥可以查看的摄像头。

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

此刻检测到移动的摄像头的 ID。

**权限**：查看摄像头。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

此刻哪些摄像头看到了被检测到的对象，以及它是什么。

**权限**：查看摄像头。

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

## 录制

开始和停止手动录制，无论摄像头的录制模式会如何处理，它都会保留录像。录制会一直持续，直到有人停止它，包括在重启和升级之后。参见[录制](/Help/recording#manual-recording)。

开始已经在录制的摄像头，或停止没有在录制的摄像头，都是安全的，不会改变任何内容。

开始和停止命令会返回哪些摄像头 `changed`（已更改）、哪些 `unchanged`（未更改），以及哪些因已禁用或不存在而被 `skipped`（跳过）。

### POST /api/v1/cameras/{cameraId}/recording/start

开始录制一台摄像头。如果摄像头已禁用或不存在，则返回 `409`。

**权限**：开始和停止手动录制，外加**录制**摄像头访问权限。

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

停止录制一台摄像头。

**权限**：开始和停止手动录制，外加**录制**摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

开始录制多台摄像头。如果密钥对其中任何一台没有访问权限，则返回 `403`，并且不更改任何内容。

**权限**：开始和停止手动录制，外加**录制**摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

停止录制多台摄像头。

**权限**：开始和停止手动录制，外加**录制**摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

此刻哪些摄像头正在保留录像。没有任何情况发生的摄像头不会被列出。

`keeping` 表示此刻录像正被保存，无论出于什么原因。`manual` 表示手动录制已开启。离线且手动录制已开启的摄像头显示为 `manual`，而没有 `keeping`。

**权限**：查看摄像头。

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

## 图像

### GET /api/v1/cameras/{cameraId}/snapshot

此刻从摄像头拍摄的 JPEG。这会向摄像头打开一个短暂的连接，因此如果您需要频繁检查，请使用下面的最新缩略图。

**权限**：添加、编辑和删除摄像头，外加**配置**摄像头访问权限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

来自摄像头最近保存的录像的一张图像。没有保存录像的摄像头没有缩略图。

**权限**：查看摄像头。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## 云台

### POST /api/v1/cameras/{cameraId}/ptz/move

开始移动摄像头。值的范围为 `-1` 到 `1`，其中 `0` 表示不动。摄像头会持续移动，直到您调用停止。

| 字段 | 含义 |
|---|---|
| `panX` | 向左（负）或向右（正）。 |
| `tiltY` | 向下（负）或向上（正）。 |
| `zoomX` | 缩小（负）或放大（正）。 |

**权限**：查看摄像头，外加 **PTZ** 摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

停止摄像头移动。

**权限**：查看摄像头，外加 **PTZ** 摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## 回放数据

### GET /api/v1/cameras/{cameraId}/timeline

某个时间范围的摘要，被分成大小相等的区间：每个区间是否有录像或移动，以及任何事件的颜色。

| 参数 | 含义 |
|---|---|
| `from`、`to` | 时间范围。必填。 |
| `buckets` | 区间数量。默认 200。 |

**权限**：回放已录制的录像，外加**回放**摄像头访问权限。

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

某个时间范围内保存的录像文件。

| 参数 | 含义 |
|---|---|
| `from`、`to` | 时间范围。必填。 |

**权限**：回放已录制的录像，外加**回放**摄像头访问权限。

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

此摄像头上的事件通常在一天中的什么时间发生。

| 参数 | 含义 |
|---|---|
| `range` | 回溯多久，例如 `7d`（默认）或 `30d`。 |
| `view` | `calendar` 按天显示，适用于 30 天或更长的范围。 |
| `sel` | 要包含的事件类型或标签，例如 `Animal` 或 `Animal:cat`。重复使用可指定多个。 |

**权限**：回放已录制的录像，外加**回放**摄像头访问权限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## 书签

### GET /api/v1/cameras/{cameraId}/bookmarks

某台摄像头在某个时间范围内的书签。

| 参数 | 含义 |
|---|---|
| `from`、`to` | 时间范围。必填。 |

**权限**：回放已录制的录像，外加**回放**摄像头访问权限。

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

在摄像头录像中的某一时刻添加书签。

**权限**：回放已录制的录像，以及在已录制的录像上创建书签，外加**回放**摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## 导出

导出会将一台或多台摄像头的录像分别保存为一个文件。导出在后台运行：创建一个，检查其进度，并在每个文件完成时下载它。

### POST /api/v1/exports

开始导出。每个密钥限制为每分钟 20 个请求。

**权限**：创建和下载导出，外加**导出**摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

列出导出及其进度。每个导出都有 `items`，每台摄像头一项。某一项的 `id` 用于下载或重试它。

**权限**：创建和下载导出。

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

下载一个已完成的导出文件。

**权限**：创建和下载导出，外加**导出**摄像头访问权限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

重试一个失败的导出项。

**权限**：创建和下载导出。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

删除一个导出及其文件。密钥可以删除它所创建的导出。只有管理员才能删除其他人的导出。

**权限**：创建和下载导出。

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## 证据锁定

证据锁定可防止录像被删除。参见[证据锁定](/Help/live-and-playback#evidence-locks)。

### GET /api/v1/evidence-locks

列出锁定。

| 参数 | 含义 |
|---|---|
| `cameraId` | 仅限此摄像头。重复使用可指定多个。 |
| `fromUtc`、`toUtc` | 仅限针对此时间范围内录像的锁定。 |
| `includeInactive` | `true` 会包含已结束的锁定。 |

**权限**：回放已录制的录像，外加**回放**摄像头访问权限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

锁定一台或多台摄像头的录像。

| 字段 | 含义 |
|---|---|
| `cameraIds` | 摄像头。 |
| `fromUtc`、`toUtc` | 要锁定的录像。 |
| `duration` | `1d`、`3d`、`7d`、`14d`、`30d`、`custom` 或 `indefinite`。 |
| `customUntilUtc` | `custom` 锁定的结束时间。 |
| `reason` | 锁定的原因。 |

**权限**：回放已录制的录像，以及锁定录像使保留策略无法删除它，外加**回放**摄像头访问权限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

移除一个锁定。需要提供原因。

**权限**：回放已录制的录像，以及与在 Web 界面中移除锁定相同的规则。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

在宽限期内恢复一个已移除的锁定。

**权限**：回放已录制的录像，以及与在 Web 界面中恢复锁定相同的规则。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## API 中没有的功能

API 密钥无法使用以下内容：

- 实时视频。请使用 Web 界面，或上面的图像命令。
- 直接回放录像。请使用导出。
- 设置、用户、角色、API 密钥、加密和备份。
- 摄像头设置，例如区域、计划和设备设置。

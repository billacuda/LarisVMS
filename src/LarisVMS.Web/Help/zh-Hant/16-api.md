# API

REST API 中的每條命令，包括它的作用、所需條件和使用範例。

範例使用的是虛構的值。請將 `https://vms.example.com` 取代為您伺服器的位址，將 `lvk_EXAMPLEKEY` 取代為您的 API 金鑰，並將 `00000000-0000-0000-0000-000000000001` 之類的 ID 取代為真實的 ID。

## 開始使用

1. 在**管理 → API 金鑰**下，選擇**新增金鑰**。選擇一個角色，並在金鑰顯示時複製它。您只能看到它一次。
2. 在每個要求的 `X-Api-Key` 標頭中傳送該金鑰。
3. 呼叫 `/api/v1/status` 以檢查金鑰是否有效，並取得您的攝影機 ID。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

回應中的每台攝影機都有一個 `cameraId`，其他命令會用到它。

只有以 `/api/v1` 開頭的路徑接受 API 金鑰。其他路徑是 Web 介面的內部路徑，可能會不經通知而變更。

## 金鑰和權限

您可以擁有任意數量的金鑰，每個都有自己的權限。金鑰的存取權限來自三點：

- **角色**：金鑰擁有其所綁定角色的權限。
- **限制**：勾選**限制此金鑰**，僅允許其中一部分權限。金鑰的權限永遠不會超過其角色。
- **攝影機存取**：**設定 → 攝影機存取**下角色的攝影機存取權限，決定了金鑰可以使用哪些攝影機。例如，必須先為某台攝影機開啟**錄影**動作，金鑰才能錄製它。要將金鑰限制在某些攝影機上，請將其綁定到只能存取這些攝影機的角色。

下面的每條命令都列出了它所需的權限。受限的金鑰必須勾選了該權限。

例如，下面這兩個金鑰可以共用一個存取範圍很廣的角色：

| 金鑰 | 限制 | 可以做什麼 |
|---|---|---|
| 監控 | 檢視攝影機健康狀況儀表板 | 讀取 `/api/v1/status`，除此之外什麼也不能做 |
| 車間按鈕 | 檢視攝影機、開始和停止手動錄影 | 開始、停止和讀取錄影 |

在**管理 → API 金鑰**下編輯金鑰的名稱和限制。金鑰本身不會改變，所以使用它的任何內容都會繼續運作。金鑰的角色和有效期無法變更；請改為建立一個新金鑰。**撤銷**會立即使金鑰失效。

受限的金鑰永遠不會被視為管理員，即使它的角色是**超級管理員**。

金鑰的動作在稽核記錄中顯示為 `API key: {name}`。

## 慣例

- 要求和回應均為 JSON，除非命令傳回圖像或檔案。
- 時間為 UTC，採用 ISO 8601 格式，例如 `2026-11-06T14:30:00Z`。
- JSON 中的欄位名稱為 camelCase。
- **設定 → 安全性**在這裡同樣適用。管理允許清單涵蓋 API。圖像和下載算作即時檢視和回放流量，因此它們遵循該允許清單，如果您設定了獨立連接埠，也遵循該連接埠。

| 狀態 | 含義 |
|---|---|
| `200` | 成功。 |
| `400` | 要求無效，例如結束時間早於開始時間。 |
| `401` | 金鑰缺失、錯誤、已撤銷或已過期。 |
| `403` | 金鑰缺少權限，或無權存取某台攝影機。 |
| `404` | 該項目不存在。 |
| `409` | 攝影機此刻無法執行此操作，例如在已停用的攝影機上錄影，或移動正被更高優先順序使用者使用的 PTZ 攝影機。 |
| `429` | 要求過多。請稍候再試。檢查 `Retry-After` 標頭。 |
| `502`、`503`、`504` | 無法連線錄影節點，或其沒有及時回應。 |

## 監控

### GET /health

伺服器是否在執行。無需金鑰，供負載平衡器和運作時間監控使用。

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

每台攝影機的健康狀況和每個節點的狀態。

**權限**：檢視攝影機健康狀況儀表板。只列出金鑰可以檢視的攝影機。

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

此刻偵測到移動的攝影機的 ID。

**權限**：檢視攝影機。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

此刻哪些攝影機看到了被偵測到的物件，以及它是什麼。

**權限**：檢視攝影機。

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

## 錄影

開始和停止手動錄影，無論攝影機的錄影模式會如何處理，它都會保留錄影。錄影會一直持續，直到有人停止它，包括在重新啟動和升級之後。請參閱[錄影](/Help/recording#manual-recording)。

開始已經在錄影的攝影機，或停止沒有在錄影的攝影機，都是安全的，不會改變任何內容。

開始和停止命令會傳回哪些攝影機 `changed`（已變更）、哪些 `unchanged`（未變更），以及哪些因已停用或不存在而被 `skipped`（略過）。

### POST /api/v1/cameras/{cameraId}/recording/start

開始錄製一台攝影機。如果攝影機已停用或不存在，則傳回 `409`。

**權限**：開始和停止手動錄影，外加**錄影**攝影機存取權限。

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

停止錄製一台攝影機。

**權限**：開始和停止手動錄影，外加**錄影**攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

開始錄製多台攝影機。如果金鑰對其中任何一台沒有存取權限，則傳回 `403`，並且不變更任何內容。

**權限**：開始和停止手動錄影，外加**錄影**攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

停止錄製多台攝影機。

**權限**：開始和停止手動錄影，外加**錄影**攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

此刻哪些攝影機正在保留錄影。沒有任何情況發生的攝影機不會被列出。

`keeping` 表示此刻錄影正被儲存，無論出於什麼原因。`manual` 表示手動錄影已開啟。離線且手動錄影已開啟的攝影機顯示為 `manual`，而沒有 `keeping`。

**權限**：檢視攝影機。

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

## 影像

### GET /api/v1/cameras/{cameraId}/snapshot

此刻從攝影機拍攝的 JPEG。這會向攝影機開啟一個短暫的連線，因此如果您需要頻繁檢查，請使用下面的最新縮圖。

**權限**：新增、編輯和刪除攝影機，外加**設定**攝影機存取權限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

來自攝影機最近儲存的錄影的一張影像。沒有儲存錄影的攝影機沒有縮圖。

**權限**：檢視攝影機。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

開始移動攝影機。值的範圍為 `-1` 到 `1`，其中 `0` 表示不動。攝影機會持續移動，直到您呼叫停止。

| 欄位 | 含義 |
|---|---|
| `panX` | 向左（負）或向右（正）。 |
| `tiltY` | 向下（負）或向上（正）。 |
| `zoomX` | 縮小（負）或放大（正）。 |

**權限**：檢視攝影機，外加 **PTZ** 攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

停止攝影機移動。

**權限**：檢視攝影機，外加 **PTZ** 攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## 回放資料

### GET /api/v1/cameras/{cameraId}/timeline

某個時間範圍的摘要，被分成大小相等的區間：每個區間是否有錄影或移動，以及任何事件的顏色。

| 參數 | 含義 |
|---|---|
| `from`、`to` | 時間範圍。必填。 |
| `buckets` | 區間數量。預設 200。 |

**權限**：回放已錄製的錄影，外加**回放**攝影機存取權限。

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

某個時間範圍內儲存的錄影檔案。

| 參數 | 含義 |
|---|---|
| `from`、`to` | 時間範圍。必填。 |

**權限**：回放已錄製的錄影，外加**回放**攝影機存取權限。

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

此攝影機上的事件通常在一天中的什麼時間發生。

| 參數 | 含義 |
|---|---|
| `range` | 回溯多久，例如 `7d`（預設）或 `30d`。 |
| `view` | `calendar` 依日期顯示，適用於 30 天或更長的範圍。 |
| `sel` | 要包含的事件類型或標籤，例如 `Animal` 或 `Animal:cat`。重複使用可指定多個。 |

**權限**：回放已錄製的錄影，外加**回放**攝影機存取權限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## 書籤

### GET /api/v1/cameras/{cameraId}/bookmarks

某台攝影機在某個時間範圍內的書籤。

| 參數 | 含義 |
|---|---|
| `from`、`to` | 時間範圍。必填。 |

**權限**：回放已錄製的錄影，外加**回放**攝影機存取權限。

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

在攝影機錄影中的某一時刻新增書籤。

**權限**：回放已錄製的錄影，以及在已錄製的錄影上建立書籤，外加**回放**攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## 匯出

匯出會將一台或多台攝影機的錄影分別儲存為一個檔案。匯出在背景執行：建立一個，檢查其進度，並在每個檔案完成時下載它。

### POST /api/v1/exports

開始匯出。每個金鑰限制為每分鐘 20 個要求。

**權限**：建立和下載匯出，外加**匯出**攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

列出匯出及其進度。每個匯出都有 `items`，每台攝影機一項。某一項的 `id` 用於下載或重試它。

**權限**：建立和下載匯出。

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

下載一個已完成的匯出檔案。

**權限**：建立和下載匯出，外加**匯出**攝影機存取權限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

重試一個失敗的匯出項目。

**權限**：建立和下載匯出。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

刪除一個匯出及其檔案。金鑰可以刪除它所建立的匯出。只有管理員才能刪除其他人的匯出。

**權限**：建立和下載匯出。

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## 證據鎖定

證據鎖定可防止錄影被刪除。請參閱[證據鎖定](/Help/live-and-playback#evidence-locks)。

### GET /api/v1/evidence-locks

列出鎖定。

| 參數 | 含義 |
|---|---|
| `cameraId` | 僅限此攝影機。重複使用可指定多個。 |
| `fromUtc`、`toUtc` | 僅限針對此時間範圍內錄影的鎖定。 |
| `includeInactive` | `true` 會包含已結束的鎖定。 |

**權限**：回放已錄製的錄影，外加**回放**攝影機存取權限。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

鎖定一台或多台攝影機的錄影。

| 欄位 | 含義 |
|---|---|
| `cameraIds` | 攝影機。 |
| `fromUtc`、`toUtc` | 要鎖定的錄影。 |
| `duration` | `1d`、`3d`、`7d`、`14d`、`30d`、`custom` 或 `indefinite`。 |
| `customUntilUtc` | `custom` 鎖定的結束時間。 |
| `reason` | 鎖定的原因。 |

**權限**：回放已錄製的錄影，以及鎖定錄影使保留原則無法刪除它，外加**回放**攝影機存取權限。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

移除一個鎖定。需要提供原因。

**權限**：回放已錄製的錄影，以及與在 Web 介面中移除鎖定相同的規則。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

在寬限期內還原一個已移除的鎖定。

**權限**：回放已錄製的錄影，以及與在 Web 介面中還原鎖定相同的規則。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## API 中沒有的功能

API 金鑰無法使用以下內容：

- 即時影像。請使用 Web 介面，或上面的圖像命令。
- 直接回放錄影。請使用匯出。
- 設定、使用者、角色、API 金鑰、加密和備份。
- 攝影機設定，例如區域、排程和裝置設定。

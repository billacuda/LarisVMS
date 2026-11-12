# API

REST API의 모든 명령에 대해 기능, 필요한 것, 사용 예를 설명합니다.

예제는 임의의 값을 사용합니다. `https://vms.example.com`은 서버의 주소로, `lvk_EXAMPLEKEY`는 본인의 API 키로, `00000000-0000-0000-0000-000000000001` 같은 ID는 실제 값으로 바꾸십시오.

## 시작하기

1. **관리 → API 키**에서 **키 추가**를 선택합니다. 역할을 선택하고 키가 표시되면 복사하십시오. 한 번만 볼 수 있습니다.
2. 모든 요청에서 키를 `X-Api-Key` 헤더에 담아 보냅니다.
3. `/api/v1/status`를 호출하여 키가 작동하는지 확인하고 카메라 ID를 가져옵니다.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

응답의 각 카메라에는 `cameraId`가 있으며 다른 명령에서 사용합니다.

API 키를 허용하는 것은 `/api/v1`로 시작하는 경로뿐입니다. 다른 경로는 웹 인터페이스 내부용이며 예고 없이 변경될 수 있습니다.

## 키와 권한

키는 원하는 만큼 만들 수 있으며 각각 고유한 권한을 가집니다. 키의 접근 권한은 세 가지로 결정됩니다.

- **역할:** 키는 연결된 역할의 권한을 가집니다.
- **제한:** **이 키 제한**을 선택하면 그 권한 중 일부만 허용합니다. 키는 역할보다 많은 일을 할 수 없습니다.
- **카메라 접근:** **설정 → 카메라 접근**의 역할 카메라 접근이 키가 사용할 수 있는 카메라를 결정합니다. 예를 들어 키로 카메라를 녹화하려면 해당 카메라에 **녹화** 작업이 켜져 있어야 합니다. 키를 특정 카메라로 제한하려면 그 카메라에만 접근할 수 있는 역할에 연결하십시오.

아래의 각 명령에는 필요한 권한이 나열되어 있습니다. 제한된 키는 그 권한이 선택되어 있어야 합니다.

예를 들어 다음 두 키는 넓은 접근 권한을 가진 같은 역할을 공유할 수 있습니다.

| 키 | 제한 | 할 수 있는 일 |
|---|---|---|
| 모니터링 | 카메라 상태 대시보드 보기 | `/api/v1/status` 읽기만 가능 |
| 작업장 버튼 | 카메라 보기, 수동 녹화 시작 및 중지 | 녹화 시작, 중지, 읽기 |

키의 이름과 제한은 **관리 → API 키**에서 편집합니다. 키 자체는 바뀌지 않으므로 이를 사용하는 것은 계속 작동합니다. 키의 역할과 만료는 변경할 수 없으니 대신 새 키를 만드십시오. **해지**하면 키가 즉시 중지됩니다.

제한된 키는 역할이 **Super Admin**이더라도 관리자로 취급되지 않습니다.

키에 의한 작업은 감사 로그에 `API key: {name}`으로 나타납니다.

## 규칙

- 요청과 응답은 이미지나 파일을 반환하는 명령을 제외하고 JSON입니다.
- 시간은 UTC이며 `2026-11-06T14:30:00Z`와 같은 ISO 8601 형식입니다.
- JSON의 필드 이름은 camelCase입니다.
- **설정 → 보안**은 여기에도 적용됩니다. 관리 허용 목록은 API를 포함합니다. 이미지와 다운로드는 라이브 보기 및 재생 트래픽으로 취급되므로 해당 허용 목록을 따르며, 별도 포트를 설정했다면 그 포트를 따릅니다.

| 상태 | 의미 |
|---|---|
| `200` | 성공했습니다. |
| `400` | 요청이 올바르지 않습니다. 예: 종료 시각이 시작 시각보다 앞섭니다. |
| `401` | 키가 없거나, 잘못되었거나, 해지되었거나, 만료되었습니다. |
| `403` | 키에 권한이 없거나 카메라에 대한 접근 권한이 없습니다. |
| `404` | 해당 항목이 없습니다. |
| `409` | 카메라가 지금은 그 작업을 할 수 없습니다. 예: 사용 안 함 카메라의 녹화나, 더 높은 우선순위의 사람이 사용 중인 PTZ 카메라 이동. |
| `429` | 요청이 너무 많습니다. 기다렸다가 다시 시도하십시오. `Retry-After` 헤더를 확인하십시오. |
| `502`, `503`, `504` | 녹화 노드에 연결할 수 없거나 제시간에 응답하지 않았습니다. |

## 모니터링

### GET /health

서버가 가동 중인지 반환합니다. 키가 필요 없으며 로드 밸런서와 가동 시간 모니터에서 사용하기 위한 것입니다.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

모든 카메라의 상태와 모든 노드의 상태입니다.

**권한:** 카메라 상태 대시보드 보기. 키가 볼 수 있는 카메라만 나열됩니다.

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

지금 모션을 감지하고 있는 카메라의 ID입니다.

**권한:** 카메라 보기.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

지금 감지된 객체를 보고 있는 카메라와 그 객체가 무엇인지입니다.

**권한:** 카메라 보기.

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

## 녹화

수동 녹화를 시작하고 중지합니다. 카메라의 녹화 모드가 무엇을 하든 영상을 보관합니다. 녹화는 다시 시작이나 업그레이드를 거치더라도 무언가 중지할 때까지 계속됩니다. [녹화](/Help/recording#manual-recording)를 참조하십시오.

이미 녹화 중인 카메라를 시작하거나 녹화 중이 아닌 카메라를 중지해도 안전하며 아무것도 바뀌지 않습니다.

시작 및 중지 명령은 어떤 카메라가 `changed`(변경됨), `unchanged`(변경 없음), `skipped`(사용 안 함이거나 존재하지 않아 건너뜀)인지 반환합니다.

### POST /api/v1/cameras/{cameraId}/recording/start

카메라 하나의 녹화를 시작합니다. 카메라가 사용 안 함이거나 존재하지 않으면 `409`를 반환합니다.

**권한:** 수동 녹화 시작 및 중지, 그리고 **녹화** 카메라 접근.

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

카메라 하나의 녹화를 중지합니다.

**권한:** 수동 녹화 시작 및 중지, 그리고 **녹화** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

여러 카메라의 녹화를 시작합니다. 키에 그중 하나라도 접근 권한이 없으면 `403`을 반환하고 아무것도 변경하지 않습니다.

**권한:** 수동 녹화 시작 및 중지, 그리고 **녹화** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

여러 카메라의 녹화를 중지합니다.

**권한:** 수동 녹화 시작 및 중지, 그리고 **녹화** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

지금 영상을 보관하고 있는 카메라입니다. 아무 일도 없는 카메라는 나열되지 않습니다.

`keeping`은 이유를 막론하고 지금 영상이 저장되고 있다는 뜻입니다. `manual`은 수동 녹화가 켜져 있다는 뜻입니다. 수동 녹화가 켜져 있는데 오프라인인 카메라는 `keeping` 없이 `manual`로 표시됩니다.

**권한:** 카메라 보기.

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

## 이미지

### GET /api/v1/cameras/{cameraId}/snapshot

지금 카메라에서 촬영한 JPEG입니다. 카메라에 짧은 연결을 열기 때문에, 자주 확인한다면 아래의 최신 썸네일을 사용하십시오.

**권한:** 카메라 추가, 편집, 삭제, 그리고 **구성** 카메라 접근.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

카메라의 가장 최근 저장된 영상에서 가져온 이미지입니다. 저장된 영상이 없는 카메라에는 썸네일이 없습니다.

**권한:** 카메라 보기.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

카메라 이동을 시작합니다. 값은 `-1`에서 `1`까지이며 `0`은 움직이지 않음입니다. 카메라는 중지를 호출할 때까지 계속 움직입니다.

| 필드 | 의미 |
|---|---|
| `panX` | 왼쪽(음수) 또는 오른쪽(양수). |
| `tiltY` | 아래(음수) 또는 위(양수). |
| `zoomX` | 축소(음수) 또는 확대(양수). |

**권한:** 카메라 보기, 그리고 **PTZ** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

카메라 이동을 중지합니다.

**권한:** 카메라 보기, 그리고 **PTZ** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## 재생 데이터

### GET /api/v1/cameras/{cameraId}/timeline

시간 범위를 같은 크기의 버킷으로 나눈 요약입니다. 각 버킷에 영상이나 모션이 있는지와 이벤트의 색상이 포함됩니다.

| 매개변수 | 의미 |
|---|---|
| `from`, `to` | 시간 범위. 필수입니다. |
| `buckets` | 버킷 수. 기본값은 200입니다. |

**권한:** 녹화된 영상 재생, 그리고 **재생** 카메라 접근.

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

시간 범위 내 저장된 영상 파일입니다.

| 매개변수 | 의미 |
|---|---|
| `from`, `to` | 시간 범위. 필수입니다. |

**권한:** 녹화된 영상 재생, 그리고 **재생** 카메라 접근.

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

이 카메라에서 이벤트가 시간대별로 언제 일어나기 쉬운지입니다.

| 매개변수 | 의미 |
|---|---|
| `range` | 얼마나 거슬러 올라갈지. 예: `7d`(기본값) 또는 `30d`. |
| `view` | `calendar`는 일별로 표시합니다(30일 이상). |
| `sel` | 포함할 이벤트 유형 또는 레이블. 예: `Animal` 또는 `Animal:cat`. 여러 개는 반복합니다. |

**권한:** 녹화된 영상 재생, 그리고 **재생** 카메라 접근.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## 북마크

### GET /api/v1/cameras/{cameraId}/bookmarks

시간 범위 내 카메라의 북마크입니다.

| 매개변수 | 의미 |
|---|---|
| `from`, `to` | 시간 범위. 필수입니다. |

**권한:** 녹화된 영상 재생, 그리고 **재생** 카메라 접근.

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

카메라 영상의 특정 순간에 북마크를 추가합니다.

**권한:** 녹화된 영상 재생, 녹화된 영상에 북마크 만들기, 그리고 **재생** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## 내보내기

내보내기는 하나 이상의 카메라 영상을 카메라마다 하나의 파일로 저장합니다. 내보내기는 백그라운드에서 실행됩니다. 하나를 만들고, 진행 상황을 확인하고, 완료되면 각 파일을 다운로드하십시오.

### POST /api/v1/exports

내보내기를 시작합니다. 키당 분당 20개 요청으로 제한됩니다.

**권한:** 내보내기 만들기 및 다운로드, 그리고 **내보내기** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

내보내기와 진행 상황을 나열합니다. 각 내보내기에는 카메라마다 하나씩 `items`가 있습니다. 항목의 `id`는 다운로드하거나 다시 시도할 때 사용합니다.

**권한:** 내보내기 만들기 및 다운로드.

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

완료된 내보내기 파일 하나를 다운로드합니다.

**권한:** 내보내기 만들기 및 다운로드, 그리고 **내보내기** 카메라 접근.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

실패한 내보내기 항목 하나를 다시 시도합니다.

**권한:** 내보내기 만들기 및 다운로드.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

내보내기와 그 파일을 삭제합니다. 키는 자신이 만든 내보내기를 삭제할 수 있습니다. 다른 사람의 것은 관리자만 삭제할 수 있습니다.

**권한:** 내보내기 만들기 및 다운로드.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## 증거 잠금

증거 잠금은 영상이 삭제되지 않게 합니다. [증거 잠금](/Help/live-and-playback#evidence-locks)을 참조하십시오.

### GET /api/v1/evidence-locks

잠금을 나열합니다.

| 매개변수 | 의미 |
|---|---|
| `cameraId` | 이 카메라만. 여러 개는 반복합니다. |
| `fromUtc`, `toUtc` | 이 시간 범위의 영상에 대한 잠금만. |
| `includeInactive` | `true`이면 끝난 잠금도 포함합니다. |

**권한:** 녹화된 영상 재생, 그리고 **재생** 카메라 접근.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

하나 이상의 카메라의 영상을 잠급니다.

| 필드 | 의미 |
|---|---|
| `cameraIds` | 카메라. |
| `fromUtc`, `toUtc` | 잠글 영상. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom` 또는 `indefinite`. |
| `customUntilUtc` | `custom` 잠금이 끝나는 시각. |
| `reason` | 잠그는 이유. |

**권한:** 녹화된 영상 재생, 보존 정책이 삭제하지 못하도록 영상 잠금, 그리고 **재생** 카메라 접근.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

잠금을 해제합니다. 사유가 필요합니다.

**권한:** 녹화된 영상 재생, 그리고 웹 인터페이스에서 잠금을 해제할 때와 같은 규칙.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

해제된 잠금을 유예 기간 중에 복원합니다.

**권한:** 녹화된 영상 재생, 그리고 웹 인터페이스에서 잠금을 복원할 때와 같은 규칙.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## API에 없는 것

다음은 API 키로 사용할 수 없습니다.

- 라이브 영상. 웹 인터페이스 또는 위의 이미지를 사용하십시오.
- 영상 직접 재생. 내보내기를 사용하십시오.
- 설정, 사용자, 역할, API 키, 암호화, 백업.
- 영역, 일정, 장치 설정 같은 카메라 설정.

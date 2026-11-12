# API

REST API のすべてのコマンドについて、その機能、必要なもの、使用例を説明します。

例では、架空の値を使用しています。`https://vms.example.com` はお使いのサーバーのアドレスに、`lvk_EXAMPLEKEY` はご自身の API キーに、`00000000-0000-0000-0000-000000000001` のような ID は実際のものに置き換えてください。

## はじめに

1. **管理 → API キー**で、**キーを追加**を選択します。ロールを選択し、キーが表示されたらコピーします。表示されるのは一度だけです。
2. すべてのリクエストで、キーを `X-Api-Key` ヘッダーに指定して送信します。
3. `/api/v1/status` を呼び出して、キーが機能することを確認し、カメラの ID を取得します。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

レスポンス内の各カメラには `cameraId` があり、ほかのコマンドで使用します。

API キーを受け付けるのは、`/api/v1` で始まるパスのみです。それ以外のパスは、ウェブインターフェースの内部用で、予告なく変更されることがあります。

## キーと権限

キーは、必要なだけ作成でき、それぞれ独自の権限を持ちます。キーのアクセス権は、次の 3 つで決まります。

- **ロール:** キーは、紐付けられたロールの権限を持ちます。
- **制限:** **このキーを制限**にチェックを入れると、それらの権限の一部のみを許可します。キーがロールの権限を超えることはありません。
- **カメラアクセス:** **設定 → カメラアクセス**のロールのカメラアクセスが、キーが使用できるカメラを決めます。たとえば、キーでカメラを録画するには、そのカメラで**録画**操作がオンになっている必要があります。キーを特定のカメラに限定するには、そのカメラにのみアクセスできるロールに紐付けてください。

以下の各コマンドには、必要な権限が記載されています。制限付きのキーは、その権限にチェックが入っている必要があります。

たとえば、次の 2 つのキーは、広いアクセス権を持つ同じロールを共有できます。

| キー | 制限 | できること |
|---|---|---|
| 監視 | カメラのヘルスダッシュボードを表示 | `/api/v1/status` の読み取りのみ |
| 作業場のボタン | カメラを表示、手動録画の開始と停止 | 録画の開始、停止、読み取り |

キーの名前と制限は、**管理 → API キー**で編集します。キー自体は変わらないため、それを使用しているものは引き続き機能します。キーのロールと有効期限は変更できません。代わりに新しいキーを作成してください。**失効**すると、キーはただちに停止します。

制限付きのキーは、ロールが **Super Admin** であっても、管理者として扱われることはありません。

キーによる操作は、監査ログに `API key: {name}` として表示されます。

## 規約

- リクエストとレスポンスは、画像やファイルを返すコマンドを除き、JSON です。
- 時刻は UTC で、`2026-11-06T14:30:00Z` のような ISO 8601 形式です。
- JSON のフィールド名は camelCase です。
- **設定 → セキュリティ**は、ここにも適用されます。管理用の許可リストは、API を対象とします。画像とダウンロードは、ライブビューと再生のトラフィックとして扱われるため、その許可リストに従い、別のポートを設定している場合はそのポートに従います。

| ステータス | 意味 |
|---|---|
| `200` | 成功しました。 |
| `400` | リクエストが無効です。たとえば、終了時刻が開始時刻より前です。 |
| `401` | キーがないか、誤っているか、失効しているか、期限切れです。 |
| `403` | キーに権限がないか、カメラへのアクセス権がありません。 |
| `404` | その項目は存在しません。 |
| `409` | カメラが現在それを実行できません。たとえば、無効なカメラでの録画や、より高い優先度の人が使用している PTZ カメラの移動です。 |
| `429` | リクエストが多すぎます。待ってから再試行してください。`Retry-After` ヘッダーを確認してください。 |
| `502`、`503`、`504` | 録画ノードに接続できないか、時間内に応答しませんでした。 |

## 監視

### GET /health

サーバーが稼働しているかどうかを返します。キーは不要で、ロードバランサーや稼働監視での使用を想定しています。

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

すべてのカメラの正常性と、すべてのノードの状態です。

**権限:** カメラのヘルスダッシュボードを表示。キーが表示できるカメラのみ一覧表示されます。

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

現在モーションを検出しているカメラの ID です。

**権限:** カメラを表示。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

現在、検出されたオブジェクトを捉えているカメラと、そのオブジェクトの内容です。

**権限:** カメラを表示。

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

## 録画

手動録画を開始および停止します。これは、カメラの録画モードの動作に関係なく、映像を保持します。録画は、再起動やアップグレードをまたいでも、何かが停止するまで続きます。[録画](/Help/recording#manual-recording)をご覧ください。

すでに録画中のカメラを開始したり、録画していないカメラを停止したりしても安全で、何も変更されません。

開始と停止のコマンドは、どのカメラが `changed`（変更された）、`unchanged`（変更なし）、`skipped`（無効または存在しないためスキップされた）だったかを返します。

### POST /api/v1/cameras/{cameraId}/recording/start

1 台のカメラの録画を開始します。カメラが無効または存在しない場合は `409` を返します。

**権限:** 手動録画の開始と停止、および**録画**カメラアクセス。

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

1 台のカメラの録画を停止します。

**権限:** 手動録画の開始と停止、および**録画**カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

複数のカメラの録画を開始します。そのうち 1 台でもキーにアクセス権がない場合は、`403` を返し、何も変更しません。

**権限:** 手動録画の開始と停止、および**録画**カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

複数のカメラの録画を停止します。

**権限:** 手動録画の開始と停止、および**録画**カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

現在、映像を保持しているカメラです。何も起きていないカメラは一覧表示されません。

`keeping` は、理由を問わず、現在映像が保存されていることを意味します。`manual` は、手動録画がオンであることを意味します。手動録画がオンでオフラインのカメラは、`keeping` なしで `manual` と表示されます。

**権限:** カメラを表示。

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

## 画像

### GET /api/v1/cameras/{cameraId}/snapshot

カメラから今取得した JPEG です。カメラへの短い接続が開かれるため、頻繁に確認する場合は、下の最新のサムネイルを使用してください。

**権限:** カメラの追加、編集、削除、および**設定**カメラアクセス。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

カメラの最新の保存済み映像からの画像です。保存された映像がないカメラには、サムネイルがありません。

**権限:** カメラを表示。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

カメラの移動を開始します。値は `-1` から `1` までで、`0` は動かないことを意味します。カメラは、停止を呼び出すまで動き続けます。

| フィールド | 意味 |
|---|---|
| `panX` | 左（負）または右（正）。 |
| `tiltY` | 下（負）または上（正）。 |
| `zoomX` | ズームアウト（負）またはズームイン（正）。 |

**権限:** カメラを表示、および **PTZ** カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

カメラの移動を停止します。

**権限:** カメラを表示、および **PTZ** カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## 再生データ

### GET /api/v1/cameras/{cameraId}/timeline

時間範囲を、均等なサイズのバケットに分割した概要です。各バケットに映像やモーションがあるかどうかと、イベントの色が含まれます。

| パラメーター | 意味 |
|---|---|
| `from`、`to` | 時間範囲。必須です。 |
| `buckets` | バケットの数。既定は 200 です。 |

**権限:** 録画された映像を再生、および**再生**カメラアクセス。

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

時間範囲内の、保存された映像ファイルです。

| パラメーター | 意味 |
|---|---|
| `from`、`to` | 時間範囲。必須です。 |

**権限:** 録画された映像を再生、および**再生**カメラアクセス。

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

このカメラで、イベントが時間帯ごとにいつ起きやすいかです。

| パラメーター | 意味 |
|---|---|
| `range` | どれだけ遡るか。たとえば `7d`（既定）や `30d`。 |
| `view` | `calendar` は、日別に表示します（30 日以上の場合）。 |
| `sel` | 含めるイベントの種別またはラベル。たとえば `Animal` や `Animal:cat`。複数指定するには繰り返します。 |

**権限:** 録画された映像を再生、および**再生**カメラアクセス。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## ブックマーク

### GET /api/v1/cameras/{cameraId}/bookmarks

時間範囲内の、カメラのブックマークです。

| パラメーター | 意味 |
|---|---|
| `from`、`to` | 時間範囲。必須です。 |

**権限:** 録画された映像を再生、および**再生**カメラアクセス。

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

カメラの映像内の特定の瞬間に、ブックマークを追加します。

**権限:** 録画された映像を再生、および録画された映像にブックマークを作成、さらに**再生**カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## エクスポート

エクスポートは、1 台以上のカメラの映像を、カメラごとのファイルとして保存します。エクスポートはバックグラウンドで実行されます。作成し、状況を確認し、完了したら各ファイルをダウンロードします。

### POST /api/v1/exports

エクスポートを開始します。キーごとに 1 分あたり 20 リクエストに制限されています。

**権限:** エクスポートの作成とダウンロード、および**エクスポート**カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

エクスポートとその進行状況を一覧表示します。各エクスポートには、カメラごとに 1 つの `items` があります。項目の `id` は、ダウンロードや再試行に使用します。

**権限:** エクスポートの作成とダウンロード。

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

完了したエクスポートファイルを 1 つダウンロードします。

**権限:** エクスポートの作成とダウンロード、および**エクスポート**カメラアクセス。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

失敗したエクスポート項目を 1 つ、もう一度試行します。

**権限:** エクスポートの作成とダウンロード。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

エクスポートとそのファイルを削除します。キーは、自身が作成したエクスポートを削除できます。ほかの人のものを削除できるのは、管理者のみです。

**権限:** エクスポートの作成とダウンロード。

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## 証拠ロック

証拠ロックは、映像が削除されるのを防ぎます。[証拠ロック](/Help/live-and-playback#evidence-locks)をご覧ください。

### GET /api/v1/evidence-locks

ロックを一覧表示します。

| パラメーター | 意味 |
|---|---|
| `cameraId` | このカメラのみ。複数指定するには繰り返します。 |
| `fromUtc`、`toUtc` | この時間範囲の映像に対するロックのみ。 |
| `includeInactive` | `true` にすると、終了したロックも含まれます。 |

**権限:** 録画された映像を再生、および**再生**カメラアクセス。

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

1 台以上のカメラの映像をロックします。

| フィールド | 意味 |
|---|---|
| `cameraIds` | カメラ。 |
| `fromUtc`、`toUtc` | ロックする映像。 |
| `duration` | `1d`、`3d`、`7d`、`14d`、`30d`、`custom`、または `indefinite`。 |
| `customUntilUtc` | `custom` のロックが終了する日時。 |
| `reason` | ロックする理由。 |

**権限:** 録画された映像を再生、および保持期間で削除されないよう映像をロック、さらに**再生**カメラアクセス。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

ロックを解除します。理由が必要です。

**権限:** 録画された映像を再生、およびウェブインターフェースでロックを解除する場合と同じルール。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

解除されたロックを、猶予期間中に復元します。

**権限:** 録画された映像を再生、およびウェブインターフェースでロックを復元する場合と同じルール。

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## API にないもの

次のものは、API キーでは利用できません。

- ライブ映像。ウェブインターフェース、または上記の画像を使用してください。
- 映像の直接再生。エクスポートを使用してください。
- 設定、ユーザー、ロール、API キー、暗号化、バックアップ。
- ゾーン、スケジュール、デバイス設定などのカメラのセットアップ。

# Home Assistant

カメラ、検出、録画ノードを Home Assistant に表示し、Home Assistant から録画を開始したりブックマークを追加したりできます。

> **試験運用。** Home Assistant との連携は新しい機能で、Home Assistant の使い方は人によってさまざまです。[使い心地をお知らせください](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback)。何に使っているか、うまく動いている点、問題があった点を教えてください。

## しくみ

LarisVMS は、Home Assistant が既に使っている MQTT ブローカーに送信します。その後、Home Assistant の MQTT ディスカバリーが、カメラと録画ノードごとにデバイスを追加します。YAML を書く必要はありません。

Home Assistant は、API キーと同じように、選んだロールとして動作します。

- **設定 → カメラアクセス** でそのロールが表示できるカメラだけを受け取ります。
- そのロールに許可された操作だけができます。たとえば録画スイッチは、ロールに **手動録画の開始と停止** と **録画** カメラアクセスがある場合にだけ表示されます。
- Home Assistant の操作は、監査ログに **Home Assistant** として記録されます。

## 設定方法

1. Home Assistant で、まだ使っていなければ **Mosquitto broker** アドオンと **MQTT** 統合をインストールします。`larisvms` という名前の Home Assistant ユーザーなど、LarisVMS 用のブローカーのログインを作成します。
2. LarisVMS の **設定 → 権限 → ロール** で、Home Assistant 用のロールを作成します。**カメラを表示** を付与し、**設定 → カメラアクセス** で必要なカメラへのアクセスを付与します。Home Assistant から録画やブックマークをしたい場合は、**手動録画の開始と停止**、または **録画された映像を再生** と **録画された映像にブックマークを作成** を追加します。
3. **設定 → Home Assistant** を開いてオンにします。ブローカーのアドレス (アドオンを使う場合は Home Assistant のアドレス)、ポート (通常は `1883`)、ログイン、ロールを入力します。**接続をテスト** を選択し、**保存** を選択します。
4. Home Assistant で **設定 → デバイスとサービス → MQTT** を開きます。カメラがデバイスとして表示されます。

**設定 → Home Assistant** の状態には、LarisVMS が接続しているかどうかと、送信しているカメラの台数が表示されます。ブローカーが再起動したりネットワークが切れたりしても、LarisVMS は自動で再接続します。Home Assistant が再起動すると、LarisVMS はすべてを送り直します。

## Home Assistant に表示されるもの

各カメラには次のエンティティがあります。

| エンティティ | 表示内容 |
|---|---|
| **動き** | 動きまたは検出がある間オン |
| **人物**、**車両**、**動物** | その種類の物体が検出されている間オン |
| **オンライン** | カメラが映像を送信しているかどうか |
| **最新の検出画像** | 最後に検出された物体の画像 |
| **最後の検出**、**最後のイベント** | 最後に検出されたものと、その時刻 |
| **最後の速度** | [速度トラップ](/Help/ai-detection) で最後に測定した速度 (単位は **設定 → AI 検出** の設定に従います) |
| **手動録画** | ロールが許可している場合、手動録画を開始・停止するスイッチ |
| **ブックマーク** | ロールが許可している場合、現在の時刻にブックマークを追加するボタン |

各録画ノードには **オンライン** と **空き容量** があります。

センサーは検出から 1～2 秒でオンになり、検出が終わってから約 25 秒でオフになります。これは **ライブ** の動きバッジと同じです。

## ライブ映像

Home Assistant の **Generic Camera** で、LarisVMS のライブ映像を表示できます。Web ページと同じストリームを使うため、カメラに 2 本目のストリームを要求しません。

1. Home Assistant 用のロールに **他のプログラムへのライブ映像の配信** 権限を付与します。付与するまで、どのロールにもこの権限はありません。
2. **管理 → API キー** で、そのロールに紐づくキーを追加します。
3. Home Assistant で、カメラごとに **Generic Camera** を追加します。**設定 → Home Assistant** に、各カメラの **静止画の URL** と **ストリームのソース** が表示されます。認証に **Basic** を選び、ユーザー名に `apikey`、パスワードにキーを入力します。

ストリームについては [API](/Help/api) を参照してください。

## Webhook によるアラート

アラートルールの Webhook から Home Assistant に送信することもできます。Home Assistant で **Webhook** トリガーを使うオートメーションを作成し、そのアドレスをルールの Webhook に入力します。例: `http://homeassistant.local:8123/api/webhook/larisvms-front-door`

Webhook は、ルール、メッセージ、時刻を含む JSON を送ります。アラートに情報があれば、イベント、カメラ、ノード、検出した物体、速度、`snapshotPath` も含まれます。`snapshotPath` を LarisVMS のアドレスに付けると、API キーで検出の画像を取得できます。

```yaml
triggers:
  - trigger: webhook
    webhook_id: larisvms-front-door
    local_only: true
actions:
  - action: notify.notify
    data:
      message: "{{ trigger.json.message }}"
```

## 何かが表示されない場合

- **接続されていない:** **接続をテスト** でアドレス、ポート、ログインを確認します。TLS が必要なブローカーは通常ポート `8883` を使います。
- **Home Assistant にデバイスがない:** MQTT 統合が設定されていること、ディスカバリーのプレフィックスが Home Assistant と一致していること (変更していなければ `homeassistant`) を確認します。
- **カメラが表示されない:** ロールにそのカメラの表示権限がないか、カメラがオフになっています。
- **録画スイッチやブックマークボタンがない:** ロールに必要な権限またはカメラアクセスがありません。
- **Webhook が届かない:** LarisVMS は `localhost` に Webhook を送りません。同じマシンで動いている場合でも、Home Assistant のネットワークアドレスを使ってください。

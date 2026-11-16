# Home Assistant

在 Home Assistant 中顯示你的攝影機、偵測和錄影節點，並從 Home Assistant 開始錄影或新增書籤。

> **實驗性。** Home Assistant 支援是新功能，而大家使用 Home Assistant 的方式各不相同。請[告訴我們你的使用情況](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback)：你用它做什麼、哪些效果好、遇到了哪些問題。

## 運作方式

LarisVMS 會傳送到 Home Assistant 已在使用的 MQTT 代理伺服器。接著 Home Assistant 的 MQTT 探索會為每台攝影機和每個錄影節點新增一個裝置，無需撰寫 YAML。

Home Assistant 以你選擇的角色執行，與 API 金鑰相同：

- 它只會取得該角色在 **設定 → 攝影機存取** 中可以檢視的攝影機。
- 它只能執行該角色允許的操作。例如，只有當角色擁有 **開始和停止手動錄影** 和 **錄影** 攝影機存取權限時，錄影開關才會出現。
- 它執行的操作會在稽核記錄中顯示為 **Home Assistant**。

## 設定

1. 在 Home Assistant 中，如果還沒有使用，請安裝 **Mosquitto broker** 附加元件和 **MQTT** 整合。為 LarisVMS 建立一個代理伺服器登入，例如名為 `larisvms` 的 Home Assistant 使用者。
2. 在 LarisVMS 的 **設定 → 權限 → 角色** 中，為 Home Assistant 建立一個角色。授予它 **檢視攝影機**，並在 **設定 → 攝影機存取** 中授予它所需攝影機的存取權限。如果希望 Home Assistant 能錄影或新增書籤，請加入 **開始和停止手動錄影**，或 **回放已錄製的錄影** 和 **在已錄製的錄影上建立書籤**。
3. 開啟 **設定 → Home Assistant** 並啟用。輸入代理伺服器位址（使用附加元件時就是你的 Home Assistant 位址）、連接埠（通常為 `1883`）、登入資訊和角色。選擇 **測試連線**，然後選擇 **儲存**。
4. 在 Home Assistant 中開啟 **設定 → 裝置與服務 → MQTT**。你的攝影機會以裝置的形式顯示在那裡。

**設定 → Home Assistant** 中的狀態會顯示 LarisVMS 是否已連線，以及正在傳送多少台攝影機。代理伺服器重新啟動或網路中斷時，LarisVMS 會自動重新連線。Home Assistant 重新啟動時，LarisVMS 會重新傳送所有內容。

## Home Assistant 中顯示的內容

每台攝影機都會有：

| 實體 | 顯示內容 |
|---|---|
| **移動** | 有移動或偵測時為開 |
| **人員**、**車輛**、**動物** | 偵測到該類物體時為開 |
| **線上** | 攝影機是否在傳送影像 |
| **最新偵測影像** | 最近偵測到的物體的圖片 |
| **最近偵測**、**最近事件** | 最近偵測到了什麼，以及時間 |
| **最近速度** | [測速區](/Help/ai-detection) 最近測得的速度，單位依 **設定 → AI 偵測** 中的設定 |
| **手動錄影** | 角色允許時，用來開始和停止手動錄影的開關 |
| **新增書籤** | 角色允許時，在目前時間新增書籤的按鈕 |

每個錄影節點都有 **線上** 和 **可用儲存空間**。

感應器會在偵測後一兩秒內開啟，並在偵測結束約 25 秒後關閉。這與 **即時** 頁面上的移動標記相同。

## 即時影像

Home Assistant 的 **Generic Camera** 可以顯示 LarisVMS 的即時影像。它使用與網頁相同的串流，因此不會向攝影機再要一路串流。

1. 為 Home Assistant 角色授予 **向其他程式串流即時影像** 權限。在你授予之前，任何角色都沒有此權限。
2. 在 **管理 → API 金鑰** 中，新增一個綁定到該角色的金鑰。
3. 在 Home Assistant 中，為每台攝影機新增一個 **Generic Camera**。**設定 → Home Assistant** 會列出每台攝影機的 **靜態影像 URL** 和 **串流來源**。驗證方式選擇 **Basic**，使用者名稱填 `apikey`，密碼填該金鑰。

[API](/Help/api) 中說明了該串流。

## 透過 Webhook 傳送警報

警報規則的 Webhook 也可以傳送到 Home Assistant。在 Home Assistant 中建立一個使用 **Webhook** 觸發條件的自動化，然後把它的位址填入規則的 Webhook，例如 `http://homeassistant.local:8123/api/webhook/larisvms-front-door`。

Webhook 傳送的 JSON 包含規則、訊息和時間；如果警報包含這些資訊，還會包含事件、攝影機、節點、偵測到的物體、速度和 `snapshotPath`。把 `snapshotPath` 加在你的 LarisVMS 位址後面，即可用 API 金鑰取得偵測影像。

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

## 如果缺少內容

- **未連線：** 用 **測試連線** 檢查位址、連接埠和登入資訊。要求 TLS 的代理伺服器通常使用 `8883` 連接埠。
- **Home Assistant 中沒有裝置：** 檢查 MQTT 整合是否已設定，以及探索前綴是否與 Home Assistant 的一致（除非你改過，否則為 `homeassistant`）。
- **缺少某台攝影機：** 角色無法檢視它，或者該攝影機已關閉。
- **沒有錄影開關或書籤按鈕：** 角色缺少相應的權限或攝影機存取權限。
- **收不到 Webhook：** LarisVMS 不會向 `localhost` 傳送 Webhook。即使 Home Assistant 執行在同一台電腦上，也請使用它的網路位址。

# 安裝與升級

安裝 Web 伺服器、錄影節點和媒體代理，並使它們保持最新。

每個部分都有自己的安裝程式：`LarisVMS-Web`、`LarisVMS-Node` 和 `LarisVMS-Proxy`（均為 `.msi`）。按兩下即可依提示輸入其設定，也可以在命令列中用 `msiexec /i <file>.msi NAME=value … /qn` 傳入。未提供的內容會被詢問，升級則會沿用上次安裝的設定。

## Web 伺服器

1. 準備一個可從伺服器存取的 SQL Server（整合式安全性或 SQL 驗證），或一個 MariaDB 伺服器（請參閱 [MariaDB](#mariadb)）。
2. 執行 `LarisVMS-Web.msi`。設定：
   - `HTTPSPORT`：HTTPS 連接埠（預設 8444）。安裝程式會在防火牆中為 TCP 和 UDP（[HTTP/3](/Help/security#http3)）開放它。
   - `CERTPATH`、`CERTPASSWORD`：伺服器憑證（`.pfx`）。留空則在您取得憑證之前使用自我簽署憑證。安裝程式在繼續之前會檢查檔案能否用該密碼開啟；檔案共用還必須能被服務帳戶讀取。
   - `SERVICEACCOUNT`、`SERVICEPASSWORD`：以特定帳戶執行服務，例如具有 SQL 整合式安全性權限的帳戶。留空則以 LocalSystem 執行。MariaDB 不需要。
3. 瀏覽到 `https://<host>:8444/`。安裝精靈會依序引導您完成資料庫、第一個管理員帳戶、儲存空間和節點註冊。伺服器本機或區域網路內的瀏覽器會直接進入。從其他任何地方存取時，會先要求輸入伺服器上 `%ProgramData%\LarisVMS\setup-token.txt` 中的安裝權杖（僅管理員可讀），因此網際網路上的任何人都無法搶先進行安裝。若要對所有其他電腦（包括區域網路內的）都要求權杖，請在開啟精靈之前，在 `appsettings.Production.json` 中設定 `"Setup": { "AlwaysRequireToken": true }`。管理員帳戶建立後，該檔案會被刪除。

SQL 登入的密碼或 MariaDB 密碼在伺服器上經過加密儲存（DPAPI，僅限此電腦），因此無法把 `setup-generated.json` 複製到另一台電腦後仍讓密碼生效。請改在那台電腦上重新執行精靈。

如果您讓精靈指向一個現有的 LarisVMS 資料庫（例如重新安裝伺服器之後），它會原樣使用該資料庫：必要時升級，不會初始化或覆寫任何內容，並略過其餘步驟。不是 LarisVMS 的資料庫會被拒絕。安裝完成後，無法再次開啟精靈。

伺服器不需要 .NET 執行階段；它已包含在內。憑證之後也可以在安裝資料夾的 `appsettings.Production.json` 中變更。同一路徑上續期後的 `.pfx` 會在一分鐘內被採用，無需重新啟動。

## MariaDB

LarisVMS 可以使用 MariaDB 10.11 或更新版本來代替 SQL Server。在安裝精靈中選擇 **MariaDB**，並輸入主機、連接埠（預設 3306）、資料庫名稱，以及 MariaDB 使用者和密碼。MariaDB 沒有 LarisVMS 可用的 Windows 登入，因此一律需要使用者名稱和密碼。

- **使用者**需要有建立資料庫的權限。或者您自己先用 `utf8mb4` 字元集建立它，並授予該使用者對它的 `ALL` 權限：

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **加密**：連線使用 TLS。會驗證伺服器的憑證，除非 MariaDB 就在此電腦上，或者您勾選了**信任此伺服器的憑證**（用於另一台電腦上的自我簽署憑證）。
- **11.4 之前的 MariaDB 預設關閉 TLS**，直到為其提供憑證為止，而沒有 TLS 時，LarisVMS 不會把密碼傳送到另一台電腦。請將憑證及其金鑰加入 `my.ini` 的 `[mysqld]` 區段，重新啟動 MariaDB，如果是自我簽署的，還要勾選**信任此伺服器的憑證**：

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 及更新版本會自行設定憑證。
- **備份**需要在 LarisVMS 伺服器上安裝 MariaDB 用戶端工具（請參閱[備份](/Help/backups)）。
- 現有的 SQL Server 安裝無法移轉到 MariaDB；MariaDB 僅用於新安裝。

## 節點

在每台錄影電腦上執行 `LarisVMS-Node.msi`。它需要：

- `SERVERURL` 和 `REGISTRATIONKEY`：伺服器的位址，以及**節點 → 更新與註冊**下的**註冊金鑰**。它們只在節點首次註冊時使用。變更金鑰不會影響現有節點。
- **FFmpeg**，未隨附。請先用 `winget install ffmpeg --scope machine` 安裝。安裝程式會找到它，或者設定 `FFMPEGPATH`。
- `STORAGEROOT`：節點錄影的位置。之後也可以在節點的編輯頁面上設定。

選用：`ARCHIVEROOT`、`LIVEPORT`（預設 8554）、`LIVEALLOWFROM`（防火牆允許存取即時連接埠的位址，用 IP 或 CIDR 區塊表示，以逗號分隔：LarisVMS 伺服器和所有代理；留空則允許任何位址）、`INSECURETLS=1`（不再需要：節點在首次使用時信任伺服器的自我簽署憑證，請參閱[伺服器憑證](/Help/nodes#server-certificate)）、直連串流端點（`CLIENTPORT`、`CLIENTENDPOINTHOST`、`CLIENTPFXPATH`、`CLIENTPFXPASSWORD`），以及用於可存取 SMB 儲存空間的帳戶的 `SERVICEACCOUNT` / `SERVICEPASSWORD`。

對於 AI 偵測，NVIDIA 電腦還需要 CUDA Toolkit 12.x 和 cuDNN 9.x；安裝程式發現它們時會將其 DLL 複製到位。請參閱 [AI 偵測](/Help/ai-detection#hardware)。

安裝後，節點會在**節點**下顯示為**等待核准**。在您按一下**核准**之前，它不會錄製任何內容，也不會獲得攝影機，因此只核准您自己安裝的節點。然後從每台攝影機的編輯頁面將攝影機指派給它。

## 媒體代理

執行 `LarisVMS-Proxy.msi`，並提供 `SERVERURL`、`REGISTRATIONKEY` 和 `CLIENTPORT`（預設 4443），以及與節點相同的選用憑證設定。新代理一開始是關閉的：請在**設定 → 媒體代理**下為其開啟**啟用**，然後在每個節點的頁面上選擇它（請參閱[節點](/Help/nodes#proxies)）。

## 更新

**升級**：執行較新的 `.msi`。它會找到現有安裝並直接進入**準備升級**；設定會被記住，因此除了網域服務帳戶的密碼外，無需重新輸入任何內容。Web 伺服器會在啟動時自行套用資料庫變更。升級絕不會觸碰錄影、設定檔、節點註冊或資料保護金鑰，安裝失敗時會回復。

**節點和代理會自動更新**。新的 Web 安裝會將其自帶的節點和代理版本，作為**待處理**註冊到**節點**的**版本**區段。只有最新的節點版本和最新的代理版本會等待核准；較舊的會變為**已取代**。**版本歷程記錄**列出每個版本。核准一個版本後，每個較舊的節點會在下次簽到時下載、驗證並安裝它。伺服器使用其簽署金鑰對每個更新簽署（請參閱**設定 → 加密與簽署**）；節點和代理會保留其註冊時的那個金鑰，並且即使它們與伺服器的連線被竄改，也不會安裝任何未用該金鑰簽署的內容。可在**節點 → 更新與註冊**下用**自動更新**將其關閉。

**新版本**：伺服器每天（以及服務啟動時）會檢查 LarisVMS 的 GitHub 發行頁面。當有新版本時，可變更設定的人員會在頂端列看到**✨ 版本 X 可用**，並連結到其版本資訊。不會傳送任何有關您安裝的資訊。可在**節點 → 更新與註冊**下用**新版本**將其關閉。

**解除安裝**會移除程式、其服務和防火牆規則。錄影、設定和 `%ProgramData%\LarisVMS` 會被保留。

## 從原始碼安裝

開發人員也可以從程式碼存放庫建置並改用 PowerShell 安裝：`install-web.ps1`（伺服器），以及由 `build-node.ps1` / `build-proxy.ps1` 建置的套件中的 `install-node.ps1` / `install-proxy.ps1` 指令碼。`build-installers.ps1` 建置這三個 MSI。MSI 會就地升級透過指令碼安裝的電腦，並保留其設定、註冊以及任何直連串流端點及其防火牆規則。

## 憑證

`appsettings.Production.json` 中的 `Kestrel:Certificates:Default:Path` 和 `:Password` 指向一個 `.pfx` 檔案。伺服器每 60 秒檢查一次，並在不重新啟動的情況下換入續期後的憑證。

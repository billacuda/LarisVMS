# 安裝與升級

安裝 Web 伺服器、錄影節點和媒體代理，並使它們保持最新。

每個部分都有自己的安裝程式：`LarisVMS-Web`、`LarisVMS-Node` 和 `LarisVMS-Proxy`（均為 `.msi`）。按兩下即可依提示輸入其設定，也可以在命令列中用 `msiexec /i <file>.msi NAME=value … /qn` 傳入。未提供的內容會被詢問，升級則會沿用上次安裝的設定。

每個部分也都可以透過 `.deb` 或 `.rpm` 套件在 Linux 上執行（請參閱 [Linux](#linux)），兩者可以混用：Windows 伺服器可以有 Linux 節點和代理，反之亦然。

## Web 伺服器

1. 準備一個可從伺服器存取的 SQL Server（整合式安全性或 SQL 驗證），或一個 MariaDB 伺服器（請參閱 [MariaDB](#mariadb)）。
2. 執行 `LarisVMS-Web.msi`。設定：
   - `HTTPSPORT`：HTTPS 連接埠（預設 8444）。安裝程式會在防火牆中為 TCP 和 UDP（[HTTP/3](/Help/security#http3)）開放它。
   - `CERTPATH`、`CERTPASSWORD`：伺服器憑證（`.pfx`）。留空則在您取得憑證之前使用自我簽署憑證。安裝程式在繼續之前會檢查檔案能否用該密碼開啟；檔案共用還必須能被服務帳戶讀取。
   - `SERVICEACCOUNT`、`SERVICEPASSWORD`：以特定帳戶執行服務，例如具有 SQL 整合式安全性權限的帳戶。留空則以 LocalSystem 執行。MariaDB 不需要。
3. 瀏覽到 `https://<host>:8444/`。安裝精靈會依序引導您完成資料庫、第一個管理員帳戶、儲存空間和節點註冊。伺服器本機或區域網路內的瀏覽器會直接進入。從其他任何地方存取時，會先要求輸入伺服器上 `%ProgramData%\LarisVMS\setup-token.txt` （Linux 上為 `/var/lib/larisvms/setup-token.txt`）中的安裝權杖（僅管理員可讀），因此網際網路上的任何人都無法搶先進行安裝。若要對所有其他電腦（包括區域網路內的）都要求權杖，請在開啟精靈之前，在 `appsettings.Production.json` 中設定 `"Setup": { "AlwaysRequireToken": true }`。管理員帳戶建立後，該檔案會被刪除。

SQL 登入的密碼或 MariaDB 密碼在伺服器上經過加密儲存（Windows 上使用 DPAPI，Linux 上使用與電腦 ID 繫結的金鑰；僅限此電腦），因此無法把 `setup-generated.json` 複製到另一台電腦後仍讓密碼生效。請改在那台電腦上重新執行精靈。

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

對於 AI 偵測，NVIDIA 電腦還需要 CUDA 12 和 cuDNN 9；在 Windows 上，安裝程式發現它們時會將其 DLL 複製到位。請參閱 [AI 偵測](/Help/ai-detection#hardware)。

安裝後，節點會在**節點**下顯示為**等待核准**。在您按一下**核准**之前，它不會錄製任何內容，也不會獲得攝影機，因此只核准您自己安裝的節點。然後從每台攝影機的編輯頁面將攝影機指派給它。

## 媒體代理

執行 `LarisVMS-Proxy.msi`，並提供 `SERVERURL`、`REGISTRATIONKEY` 和 `CLIENTPORT`（預設 4443），以及與節點相同的選用憑證設定。新代理一開始是關閉的：請在**設定 → 媒體代理**下為其開啟**啟用**，然後在每個節點的頁面上選擇它（請參閱[節點](/Help/nodes#proxies)）。

## Linux

Web 伺服器、節點和代理透過 x86_64 的 `.deb` 套件（Ubuntu 22.04、24.04、26.04，Debian 12、13）或 `.rpm` 套件（Rocky Linux 9、10，以及其他 RHEL 9 或 10 的重新建置版）安裝。它們各自作為 systemd 服務（`larisvms-web`、`larisvms-node`、`larisvms-proxy`）以專用的 `larisvms` 帳戶執行。程式位於 `/opt/larisvms`，設定位於 `/etc/larisvms`，資料（金鑰、模型、安裝權杖）位於 `/var/lib/larisvms`，記錄檔位於 `/var/log/larisvms`。不需要 .NET 執行階段。如果 firewalld 或 ufw 處於使用中狀態，安裝時會開放服務的連接埠。

- **Web 伺服器**：`sudo apt install ./larisvms-web_<version>-1_amd64.deb`（或 `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`）。它會立即啟動；開啟 `https://<host>:8444/` 進入安裝精靈。其連接埠和憑證在 `/etc/larisvms/web.env` 中設定（`Kestrel__HttpsPort`、`Kestrel__Certificates__Default__Path`、`Kestrel__Certificates__Default__Password`；larisvms 帳戶必須能讀取該檔案），然後執行 `sudo systemctl restart larisvms-web`。`appsettings.Production.json` 和 `setup-generated.json` 也在 `/etc/larisvms` 中。在 Linux 上，SQL Server 需要 SQL 登入；選擇 MariaDB 較簡單。
- **節點**：安裝 `larisvms-node`，然後用 `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>` 配對。安裝精靈的節點步驟會顯示此命令，並已填好金鑰和伺服器憑證的指紋。除非您加上 `--storage-root <folder>`，否則它會錄影到 `/var/lib/larisvms/recordings`；網路共用必須先掛載（例如在 `/etc/fstab` 中），並且 larisvms 帳戶可以寫入。其他選項：`--archive-root`、`--live-port`、`--live-allow-from`、`--client-port`、`--client-pfx-path`、`--client-endpoint-host` 和 `--ffmpeg-path`。`sudo larisvms-node setup --help` 會列出全部選項，再次執行 `setup` 可變更它們。
- **ffmpeg** 來自發行版，隨節點一起安裝。在 Rocky Linux 上它來自 RPM Fusion，因此請在安裝節點之前啟用 EPEL、CRB 和 RPM Fusion。
- **代理**：安裝 `larisvms-proxy`，然後執行 `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`。

使用 `systemctl status larisvms-node` 和 `journalctl -u larisvms-node` 檢查服務，使用 `larisvms-node --version` 顯示其版本。

**Docker**：每次發行也會以映像形式發佈到 GitHub 的容器登錄：`ghcr.io/billacuda/larisvms-web`、`larisvms-node`（帶有適用於 NVIDIA GPU 的 `-cuda` 和 `-tensorrt` 標籤）和 `larisvms-proxy`。Web 伺服器需要您自行執行的資料庫（MariaDB 10.11 或更新版本，或使用 SQL 登入的 SQL Server）。請用以下方式之一安裝：

- **Docker Compose**：下載 [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml)，並在其資料夾中執行 `docker compose up -d`。它會啟動 Web 伺服器；開啟 `https://<host>:8444/` 進入安裝精靈。然後在該檔案中取消節點（以及代理，如果需要）的註解，填寫伺服器位址、註冊金鑰和錄影資料夾，並再次執行 `docker compose up -d`。
- **Portainer 堆疊**：在 **Stacks → Add stack → Web editor** 下貼上同一個檔案，然後選擇 **Deploy the stack**。完成精靈後，在堆疊的 **Editor** 索引標籤中取消節點的註解，然後選擇 **Update the stack**。請使用獨立的 Docker 環境，而不是 Swarm。
- **純 Docker**：啟動 Web 伺服器，執行精靈，然後啟動節點：

```bash
docker run -d --name larisvms-web --network host --restart unless-stopped \
  -e Kestrel__HttpsPort=8444 \
  -v /srv/larisvms/web/data:/var/lib/larisvms \
  -v /srv/larisvms/web/config:/etc/larisvms \
  -v /srv/larisvms/web/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-web:latest

docker run -d --name larisvms-node --hostname larisvms-node-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_STORAGE_ROOT=/recordings \
  -v /srv/larisvms/node/data:/var/lib/larisvms \
  -v /srv/larisvms/node/config:/etc/larisvms \
  -v /srv/larisvms/node/logs:/var/log/larisvms \
  -v /srv/larisvms/recordings:/recordings \
  ghcr.io/billacuda/larisvms-node:latest
```

之後在**節點**中核准該節點。伺服器位址請使用其區域網路位址或名稱，即使在同一台電腦上也不要使用 `localhost`。如果電腦有防火牆，請允許 TCP 8444（Web 伺服器）、8554（節點）、8555（直接向瀏覽器串流的節點）和 4443（代理），如果使用 HTTP/3，還需允許 UDP 8444。容器以使用者和群組 8444 執行，其在主機上的資料夾也歸該使用者和群組所有。

安裝精靈的節點步驟和**媒體代理**頁面會顯示適用於 Portainer 或 `docker compose` 的 Compose 堆疊，並已填好此伺服器的位址、註冊金鑰和憑證。這些設定與 Linux 服務使用的環境變數相同（`LARISVMS_SERVER_URL`、`LARISVMS_REGISTRATION_KEY`、`LARISVMS_STORAGE_ROOT` 等）。請將 `/var/lib/larisvms` 放在磁碟區上：其中包含金鑰、註冊資訊，以及在重新建立容器後使已儲存的密碼仍可讀取的電腦 ID。請使用主機網路，以便攝影機探索能夠運作。要更新，請執行 `docker compose pull` 和 `docker compose up -d`；在 Portainer 中，請開啟 **Re-pull image and redeploy** 後選擇 **Update the stack**；使用純 Docker 時，請提取映像、移除容器，然後再次執行其命令。

**將 Linux 安裝移轉到 Docker**：在容器首次啟動之前，複製資料資料夾（最重要的是 `keys`）和電腦 ID，並先備份資料庫。資料庫密碼和節點註冊資訊用電腦 ID 加密，資料庫中的密碼和節點金鑰用 `keys` 加密；缺少它們時，**節點**等頁面會顯示錯誤。Windows 安裝無法用這種方式移轉。對於 Web 伺服器：

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

將 `/etc/larisvms/web.env` 中的設定移到堆疊的 `environment` 中，然後啟動容器。它會連線到同一個資料庫，無需精靈。切勿同時執行舊服務和容器。對於節點，請將錄影留在原處：

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

將 `/etc/larisvms/node.env` 中的設定移到堆疊的 `environment` 中，並將每個錄影和封存資料夾掛載到容器內的相同路徑（例如 `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`，並將 `LARISVMS_STORAGE_ROOT` 設為該路徑）。節點會以原來的身分恢復，保留其攝影機和錄影，不需要註冊金鑰或核准。代理以同樣的方式移轉，只是沒有錄影。之後移除舊套件會關閉防火牆中的連接埠，因此請為容器重新開放它們。

如果您已經在容器中針對舊資料庫執行過精靈，則只缺少金鑰。請將它們複製到容器自己的金鑰旁邊，並保留兩者：

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

說明中提到的 Windows 資料夾，在 Linux 上對應如下：`%ProgramData%\LarisVMS` → `/var/lib/larisvms`（其 `logs` 資料夾 → `/var/log/larisvms`），安裝資料夾中的 `appsettings.Production.json` 和 `setup-generated.json` → `/etc/larisvms`。`Restart-Service LarisVMSWeb` 對應 `sudo systemctl restart larisvms-web`。

## 更新

**升級**：執行較新的 `.msi`。它會找到現有安裝並直接進入**準備升級**；設定會被記住，因此除了網域服務帳戶的密碼外，無需重新輸入任何內容。Web 伺服器會在啟動時自行套用資料庫變更。升級絕不會觸碰錄影、設定檔、節點註冊或資料保護金鑰，安裝失敗時會回復。

**節點和代理會自動更新**。新的 Web 安裝會將其自帶的節點和代理版本，作為**待處理**註冊到**節點**的**版本**區段。只有最新的節點版本和最新的代理版本會等待核准；較舊的會變為**已取代**。**版本歷程記錄**列出每個版本。核准一個版本後，每個較舊的節點會在下次簽到時下載、驗證並安裝它。伺服器使用其簽署金鑰對每個更新簽署（請參閱**設定 → 加密與簽署**）；節點和代理會保留其註冊時的那個金鑰，並且即使它們與伺服器的連線被竄改，也不會安裝任何未用該金鑰簽署的內容。可在**節點 → 更新與註冊**下用**自動更新**將其關閉。

**新版本**：伺服器每天（以及服務啟動時）會檢查 LarisVMS 的 GitHub 發行頁面。當有新版本時，可變更設定的人員會在頂端列看到 **✨ 版本 X 可用**，並連結到其版本資訊。不會傳送任何有關您安裝的資訊。可在**節點 → 更新與註冊**下用**新版本**將其關閉。

**在 Linux 上**，以同樣的方式安裝較新的套件即可升級 Web 伺服器；`/etc/larisvms` 和 `/var/lib/larisvms` 會被保留。核准版本後，Linux 節點和代理會像 Windows 上的一樣從伺服器自行更新：一個以 root 執行的小型輔助程式會安裝服務已下載並驗證的更新。Linux 伺服器向 Windows 節點提供 Windows 版本，Windows 伺服器向 Linux 節點提供 Linux 版本。

**解除安裝**會移除程式、其服務和防火牆規則。錄影、設定和 `%ProgramData%\LarisVMS` 會被保留。在 Linux 上，`apt remove` 或 `dnf remove` 的作用相同，並保留 `/var/lib/larisvms`、`/var/log/larisvms` 和 `/etc/larisvms`。

## 從原始碼安裝

開發人員也可以從程式碼存放庫建置並改用 PowerShell 安裝：`install-web.ps1`（伺服器），以及由 `build-node.ps1` / `build-proxy.ps1` 建置的套件中的 `install-node.ps1` / `install-proxy.ps1` 指令碼。`build-installers.ps1` 建置這三個 MSI，以及 Linux 的 `.deb` 和 `.rpm` 套件（使用 nFPM）。MSI 會就地升級透過指令碼安裝的電腦，並保留其設定、註冊以及任何直連串流端點及其防火牆規則。

## 憑證

`appsettings.Production.json` 中的 `Kestrel:Certificates:Default:Path` 和 `:Password` 指向一個 `.pfx` 檔案（在 Linux 上，也可在 `/etc/larisvms/web.env` 中用 `Kestrel__Certificates__Default__Path` 和 `__Password` 設定）。伺服器每 60 秒檢查一次，並在不重新啟動的情況下換入續期後的憑證。

在 Docker 中，請掛載憑證資料夾（`/srv/larisvms/certs:/certs:ro`），並使用容器內的路徑，例如 `/certs/vms.pfx`。使用者 8444 必須能讀取它，密碼中的 `$` 在 Compose 檔案中要寫成 `$$`。無法載入的憑證會被取代為自我簽署憑證，原因可在 `docker logs` 中查看。

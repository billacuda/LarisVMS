# インストールとアップグレード

ウェブサーバー、録画ノード、メディアプロキシのインストールと、それらを最新に保つ方法を説明します。

各部分には、それぞれ専用のインストーラー（`LarisVMS-Web`、`LarisVMS-Node`、`LarisVMS-Proxy`、いずれも `.msi`）があります。ダブルクリックすると設定の入力を求められます。コマンドラインで `msiexec /i <file>.msi NAME=value … /qn` のように渡すこともできます。指定しなかった項目は入力を求められ、アップグレードでは前回のインストールの設定が再利用されます。

どの部分も、`.deb` または `.rpm` パッケージから Linux でも動作します（[Linux](#linux) をご覧ください）。両者は混在できます。Windows サーバーに Linux のノードやプロキシを持たせることも、その逆も可能です。

## ウェブサーバー

1. サーバーから到達できる SQL Server（統合セキュリティまたは SQL 認証）、または MariaDB サーバーを用意します（[MariaDB](#mariadb) をご覧ください）。
2. `LarisVMS-Web.msi` を実行します。設定項目:
   - `HTTPSPORT`: HTTPS ポート（既定は 8444）。インストーラーが、TCP と UDP（[HTTP/3](/Help/security#http3)）の両方でファイアウォールを開きます。
   - `CERTPATH`、`CERTPASSWORD`: サーバー証明書（`.pfx`）。証明書が用意できるまでは、空欄にして自己署名証明書を使用します。インストーラーは、続行する前にパスワードでファイルが開くことを確認します。ファイル共有の場合は、サービスアカウントからも読み取れる必要があります。
   - `SERVICEACCOUNT`、`SERVICEPASSWORD`: サービスを特定のアカウント（たとえば SQL 統合セキュリティの権限を持つアカウント）で実行します。空欄の場合は LocalSystem で実行されます。MariaDB では不要です。
3. `https://<host>:8444/` にアクセスします。セットアップウィザードが、データベース、最初の管理者アカウント、ストレージ、ノードの登録を順に案内します。サーバー自体のブラウザー、またはローカルネットワーク内のブラウザーからは、そのまま進めます。それ以外の場所からは、まずサーバー上の `%ProgramData%\LarisVMS\setup-token.txt`（Linux では `/var/lib/larisvms/setup-token.txt`、管理者のみ読み取り可能）にあるセットアップトークンを求められるため、インターネット上の誰かが先にセットアップすることはできません。ローカルネットワークを含むすべてのほかのコンピューターからトークンを求めるには、ウィザードを開く前に、`appsettings.Production.json` に `"Setup": { "AlwaysRequireToken": true }` を設定します。このファイルは、管理者アカウントが作成されると削除されます。

SQL ログインのパスワードや MariaDB のパスワードは、サーバーに暗号化して保存されます（Windows では DPAPI、Linux ではマシン ID に結び付いたキー。このマシンのみ）。そのため、`setup-generated.json` を別のマシンにコピーしても、パスワードは機能しません。代わりに、そこでもう一度ウィザードを実行してください。

ウィザードで既存の LarisVMS データベース（たとえばサーバーの再インストール後）を指定した場合は、そのデータベースがそのまま使用されます。必要に応じてアップグレードされ、何も初期投入や上書きは行われず、残りの手順はスキップされます。LarisVMS 以外のデータベースは拒否されます。セットアップが完了すると、ウィザードを再び開くことはできません。

サーバーに .NET ランタイムは不要です。同梱されています。証明書は、後からインストールフォルダー内の `appsettings.Production.json` でも変更できます。同じパスにある更新された `.pfx` は、再起動なしで 1 分以内に反映されます。

## MariaDB

LarisVMS は、SQL Server の代わりに MariaDB 10.11 以降を使用できます。セットアップウィザードで **MariaDB** を選択し、ホスト、ポート（既定は 3306）、データベース名、MariaDB のユーザー名とパスワードを入力します。LarisVMS が使用できる MariaDB 用の Windows サインインはないため、ユーザー名とパスワードが常に必要です。

- **ユーザー**には、データベースを作成する権限が必要です。または、先にご自身で `utf8mb4` 文字セットを指定して作成し、そのユーザーに対して `ALL` を付与します:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **暗号化:** 接続には TLS を使用します。MariaDB がこのマシン上にある場合、または**このサーバーの証明書を信頼する**にチェックを入れた場合（別のマシン上の自己署名証明書向け）を除き、サーバーの証明書が検証されます。
- **11.4 より前の MariaDB は TLS がオフ**で、証明書を設定するまで有効になりません。LarisVMS は、TLS なしでは別のマシンにパスワードを送信しません。`my.ini` の `[mysqld]` セクションに証明書とそのキーを追加し、MariaDB を再起動します。自己署名の場合は、**このサーバーの証明書を信頼する**にもチェックを入れてください:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 以降は、証明書を自動で設定します。
- **バックアップ**には、LarisVMS サーバー上に MariaDB クライアントツールが必要です（[バックアップ](/Help/backups)をご覧ください）。
- 既存の SQL Server のインストールを MariaDB に移行することはできません。MariaDB は新規インストール向けです。

## ノード

各録画マシンで `LarisVMS-Node.msi` を実行します。次のものが必要です:

- `SERVERURL` と `REGISTRATIONKEY`: サーバーのアドレスと、**ノード → 更新と登録**にある**登録キー**です。ノードが初めて登録するときにのみ使用されます。キーを変更しても、既存のノードには影響しません。
- **FFmpeg**: 同梱されていません。先に `winget install ffmpeg --scope machine` でインストールしてください。インストーラーが見つけます。見つからない場合は `FFMPEGPATH` を設定してください。
- `STORAGEROOT`: ノードの録画先です。後からノードの編集ページでも設定できます。

任意項目: `ARCHIVEROOT`、`LIVEPORT`（既定は 8554）、`LIVEALLOWFROM`（ファイアウォールでライブポートへの接続を許可するアドレス。LarisVMS サーバーとプロキシを、IP または CIDR ブロックでカンマ区切りで指定します。空欄はすべて許可）、`INSECURETLS=1`（不要になりました。ノードは初回使用時にサーバーの自己署名証明書を信頼します。[サーバー証明書](/Help/nodes#server-certificate)をご覧ください）、ダイレクトストリーミングのエンドポイント（`CLIENTPORT`、`CLIENTENDPOINTHOST`、`CLIENTPFXPATH`、`CLIENTPFXPASSWORD`）、SMB ストレージにアクセスできるアカウント用の `SERVICEACCOUNT` / `SERVICEPASSWORD`。

AI 検出では、NVIDIA のマシンに CUDA 12 と cuDNN 9 も必要です。Windows では、インストーラーは、これらを見つけると、その DLL を所定の場所にコピーします。[AI 検出](/Help/ai-detection#hardware)をご覧ください。

インストール後、ノードは**ノード**に**承認待ち**として表示されます。**承認**をクリックするまで、何も録画せず、カメラも割り当てられません。そのため、ご自身でインストールしたノードのみを承認してください。その後、各カメラの編集ページから、ノードにカメラを割り当てます。

## メディアプロキシ

`SERVERURL`、`REGISTRATIONKEY`、`CLIENTPORT`（既定は 4443）を指定して `LarisVMS-Proxy.msi` を実行します。証明書の任意設定は、ノードと同じです。新しいプロキシは、オフの状態で始まります。**設定 → メディアプロキシ**で**有効**をオンにしてから、各ノードのページでそれを選択してください（[ノード](/Help/nodes#proxies)をご覧ください）。

## Linux

Web サーバー、ノード、プロキシは、x86_64 用の `.deb` パッケージ（Ubuntu 22.04、24.04、26.04、Debian 12、13）または `.rpm` パッケージ（Rocky Linux 9、10、およびその他の RHEL 9 または 10 のリビルド）からインストールします。それぞれ systemd サービス（`larisvms-web`、`larisvms-node`、`larisvms-proxy`）として、専用の `larisvms` アカウントで動作します。プログラムは `/opt/larisvms`、設定は `/etc/larisvms`、データ（キー、モデル、セットアップトークン）は `/var/lib/larisvms`、ログファイルは `/var/log/larisvms` に置かれます。.NET ランタイムは不要です。firewalld または ufw が有効な場合、インストール時にサービスのポートが開かれます。

- **Web サーバー:** `sudo apt install ./larisvms-web_<version>-1_amd64.deb`（または `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`）。すぐに起動します。`https://<host>:8444/` を開いて、セットアップウィザードに進みます。ポートと証明書は `/etc/larisvms/web.env` で設定し（`Kestrel__HttpsPort`、`Kestrel__Certificates__Default__Path`、`Kestrel__Certificates__Default__Password`。larisvms アカウントがファイルを読み取れる必要があります）、その後 `sudo systemctl restart larisvms-web` を実行します。`appsettings.Production.json` と `setup-generated.json` も `/etc/larisvms` にあります。Linux では、SQL Server に SQL ログインが必要です。Linux では MariaDB のほうが簡単です。
- **ノード:** `larisvms-node` をインストールし、`sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>` でペアリングします。セットアップウィザードのノードのステップには、キーとサーバー証明書のサムプリントが入力済みのこのコマンドが表示されます。`--storage-root <folder>` を追加しないかぎり、`/var/lib/larisvms/recordings` に録画します。ネットワーク共有は、先にマウントし（たとえば `/etc/fstab` で）、larisvms アカウントが書き込めるようにしておく必要があります。その他のオプションは、`--archive-root`、`--live-port`、`--live-allow-from`、`--client-port`、`--client-pfx-path`、`--client-endpoint-host`、`--ffmpeg-path` です。`sudo larisvms-node setup --help` ですべて一覧表示され、`setup` をもう一度実行すると変更できます。
- **ffmpeg** はディストリビューションのもので、ノードとともにインストールされます。Rocky Linux では RPM Fusion から取得されるため、ノードをインストールする前に EPEL、CRB、RPM Fusion を有効にしてください。
- **プロキシ:** `larisvms-proxy` をインストールし、`sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443` を実行します。

サービスは `systemctl status larisvms-node` と `journalctl -u larisvms-node` で確認でき、バージョンは `larisvms-node --version` で表示されます。

**Docker:** すべてのリリースは、GitHub のコンテナーレジストリにもイメージとして公開されます。`ghcr.io/billacuda/larisvms-web`、`larisvms-node`（NVIDIA GPU 用の `-cuda` と `-tensorrt` タグあり）、`larisvms-proxy` です。Web サーバーには、ご自身で運用するデータベース（MariaDB 10.11 以降、または SQL ログインを使う SQL Server）が必要です。次のいずれかの方法でインストールします。

- **Docker Compose:** [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) をダウンロードし、そのフォルダーで `docker compose up -d` を実行します。Web サーバーが起動するので、`https://<host>:8444/` を開いてセットアップウィザードに進みます。その後、ファイル内のノード（必要ならプロキシも）のコメントを外し、サーバーのアドレス、登録キー、録画フォルダーを入力して、もう一度 `docker compose up -d` を実行します。
- **Portainer スタック:** **Stacks → Add stack → Web editor** で、同じファイルを貼り付けて **Deploy the stack** を選びます。ウィザードのあと、スタックの **Editor** タブでノードのコメントを外し、**Update the stack** を選びます。Swarm ではなく、スタンドアロンの Docker 環境を使用してください。
- **Docker のみ:** Web サーバーを起動し、ウィザードを実行してから、ノードを起動します。

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

その後、**ノード**でノードを承認します。サーバーのアドレスには、同じマシン上であっても、`localhost` ではなく LAN のアドレスまたは名前を使用してください。マシンにファイアウォールがある場合は、TCP 8444（Web サーバー）、8554（ノード）、8555（ブラウザーに直接ストリーミングするノード）、4443（プロキシ）を許可し、HTTP/3 を使う場合は UDP 8444 も許可してください。コンテナーはユーザーとグループ 8444 で動作し、ホスト上のフォルダーもこの ID が所有します。

セットアップウィザードのノードのステップと、**メディアプロキシ**ページには、このサーバーのアドレス、登録キー、証明書が入力済みの、Portainer または `docker compose` 用の Compose スタックが表示されます。設定は、Linux のサービスが使うものと同じ環境変数です（`LARISVMS_SERVER_URL`、`LARISVMS_REGISTRATION_KEY`、`LARISVMS_STORAGE_ROOT` など）。`/var/lib/larisvms` はボリュームに置いてください。キー、登録、そしてコンテナーを再作成しても保存済みのパスワードを読み取れるようにするマシン ID が入っています。カメラの検出が機能するように、ホストネットワークを使用してください。更新するには、`docker compose pull` と `docker compose up -d` を実行するか、Portainer で **Re-pull image and redeploy** をオンにして **Update the stack** を選びます。Docker のみの場合は、イメージをプルし、コンテナーを削除して、そのコマンドをもう一度実行します。

**Linux のインストールを Docker に移す:** コンテナーを最初に起動する前に、データフォルダー（特に `keys`）とマシン ID をコピーし、その前にデータベースをバックアップしてください。データベースのパスワードとノードの登録はマシン ID で、データベース内のパスワードとノードのキーは `keys` で暗号化されています。これらがないと、**ノード**などのページにエラーが表示されます。Windows のインストールは、この方法では移せません。Web サーバーの場合:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

`/etc/larisvms/web.env` の設定をスタックの `environment` に移してから、コンテナーを起動します。ウィザードなしで、同じデータベースに接続します。古いサービスとコンテナーを同時に実行しないでください。ノードの場合、録画は今の場所に残します。

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

`/etc/larisvms/node.env` の設定をスタックの `environment` に移し、録画とアーカイブの各フォルダーを、コンテナー内の同じパスにマウントします（たとえば `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings` とし、`LARISVMS_STORAGE_ROOT` をそのパスに設定します）。ノードは、カメラと録画を保ったまま同じノードとして復帰し、登録キーも承認も必要ありません。プロキシも、録画を除いて同じ方法で移せます。そのあと古いパッケージを削除すると、ファイアウォールのポートが閉じられるため、コンテナー用にもう一度開いてください。

古いデータベースに対して、コンテナーですでにウィザードを実行した場合、足りないのはキーだけです。コンテナー自身のキーの隣にコピーし、両方を残してください。

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

ヘルプに Windows のフォルダーが書かれている箇所は、Linux では次のとおりです。`%ProgramData%\LarisVMS` → `/var/lib/larisvms`（その `logs` フォルダー → `/var/log/larisvms`）、インストールフォルダーの `appsettings.Production.json` と `setup-generated.json` → `/etc/larisvms`。`Restart-Service LarisVMSWeb` は `sudo systemctl restart larisvms-web` です。

## 更新

**アップグレード:** 新しい `.msi` を実行します。既存のインストールが検出され、そのまま**アップグレードの準備完了**に進みます。設定は記憶されているため、ドメインのサービスアカウントのパスワード以外は、再入力する必要がありません。ウェブサーバーは、起動時にデータベースの変更を自動的に適用します。アップグレードで、録画、構成ファイル、ノードの登録、データ保護キーに触れることはなく、インストールに失敗した場合はロールバックされます。

**ノードとプロキシは自動更新されます。** 新しいウェブサーバーのインストールでは、そこに含まれるノードとプロキシのビルドが、**ノード**の**ビルド**セクションに**保留中**として登録されます。承認待ちになるのは、最新のノードビルドと最新のプロキシビルドのみで、古いものは**置き換え済み**になります。**ビルド履歴**には、すべてのビルドが一覧表示されます。1 つを承認すると、それより古いすべてのノードが、次回のチェックイン時にダウンロード、検証、インストールを行います。サーバーはすべての更新に署名キーで署名します（**設定 → 暗号化と署名**をご覧ください）。ノードとプロキシは、登録時にそのキーを保存し、サーバーとの接続が改ざんされたとしても、そのキーで署名されていないものはインストールしません。これをオフにするには、**ノード → 更新と登録**の**自動更新**を使用します。

**新バージョン:** 1 日に 1 回（およびサービスの起動時）、サーバーが LarisVMS の GitHub リリースを確認します。新しいバージョンが公開されると、設定を変更できるユーザーに、上部バーにリリースノートへのリンク付きで **✨ バージョン X が利用可能です**と表示されます。お使いの環境に関する情報は送信されません。オフにするには、**ノード → 更新と登録**の**新バージョン**を使用します。

**Linux では**、新しいパッケージを同じ方法でインストールして Web サーバーをアップグレードします。`/etc/larisvms` と `/var/lib/larisvms` は保持されます。Linux のノードとプロキシは、ビルドを承認すると、Windows のものと同じようにサーバーから自動で更新されます。root として動作する小さなヘルパーが、サービスがダウンロードして検証した更新をインストールします。Linux サーバーは Windows のノードに Windows 用のビルドを、Windows サーバーは Linux のノードに Linux 用のビルドを提供します。

**アンインストール**すると、プログラム、そのサービス、ファイアウォールのルールが削除されます。録画、構成、`%ProgramData%\LarisVMS` は保持されます。Linux では、`apt remove` または `dnf remove` が同じことを行い、`/var/lib/larisvms`、`/var/log/larisvms`、`/etc/larisvms` は保持されます。

## ソースからのインストール

開発者は、リポジトリからビルドして、代わりに PowerShell でインストールすることもできます。`install-web.ps1`（サーバー）と、`build-node.ps1` / `build-proxy.ps1` が作成するパッケージ内の `install-node.ps1` / `install-proxy.ps1` スクリプトを使用します。`build-installers.ps1` は、3 つの MSI と、Linux の `.deb` および `.rpm` パッケージ（nFPM を使用）をビルドします。MSI は、スクリプトでインストールされたマシンをそのままアップグレードし、設定、登録、ダイレクトストリーミングのエンドポイントとそのファイアウォールのルールを維持します。

## 証明書

`appsettings.Production.json` の `Kestrel:Certificates:Default:Path` と `:Password` が、`.pfx` ファイルを指します（Linux では、`/etc/larisvms/web.env` の `Kestrel__Certificates__Default__Path` と `__Password` でも設定できます）。サーバーは 60 秒ごとにそれを確認し、更新された証明書を再起動なしで入れ替えます。

Docker では、証明書のフォルダーをマウントし（`/srv/larisvms/certs:/certs:ro`）、`/certs/vms.pfx` のようなコンテナー内のパスを使用します。ユーザー 8444 がそれを読み取れる必要があり、パスワード内の `$` は、Compose ファイルでは `$$` と書きます。読み込めない証明書は自己署名のものに置き換えられ、理由は `docker logs` に表示されます。

# 安装与升级

安装 Web 服务器、录制节点和媒体代理，并使它们保持最新。

每个部分都有自己的安装程序：`LarisVMS-Web`、`LarisVMS-Node` 和 `LarisVMS-Proxy`（均为 `.msi`）。双击即可按提示输入其设置，也可以在命令行中用 `msiexec /i <file>.msi NAME=value … /qn` 传入。未提供的内容会被询问，升级则会沿用上次安装的设置。

每个部分也都可以通过 `.deb` 或 `.rpm` 软件包在 Linux 上运行（参见 [Linux](#linux)），两者可以混用：Windows 服务器可以有 Linux 节点和代理，反之亦然。

## Web 服务器

1. 准备一个可从服务器访问的 SQL Server（集成安全或 SQL 身份验证），或一个 MariaDB 服务器（参见 [MariaDB](#mariadb)）。
2. 运行 `LarisVMS-Web.msi`。设置：
   - `HTTPSPORT`：HTTPS 端口（默认 8444）。安装程序会在防火墙中为 TCP 和 UDP（[HTTP/3](/Help/security#http3)）开放它。
   - `CERTPATH`、`CERTPASSWORD`：服务器证书（`.pfx`）。留空则在您拿到证书之前使用自签名证书。安装程序在继续之前会检查文件能否用该密码打开；文件共享还必须能被服务账户读取。
   - `SERVICEACCOUNT`、`SERVICEPASSWORD`：以特定账户运行服务，例如具有 SQL 集成安全权限的账户。留空则以 LocalSystem 运行。MariaDB 不需要。
3. 浏览到 `https://<host>:8444/`。安装向导会依次引导您完成数据库、第一个管理员账户、存储和节点注册。服务器本机或局域网内的浏览器会直接进入。从其他任何地方访问时，会首先要求输入服务器上 `%ProgramData%\LarisVMS\setup-token.txt` （Linux 上为 `/var/lib/larisvms/setup-token.txt`）中的安装令牌（仅管理员可读），因此互联网上的任何人都无法抢先进行安装。若要对所有其他计算机（包括局域网内的）都要求令牌，请在打开向导之前，在 `appsettings.Production.json` 中设置 `"Setup": { "AlwaysRequireToken": true }`。管理员账户创建后，该文件会被删除。

SQL 登录的密码或 MariaDB 密码在服务器上经过加密存储（Windows 上使用 DPAPI，Linux 上使用与计算机 ID 绑定的密钥；仅限此计算机），因此无法把 `setup-generated.json` 复制到另一台计算机后仍让密码生效。请改在那台计算机上重新运行向导。

如果您让向导指向一个现有的 LarisVMS 数据库（例如重新安装服务器之后），它会原样使用该数据库：必要时升级，不会初始化或覆盖任何内容，并跳过其余步骤。不是 LarisVMS 的数据库会被拒绝。安装完成后，无法再次打开向导。

服务器不需要 .NET 运行时；它已包含在内。证书之后也可以在安装文件夹的 `appsettings.Production.json` 中更改。同一路径上续期后的 `.pfx` 会在一分钟内被采用，无需重启。

## MariaDB

LarisVMS 可以使用 MariaDB 10.11 或更高版本来代替 SQL Server。在安装向导中选择 **MariaDB**，并输入主机、端口（默认 3306）、数据库名称，以及 MariaDB 用户和密码。MariaDB 没有 LarisVMS 可用的 Windows 登录，因此始终需要用户名和密码。

- **用户**需要有创建数据库的权限。或者您自己先用 `utf8mb4` 字符集创建它，并授予该用户对它的 `ALL` 权限：

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **加密**：连接使用 TLS。会校验服务器的证书，除非 MariaDB 就在此计算机上，或者您勾选了**信任此服务器的证书**（用于另一台计算机上的自签名证书）。
- **11.4 之前的 MariaDB 默认关闭 TLS**，直到为其提供证书为止，而没有 TLS 时，LarisVMS 不会把密码发送到另一台计算机。请将证书及其密钥添加到 `my.ini` 的 `[mysqld]` 部分，重启 MariaDB，如果是自签名的，还要勾选**信任此服务器的证书**：

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 及更高版本会自行设置证书。
- **备份**需要在 LarisVMS 服务器上安装 MariaDB 客户端工具（参见[备份](/Help/backups)）。
- 现有的 SQL Server 安装无法迁移到 MariaDB；MariaDB 仅用于新安装。

## 节点

在每台录制计算机上运行 `LarisVMS-Node.msi`。它需要：

- `SERVERURL` 和 `REGISTRATIONKEY`：服务器的地址，以及**节点 → 更新与注册**下的**注册密钥**。它们只在节点首次注册时使用。更改密钥不会影响现有节点。
- **FFmpeg**，未随附。请先用 `winget install ffmpeg --scope machine` 安装。安装程序会找到它，或者设置 `FFMPEGPATH`。
- `STORAGEROOT`：节点录制的位置。之后也可以在节点的编辑页面上设置。

可选：`ARCHIVEROOT`、`LIVEPORT`（默认 8554）、`LIVEALLOWFROM`（防火墙允许访问实时端口的地址，用 IP 或 CIDR 块表示，以逗号分隔：LarisVMS 服务器和所有代理；留空则允许任何地址）、`INSECURETLS=1`（不再需要：节点在首次使用时信任服务器的自签名证书，参见[服务器证书](/Help/nodes#server-certificate)）、直连流媒体端点（`CLIENTPORT`、`CLIENTENDPOINTHOST`、`CLIENTPFXPATH`、`CLIENTPFXPASSWORD`），以及用于可访问 SMB 存储的账户的 `SERVICEACCOUNT` / `SERVICEPASSWORD`。

对于 AI 检测，NVIDIA 计算机还需要 CUDA 12 和 cuDNN 9；在 Windows 上，安装程序发现它们时会将其 DLL 复制到位。参见 [AI 检测](/Help/ai-detection#hardware)。

安装后，节点会在**节点**下显示为**等待批准**。在您点击**批准**之前，它不会录制任何内容，也不会获得摄像头，因此只批准您自己安装的节点。然后从每台摄像头的编辑页面将摄像头分配给它。

## 媒体代理

运行 `LarisVMS-Proxy.msi`，并提供 `SERVERURL`、`REGISTRATIONKEY` 和 `CLIENTPORT`（默认 4443），以及与节点相同的可选证书设置。新代理一开始是关闭的：请在**设置 → 媒体代理**下为其开启**启用**，然后在每个节点的页面上选择它（参见[节点](/Help/nodes#proxies)）。

## Linux

Web 服务器、节点和代理通过 x86_64 的 `.deb` 软件包（Ubuntu 22.04、24.04、26.04，Debian 12、13）或 `.rpm` 软件包（Rocky Linux 9、10，以及其他 RHEL 9 或 10 的重新构建版）安装。它们各自作为 systemd 服务（`larisvms-web`、`larisvms-node`、`larisvms-proxy`）以专用的 `larisvms` 账户运行。程序位于 `/opt/larisvms`，设置位于 `/etc/larisvms`，数据（密钥、模型、安装令牌）位于 `/var/lib/larisvms`，日志文件位于 `/var/log/larisvms`。不需要 .NET 运行时。如果 firewalld 或 ufw 处于活动状态，安装时会开放服务的端口。

- **Web 服务器**：`sudo apt install ./larisvms-web_<version>-1_amd64.deb`（或 `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`）。它会立即启动；打开 `https://<host>:8444/` 进入安装向导。其端口和证书在 `/etc/larisvms/web.env` 中设置（`Kestrel__HttpsPort`、`Kestrel__Certificates__Default__Path`、`Kestrel__Certificates__Default__Password`；larisvms 账户必须能读取该文件），然后运行 `sudo systemctl restart larisvms-web`。`appsettings.Production.json` 和 `setup-generated.json` 也在 `/etc/larisvms` 中。在 Linux 上，SQL Server 需要 SQL 登录；选择 MariaDB 更简单。
- **节点**：安装 `larisvms-node`，然后用 `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>` 配对。安装向导的节点步骤会显示此命令，并已填好密钥和服务器证书的指纹。除非您加上 `--storage-root <folder>`，否则它会录制到 `/var/lib/larisvms/recordings`；网络共享必须先挂载（例如在 `/etc/fstab` 中），并且 larisvms 账户可以写入。其他选项：`--archive-root`、`--live-port`、`--live-allow-from`、`--client-port`、`--client-pfx-path`、`--client-endpoint-host` 和 `--ffmpeg-path`。`sudo larisvms-node setup --help` 会列出全部选项，再次运行 `setup` 可更改它们。
- **ffmpeg** 来自发行版，随节点一起安装。在 Rocky Linux 上它来自 RPM Fusion，因此请在安装节点之前启用 EPEL、CRB 和 RPM Fusion。
- **代理**：安装 `larisvms-proxy`，然后运行 `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`。

使用 `systemctl status larisvms-node` 和 `journalctl -u larisvms-node` 检查服务，使用 `larisvms-node --version` 显示其版本。

**Docker**：每次发布也会以镜像形式发布到 GitHub 的容器注册表：`ghcr.io/billacuda/larisvms-web`、`larisvms-node`（带有适用于 NVIDIA GPU 的 `-cuda` 和 `-tensorrt` 标签）和 `larisvms-proxy`。Web 服务器需要您自行运行的数据库（MariaDB 10.11 或更高版本，或使用 SQL 登录的 SQL Server）。请用以下方式之一安装：

- **Docker Compose**：下载 [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml)，并在其文件夹中运行 `docker compose up -d`。它会启动 Web 服务器；打开 `https://<host>:8444/` 进入安装向导。然后在该文件中取消节点（以及代理，如果需要）的注释，填写服务器地址、注册密钥和录像文件夹，并再次运行 `docker compose up -d`。
- **Portainer 堆栈**：在 **Stacks → Add stack → Web editor** 下粘贴同一个文件，然后选择 **Deploy the stack**。完成向导后，在堆栈的 **Editor** 选项卡中取消节点的注释，然后选择 **Update the stack**。请使用独立的 Docker 环境，而不是 Swarm。
- **纯 Docker**：启动 Web 服务器，运行向导，然后启动节点：

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

之后在**节点**中批准该节点。服务器地址请使用其局域网地址或名称，即使在同一台计算机上也不要使用 `localhost`。如果计算机有防火墙，请允许 TCP 8444（Web 服务器）、8554（节点）、8555（直接向浏览器传输流媒体的节点）和 4443（代理），如果使用 HTTP/3，还需允许 UDP 8444。容器以用户和组 8444 运行，其在主机上的文件夹也归该用户和组所有。

安装向导的节点步骤和**媒体代理**页面会显示适用于 Portainer 或 `docker compose` 的 Compose 堆栈，并已填好此服务器的地址、注册密钥和证书。这些设置与 Linux 服务使用的环境变量相同（`LARISVMS_SERVER_URL`、`LARISVMS_REGISTRATION_KEY`、`LARISVMS_STORAGE_ROOT` 等）。请将 `/var/lib/larisvms` 放在卷上：其中包含密钥、注册信息，以及在重新创建容器后使已保存的密码仍可读取的计算机 ID。请使用主机网络，以便摄像头发现能够工作。要更新，请运行 `docker compose pull` 和 `docker compose up -d`；在 Portainer 中，请开启 **Re-pull image and redeploy** 后选择 **Update the stack**；使用纯 Docker 时，请拉取镜像、移除容器，然后再次运行其命令。

**将 Linux 安装迁移到 Docker**：在容器首次启动之前，复制数据文件夹（最重要的是 `keys`）和计算机 ID，并先备份数据库。数据库密码和节点注册信息用计算机 ID 加密，数据库中的密码和节点密钥用 `keys` 加密；缺少它们时，**节点**等页面会显示错误。Windows 安装无法用这种方式迁移。对于 Web 服务器：

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

将 `/etc/larisvms/web.env` 中的设置移到堆栈的 `environment` 中，然后启动容器。它会连接到同一个数据库，无需向导。切勿同时运行旧服务和容器。对于节点，请将录像留在原处：

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

将 `/etc/larisvms/node.env` 中的设置移到堆栈的 `environment` 中，并将每个录像和归档文件夹挂载到容器内的相同路径（例如 `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`，并将 `LARISVMS_STORAGE_ROOT` 设为该路径）。节点会以原来的身份恢复，保留其摄像头和录像，不需要注册密钥或批准。代理以同样的方式迁移，只是没有录像。之后移除旧软件包会关闭防火墙中的端口，因此请为容器重新开放它们。

如果您已经在容器中针对旧数据库运行过向导，则只缺少密钥。请将它们复制到容器自己的密钥旁边，并保留两者：

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

帮助中提到的 Windows 文件夹，在 Linux 上对应如下：`%ProgramData%\LarisVMS` → `/var/lib/larisvms`（其 `logs` 文件夹 → `/var/log/larisvms`），安装文件夹中的 `appsettings.Production.json` 和 `setup-generated.json` → `/etc/larisvms`。`Restart-Service LarisVMSWeb` 对应 `sudo systemctl restart larisvms-web`。

## 更新

**升级**：运行较新的 `.msi`。它会找到现有安装并直接进入**准备升级**；设置会被记住，因此除了域服务账户的密码外，无需重新输入任何内容。Web 服务器会在启动时自行应用数据库更改。升级绝不会触碰录像、配置文件、节点注册或数据保护密钥，安装失败时会回滚。

**节点和代理会自动更新。** 新的 Web 安装会将其自带的节点和代理版本，作为**待处理**注册到**节点**的**版本**部分。只有最新的节点版本和最新的代理版本会等待批准；较旧的会变为**已取代**。**版本历史**列出每个版本。批准一个版本后，每个较旧的节点会在下次签到时下载、校验并安装它。服务器使用其签名密钥对每个更新签名（参见**设置 → 加密与签名**）；节点和代理会保留其注册时的那个密钥，并且即使它们与服务器的连接被篡改，也不会安装任何未用该密钥签名的内容。可在**节点 → 更新与注册**下用**自动更新**将其关闭。

**新版本**：服务器每天（以及服务启动时）会检查 LarisVMS 的 GitHub 发布页面。当有新版本时，可更改设置的人员会在顶部栏看到 **✨ 版本 X 可用**，并链接到其发行说明。不会发送任何有关您安装的信息。可在**节点 → 更新与注册**下用**新版本**将其关闭。

**在 Linux 上**，以同样的方式安装较新的软件包即可升级 Web 服务器；`/etc/larisvms` 和 `/var/lib/larisvms` 会被保留。批准版本后，Linux 节点和代理会像 Windows 上的一样从服务器自行更新：一个以 root 运行的小型辅助程序会安装服务已下载并验证的更新。Linux 服务器向 Windows 节点提供 Windows 版本，Windows 服务器向 Linux 节点提供 Linux 版本。

**卸载**会移除程序、其服务和防火墙规则。录像、配置和 `%ProgramData%\LarisVMS` 会被保留。在 Linux 上，`apt remove` 或 `dnf remove` 的作用相同，并保留 `/var/lib/larisvms`、`/var/log/larisvms` 和 `/etc/larisvms`。

## 从源代码安装

开发者也可以从代码仓库构建并改用 PowerShell 安装：`install-web.ps1`（服务器），以及由 `build-node.ps1` / `build-proxy.ps1` 构建的包中的 `install-node.ps1` / `install-proxy.ps1` 脚本。`build-installers.ps1` 构建这三个 MSI，以及 Linux 的 `.deb` 和 `.rpm` 软件包（使用 nFPM）。MSI 会就地升级通过脚本安装的计算机，并保留其设置、注册以及任何直连流媒体端点及其防火墙规则。

## 证书

`appsettings.Production.json` 中的 `Kestrel:Certificates:Default:Path` 和 `:Password` 指向一个 `.pfx` 文件（在 Linux 上，也可在 `/etc/larisvms/web.env` 中用 `Kestrel__Certificates__Default__Path` 和 `__Password` 设置）。服务器每 60 秒检查一次，并在不重启的情况下换入续期后的证书。

在 Docker 中，请挂载证书文件夹（`/srv/larisvms/certs:/certs:ro`），并使用容器内的路径，例如 `/certs/vms.pfx`。用户 8444 必须能读取它，密码中的 `$` 在 Compose 文件中要写成 `$$`。无法加载的证书会被替换为自签名证书，原因可在 `docker logs` 中查看。

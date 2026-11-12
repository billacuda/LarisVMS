# 安装与升级

安装 Web 服务器、录制节点和媒体代理，并使它们保持最新。

每个部分都有自己的安装程序：`LarisVMS-Web`、`LarisVMS-Node` 和 `LarisVMS-Proxy`（均为 `.msi`）。双击即可按提示输入其设置，也可以在命令行中用 `msiexec /i <file>.msi NAME=value … /qn` 传入。未提供的内容会被询问，升级则会沿用上次安装的设置。

## Web 服务器

1. 准备一个可从服务器访问的 SQL Server（集成安全或 SQL 身份验证），或一个 MariaDB 服务器（参见 [MariaDB](#mariadb)）。
2. 运行 `LarisVMS-Web.msi`。设置：
   - `HTTPSPORT`：HTTPS 端口（默认 8444）。安装程序会在防火墙中为 TCP 和 UDP（[HTTP/3](/Help/security#http3)）开放它。
   - `CERTPATH`、`CERTPASSWORD`：服务器证书（`.pfx`）。留空则在您拿到证书之前使用自签名证书。安装程序在继续之前会检查文件能否用该密码打开；文件共享还必须能被服务账户读取。
   - `SERVICEACCOUNT`、`SERVICEPASSWORD`：以特定账户运行服务，例如具有 SQL 集成安全权限的账户。留空则以 LocalSystem 运行。MariaDB 不需要。
3. 浏览到 `https://<host>:8444/`。安装向导会依次引导您完成数据库、第一个管理员账户、存储和节点注册。服务器本机或局域网内的浏览器会直接进入。从其他任何地方访问时，会首先要求输入服务器上 `%ProgramData%\LarisVMS\setup-token.txt` 中的安装令牌（仅管理员可读），因此互联网上的任何人都无法抢先进行安装。若要对所有其他计算机（包括局域网内的）都要求令牌，请在打开向导之前，在 `appsettings.Production.json` 中设置 `"Setup": { "AlwaysRequireToken": true }`。管理员账户创建后，该文件会被删除。

SQL 登录的密码或 MariaDB 密码在服务器上经过加密存储（DPAPI，仅限此计算机），因此无法把 `setup-generated.json` 复制到另一台计算机后仍让密码生效。请改在那台计算机上重新运行向导。

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

对于 AI 检测，NVIDIA 计算机还需要 CUDA Toolkit 12.x 和 cuDNN 9.x；安装程序发现它们时会将其 DLL 复制到位。参见 [AI 检测](/Help/ai-detection#hardware)。

安装后，节点会在**节点**下显示为**等待批准**。在您点击**批准**之前，它不会录制任何内容，也不会获得摄像头，因此只批准您自己安装的节点。然后从每台摄像头的编辑页面将摄像头分配给它。

## 媒体代理

运行 `LarisVMS-Proxy.msi`，并提供 `SERVERURL`、`REGISTRATIONKEY` 和 `CLIENTPORT`（默认 4443），以及与节点相同的可选证书设置。新代理一开始是关闭的：请在**设置 → 媒体代理**下为其开启**启用**，然后在每个节点的页面上选择它（参见[节点](/Help/nodes#proxies)）。

## 更新

**升级**：运行较新的 `.msi`。它会找到现有安装并直接进入**准备升级**；设置会被记住，因此除了域服务账户的密码外，无需重新输入任何内容。Web 服务器会在启动时自行应用数据库更改。升级绝不会触碰录像、配置文件、节点注册或数据保护密钥，安装失败时会回滚。

**节点和代理会自动更新。** 新的 Web 安装会将其自带的节点和代理版本，作为**待处理**注册到**节点**的**版本**部分。只有最新的节点版本和最新的代理版本会等待批准；较旧的会变为**已取代**。**版本历史**列出每个版本。批准一个版本后，每个较旧的节点会在下次签到时下载、校验并安装它。服务器使用其签名密钥对每个更新签名（参见**设置 → 加密与签名**）；节点和代理会保留其注册时的那个密钥，并且即使它们与服务器的连接被篡改，也不会安装任何未用该密钥签名的内容。可在**节点 → 更新与注册**下用**自动更新**将其关闭。

**新版本**：服务器每天（以及服务启动时）会检查 LarisVMS 的 GitHub 发布页面。当有新版本时，可更改设置的人员会在顶部栏看到**✨ 版本 X 可用**，并链接到其发行说明。不会发送任何有关您安装的信息。可在**节点 → 更新与注册**下用**新版本**将其关闭。

**卸载**会移除程序、其服务和防火墙规则。录像、配置和 `%ProgramData%\LarisVMS` 会被保留。

## 从源代码安装

开发者也可以从代码仓库构建并改用 PowerShell 安装：`install-web.ps1`（服务器），以及由 `build-node.ps1` / `build-proxy.ps1` 构建的包中的 `install-node.ps1` / `install-proxy.ps1` 脚本。`build-installers.ps1` 构建这三个 MSI。MSI 会就地升级通过脚本安装的计算机，并保留其设置、注册以及任何直连流媒体端点及其防火墙规则。

## 证书

`appsettings.Production.json` 中的 `Kestrel:Certificates:Default:Path` 和 `:Password` 指向一个 `.pfx` 文件。服务器每 60 秒检查一次，并在不重启的情况下换入续期后的证书。

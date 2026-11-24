# Installation & upgrades

Installing the web server, recorder nodes, and media proxies, and keeping them up to date.

Each part has its own installer: `LarisVMS-Web`, `LarisVMS-Node`, and `LarisVMS-Proxy` (`.msi`). Double-click one to be prompted for its settings, or pass them on the command line with `msiexec /i <file>.msi NAME=value … /qn`. Anything not given is asked for, and an upgrade reuses the previous install's settings.

Each part also runs on Linux, from a `.deb` or `.rpm` package (see [Linux](#linux)), and the two can be mixed: a Windows server can run Linux nodes and proxies, and the other way round.

## Web server

1. Have a SQL Server reachable from the server (Integrated Security or SQL Authentication), or a MariaDB server (see [MariaDB](#mariadb)).
2. Run `LarisVMS-Web.msi`. Settings:
   - `HTTPSPORT`: the HTTPS port (default 8444). The installer opens it in the firewall, for TCP and for UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: the server certificate (`.pfx`). Leave blank to use a self-signed certificate until you have one. The installer checks that the file opens with the password before continuing; a file share must also be readable by the service account.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: run the service as a specific account, for example one with SQL Integrated Security rights. Blank runs it as LocalSystem. Not needed for MariaDB.
3. Browse to `https://<host>:8444/`. The setup wizard walks through the database, the first admin account, storage, and node registration. A browser on the server itself or on the local network goes straight in. From anywhere else it first asks for the setup token in `%ProgramData%\LarisVMS\setup-token.txt` on the server (`/var/lib/larisvms/setup-token.txt` on Linux; readable by administrators only), so nobody on the internet can set it up first. To ask for the token from every other computer, local network included, set `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json` before you open the wizard. The file is deleted once the admin account exists.

A SQL login's password, or a MariaDB password, is stored encrypted on the server (DPAPI on Windows, or a key tied to the machine's ID on Linux; this machine only), so `setup-generated.json` can't be copied to another machine with the password still working. Run the wizard again there instead.

If you point the wizard at an existing LarisVMS database (for example after reinstalling the server), it uses that database as it is: upgraded if needed, nothing seeded or overwritten, and the remaining steps skipped. A database that isn't a LarisVMS one is refused. Once setup is complete, the wizard can't be opened again.

The server needs no .NET runtime; it's included. The certificate can also be changed later in `appsettings.Production.json` in the install folder. A renewed `.pfx` at the same path is picked up within a minute, without a restart.

## MariaDB

LarisVMS can use MariaDB 10.11 or later instead of SQL Server. In the setup wizard, choose **MariaDB** and enter the host, port (default 3306), database name, and a MariaDB user and password. MariaDB has no Windows sign-in for LarisVMS to use, so a user and password are always needed.

- **The user** needs permission to create the database. Or create it yourself first, with the `utf8mb4` character set, and grant the user `ALL` on it:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **Encryption:** the connection uses TLS. The server's certificate is checked unless MariaDB is on this machine, or you tick **Trust this server's certificate** (for a self-signed certificate on another machine).
- **MariaDB before 11.4 has TLS turned off** until it's given a certificate, and LarisVMS won't send the password to another machine without it. Add the certificate and its key to the `[mysqld]` section of `my.ini`, restart MariaDB, and tick **Trust this server's certificate** if it's self-signed:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 and later set up a certificate themselves.
- **Backups** need the MariaDB client tools on the LarisVMS server (see [Backups](/Help/backups)).
- An existing SQL Server installation can't be moved to MariaDB; MariaDB is for new installations.

## Nodes

Run `LarisVMS-Node.msi` on each recording machine. It needs:

- `SERVERURL` and `REGISTRATIONKEY`: the server's address and the **registration key** under **Nodes → Updates and registration**. They're only used the first time the node registers. Changing the key doesn't affect existing nodes.
- **FFmpeg**, which is not bundled. Install it first with `winget install ffmpeg --scope machine`. The installer finds it, or set `FFMPEGPATH`.
- `STORAGEROOT`: where the node records. It can also be set later on the node's edit page.

Optional: `ARCHIVEROOT`, `LIVEPORT` (default 8554), `LIVEALLOWFROM` (addresses allowed through the firewall to the live port, as IPs or CIDR blocks separated by commas: the LarisVMS server and any proxies; blank allows any), `INSECURETLS=1` (no longer needed: nodes trust the server's self-signed certificate on first use, see [Server certificate](/Help/nodes#server-certificate)), the direct-streaming endpoint (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`), and `SERVICEACCOUNT` / `SERVICEPASSWORD` for an account that can reach SMB storage.

For AI detection, NVIDIA machines also need CUDA 12 and cuDNN 9; on Windows the installer copies their DLLs into place when it finds them. See [AI detection](/Help/ai-detection#hardware).

After installing, the node appears under **Nodes** as **waiting for approval**. It records nothing and gets no cameras until you click **Approve**, so only approve a node you installed yourself. Then assign cameras to it from each camera's edit page.

## Media proxies

Run `LarisVMS-Proxy.msi` with `SERVERURL`, `REGISTRATIONKEY`, and `CLIENTPORT` (default 4443), plus the same optional certificate settings as a node. A new proxy starts turned off: turn on **Enabled** for it under **Settings → Media proxies**, then choose it on each node's page (see [Nodes](/Help/nodes#proxies)).

## Linux

The web server, nodes, and proxies install from `.deb` packages (Ubuntu 22.04, 24.04, 26.04, Debian 12, 13) or `.rpm` packages (Rocky Linux 9, 10, and other RHEL 9 or 10 rebuilds), for x86_64. Each runs as a systemd service (`larisvms-web`, `larisvms-node`, `larisvms-proxy`) under its own `larisvms` account. The program goes in `/opt/larisvms`, settings in `/etc/larisvms`, data (keys, models, the setup token) in `/var/lib/larisvms`, and log files in `/var/log/larisvms`. No .NET runtime is needed. Installing opens the service's ports in firewalld or ufw when one is active.

- **Web server:** `sudo apt install ./larisvms-web_<version>-1_amd64.deb` (or `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`). It starts straight away; open `https://<host>:8444/` for the setup wizard. Its port and certificate are set in `/etc/larisvms/web.env` (`Kestrel__HttpsPort`, `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password`; the larisvms account must be able to read the file), then `sudo systemctl restart larisvms-web`. `appsettings.Production.json` and `setup-generated.json` are in `/etc/larisvms` too. SQL Server needs a SQL login on Linux; MariaDB is the simpler choice there.
- **Nodes:** install `larisvms-node`, then pair it with `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>`. The **Add node** page shows this command with the key and the server's certificate thumbprint filled in. It records to `/var/lib/larisvms/recordings` unless you add `--storage-root <folder>`; a network share must be mounted first (for example in `/etc/fstab`) and writable by the larisvms account. Other options: `--archive-root`, `--live-port`, `--live-allow-from`, `--client-port`, `--client-pfx-path`, `--client-endpoint-host`, and `--ffmpeg-path`. `sudo larisvms-node setup --help` lists them all, and running `setup` again changes them.
- **ffmpeg** comes from the distro and is installed with the node. On Rocky Linux it comes from RPM Fusion, so enable EPEL, CRB, and RPM Fusion before installing a node.
- **Proxies:** install `larisvms-proxy`, then `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`.

Check on a service with `systemctl status larisvms-node` and `journalctl -u larisvms-node`, and print its version with `larisvms-node --version`.

**Docker:** every release is also published as images on GitHub's container registry: `ghcr.io/billacuda/larisvms-web`, `larisvms-node` (with `-cuda` and `-tensorrt` tags for NVIDIA GPUs), and `larisvms-proxy`. The web server needs a database you run yourself (MariaDB 10.11 or later, or SQL Server with a SQL login). Install it in one of these ways:

- **Docker Compose:** download [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) and run `docker compose up -d` in its folder. It starts the web server; open `https://<host>:8444/` for the setup wizard. Then uncomment the node (and the proxy, if you want one) in the file, fill in the server's address, the registration key, and the recordings folder, and run `docker compose up -d` again.
- **Portainer stack:** under **Stacks → Add stack → Web editor**, paste the same file and choose **Deploy the stack**. After the wizard, uncomment the node in the stack's **Editor** tab and choose **Update the stack**. Use a standalone Docker environment, not a Swarm.
- **Plain Docker:** start the web server, run the wizard, then start the node:

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

Approve the node under **Nodes** afterward. For the server's address, use its LAN address or name, not `localhost`, even on the same machine. If the machine has a firewall, allow TCP 8444 (web server), 8554 (node), and 4443 (proxy), and UDP 8444 if you use HTTP/3. The containers run as user and group 8444, which owns their folders on the host.

The Node step of the setup wizard, and the **Media proxies** page, show a compose stack for Portainer or `docker compose` with this server's address, registration key, and certificate filled in. The settings are the same environment variables the Linux services use (`LARISVMS_SERVER_URL`, `LARISVMS_REGISTRATION_KEY`, `LARISVMS_STORAGE_ROOT`, and so on). Keep `/var/lib/larisvms` on a volume: it holds the keys, the registration, and the machine ID that keeps saved passwords readable when the container is recreated. Use host networking, so camera discovery works. To update, run `docker compose pull` and `docker compose up -d`, choose **Update the stack** with **Re-pull image and redeploy** on in Portainer, or with plain Docker, pull the image, remove the container, and run its command again.

**Moving a Linux install to Docker:** copy the data folder (above all `keys`) and the machine ID before the container's first start, and back up the database first. The database password and node registrations are encrypted with the machine ID, and the passwords and node keys in the database with `keys`; without them, pages such as **Nodes** show an error. A Windows install can't move this way. For the web server:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Move any settings from `/etc/larisvms/web.env` into the stack's `environment`, then start the container. It connects to the same database, without the wizard. Never run the old service and the container at the same time. For a node, leave the recordings where they are:

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Move the settings from `/etc/larisvms/node.env` into the stack's `environment`, and mount each recordings and archive folder at the same path inside the container (for example `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`, with `LARISVMS_STORAGE_ROOT` set to it). The node comes back as itself, with its cameras and recordings, and needs no registration key or approval. A proxy moves the same way, without the recordings. Removing the old packages afterward closes the ports in the firewall, so open them again for the containers.

If you already ran the wizard in the container against the old database, only the keys are missing. Copy them in next to the container's own, and keep both:

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

Wherever Help names a Windows folder, the Linux one is: `%ProgramData%\LarisVMS` → `/var/lib/larisvms` (its `logs` folder → `/var/log/larisvms`), and the install folder's `appsettings.Production.json` and `setup-generated.json` → `/etc/larisvms`. `Restart-Service LarisVMSWeb` is `sudo systemctl restart larisvms-web`.

## Updates

**Upgrading:** run the newer `.msi`. It finds the existing install and goes straight to **Ready to upgrade**; settings are remembered, so nothing needs to be entered again except a domain service account's password. The web server applies any database changes itself when it starts. Upgrades never touch recordings, configuration files, node registration, or the data-protection keys, and a failed install is rolled back.

**Nodes and proxies update themselves.** A new web install registers the node and proxy builds it carries as **Pending** in the **Builds** section of **Nodes**. Only the newest node build and the newest proxy build wait for approval; older ones are **Superseded**. **Build history** lists every build. Approve one, and every older node downloads, verifies, and installs it on its next check-in. The server signs every update with its signing key (see **Settings → Encryption and signing**); nodes and proxies keep that key from when they registered and install nothing that isn't signed with it, even if their connection to the server were tampered with. Turn this off with **Auto-update** under **Nodes → Updates and registration**.

**New versions:** once a day (and when the service starts) the server checks LarisVMS's GitHub releases. When a newer version is out, people who can change settings see **✨ Version X is available** in the top bar, linking to its release notes. Nothing about your install is sent. Turn it off with **New versions** under **Nodes → Updates and registration**.

**On Linux,** upgrade the web server by installing the newer package the same way; it keeps `/etc/larisvms` and `/var/lib/larisvms`. Linux nodes and proxies update themselves from the server like Windows ones, once you approve the build: a small helper running as root installs the update the service downloaded and checked. A Linux server offers Windows nodes their Windows build, and a Windows server offers Linux nodes theirs.

**Uninstalling** removes the program, its service, and firewall rules. Recordings, configuration, and `%ProgramData%\LarisVMS` are kept. On Linux, `apt remove` or `dnf remove` does the same, keeping `/var/lib/larisvms`, `/var/log/larisvms`, and `/etc/larisvms`.

## Installing from source

Developers can build and install from the repository with PowerShell instead: `install-web.ps1` (server), and the `install-node.ps1` / `install-proxy.ps1` scripts in the packages built by `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` builds the three MSIs, and the Linux `.deb` and `.rpm` packages (with nFPM). An MSI upgrades a script-installed machine in place, keeping its settings, registration, and any direct-streaming endpoint with its firewall rule.

## Certificates

`Kestrel:Certificates:Default:Path` and `:Password` in `appsettings.Production.json` point at a `.pfx` file (on Linux, also settable in `/etc/larisvms/web.env` as `Kestrel__Certificates__Default__Path` and `__Password`). The server checks it every 60 seconds and swaps in a renewed certificate without a restart.

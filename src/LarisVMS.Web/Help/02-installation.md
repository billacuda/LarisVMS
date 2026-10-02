# Installation & upgrades

Installing the web server, recorder nodes and media proxies, and keeping them up to date.

Each part has its own installer: `LarisVMS-Web`, `LarisVMS-Node` and `LarisVMS-Proxy` (`.msi`). Double-click one to be prompted for its settings, or pass them on the command line with `msiexec /i <file>.msi NAME=value … /qn`. Anything not given is asked for, and an upgrade reuses the previous install's settings.

## Web server

1. Have a SQL Server reachable from the server (Integrated Security or SQL Authentication).
2. Run `LarisVMS-Web.msi`. Settings:
   - `HTTPSPORT`: the HTTPS port (default 8444). The installer opens it in the firewall.
   - `CERTPATH`, `CERTPASSWORD`: the server certificate (`.pfx`). Leave blank to use a self-signed certificate until you have one.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: run the service as a specific account, for example one with SQL Integrated Security rights. Blank runs it as LocalSystem.
3. Browse to `https://<host>:8444/`. The setup wizard walks through the database, the first admin account, storage and node registration.

The server needs no .NET runtime; it's included. The certificate can also be changed later in `appsettings.Production.json` in the install folder. A renewed `.pfx` at the same path is picked up within a minute, without a restart.

## Nodes

Run `LarisVMS-Node.msi` on each recording machine. It needs:

- `SERVERURL` and `REGISTRATIONKEY`: the server's address and the **registration key** from **Settings → Node defaults**. They're only used the first time the node registers. Changing the key doesn't affect existing nodes.
- **FFmpeg**, which is not bundled. Install it first with `winget install ffmpeg --scope machine`. The installer finds it, or set `FFMPEGPATH`.
- `STORAGEROOT`: where the node records. It can also be set later on the node's edit page.

Optional: `ARCHIVEROOT`, `LIVEPORT` (default 8554), `INSECURETLS=1` (accept the server's self-signed certificate), the direct-streaming endpoint (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`), and `SERVICEACCOUNT` / `SERVICEPASSWORD` for an account that can reach SMB storage.

For AI detection, NVIDIA machines also need the CUDA Toolkit 12.x and cuDNN 9.x; the installer copies their DLLs into place when it finds them. See [AI detection](/Help/ai-detection#hardware).

After installing, the node appears under **Settings → Nodes**. Assign cameras to it from each camera's edit page.

## Media proxies

Run `LarisVMS-Proxy.msi` with `SERVERURL`, `REGISTRATIONKEY` and `CLIENTPORT` (default 4443), plus the same optional certificate settings as a node. Then choose it on each node's page (see [Nodes](/Help/nodes#proxies)).

## Updates

**Upgrading:** run the newer `.msi`. Settings are remembered, so nothing needs to be entered again except a custom service account's password. The web server applies any database changes itself when it starts. Upgrades never touch recordings, configuration files, node registration or the data-protection keys.

**Nodes and proxies update themselves.** A new web install registers the node and proxy builds it carries as **Pending** under **Settings → Node builds**. Approve one, and every older node downloads, verifies and installs it on its next check-in. Turn this off with **Auto-update** under **Settings → Node defaults**.

**Uninstalling** removes the program, its service and firewall rules. Recordings, configuration and `%ProgramData%\LarisVMS` are kept.

## Installing from source

Developers can build and install from the repository with PowerShell instead: `install-web.ps1` (server), and the `install-node.ps1` / `install-proxy.ps1` scripts in the packages built by `build-node.ps1` / `build-proxy.ps1`. `build-installers.ps1` builds the three MSIs. An MSI upgrades a script-installed machine in place.

## Certificates

`Kestrel:Certificates:Default:Path` and `:Password` in `appsettings.Production.json` point at a `.pfx` file. The server checks it every 60 seconds and swaps in a renewed certificate without a restart.

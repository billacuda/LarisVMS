# Installation & upgrades

Installing the web server and recorder nodes, and keeping them up to date.

## Web server

1. Have a SQL Server reachable from the server (Integrated Security or SQL Authentication).
2. Publish and install as a Windows service, as Administrator:

   ```powershell
   dotnet publish src\LarisVMS.Web\LarisVMS.Web.csproj -c Release -o publish\LarisVMS.Web
   .\install-web.ps1
   # Under a domain/service account (for SQL Integrated Security):
   .\install-web.ps1 -ServiceCredential (Get-Credential)
   ```

3. Edit `appsettings.Production.json` in the install folder with your certificate path and password, then `Restart-Service LarisVMSWeb`. Until then the site runs on a self-signed certificate.
4. Browse to `https://<host>:8444/`. The setup wizard walks through the database, the first admin account, storage and node registration.

To upgrade, run `.\install-web.ps1` again. It never touches recordings, `appsettings.Production.json`, `setup-generated.json` or the data-protection keys.

## Nodes

Install a recorder node on each recording machine with `install-node.ps1`, passing the **registration key** from **Settings → Node defaults**. The key is only checked the first time a node registers. Changing it doesn't affect existing nodes, but it does invalidate old install commands.

Each node needs:

- **FFmpeg**, which is not bundled: `winget install ffmpeg --scope machine`. The node finds it on `PATH` or in the WinGet package store, and picks up upgrades automatically.
- A **storage path**, set at install time (`-StorageRoot`) or on the node's edit page.
- For AI detection only: a GPU driver, plus on NVIDIA the CUDA Toolkit 12.x and cuDNN 9.x. See [AI detection](/Help/ai-detection#hardware).

After installing, the node appears under **Settings → Nodes**. Assign cameras to it from each camera's edit page.

## Updates

Nodes and media proxies update themselves. Each `install-web.ps1` run registers a new build as **Pending** under **Settings → Node builds**. Approve it, and every older node downloads, verifies and installs it on its next check-in. Turn this off with **Auto-update** under **Settings → Node defaults**.

A node's *first* AI detection install still needs a manual `install-node.ps1` run. Auto-update only replaces files that are already there.

## Certificates

`Kestrel:Certificates:Default:Path` and `:Password` in `appsettings.Production.json` point at a `.pfx` file. The server checks it every 60 seconds and swaps in a renewed certificate without a restart.

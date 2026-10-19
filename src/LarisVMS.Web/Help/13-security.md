# Security

IP allow lists, a separate live/playback port, the strict script policy, certificates, and encrypted secrets.

## Allow lists

**Settings → Security** has two IP allow lists. Enter one IP or CIDR block per line (for example `10.0.0.5` or `10.0.0.0/24`); lines starting with `#` are comments. A blank list allows everyone.

- **Management / API**: the admin pages and the REST API. A list that would block the address you're saving from is refused, so you can't lock yourself out.
- **Live view / playback**: live video, playback, thumbnails, export downloads, and camera snapshots.

## Separate port

**Settings → Live view → Custom port** moves live and playback traffic onto its own HTTPS port, using the same certificate. This lets you firewall it separately, or expose only it outside the LAN. It takes effect after `Restart-Service LarisVMSWeb`. After that, video only answers on the new port and everything else only on the main port. Clear the field to go back to one port.

## HTTP/3

The server uses HTTP/2, and adds HTTP/3 on Windows 11, Windows Server 2022, or later. Browsers start on HTTP/2 and move to HTTP/3 on their own when they can reach it. HTTP/3 uses UDP on the HTTPS port (and the custom port, if set). The installer opens both TCP and UDP; on an older install, add a UDP rule for the port or run the current installer again. Until then, everything keeps working over HTTP/2. To turn HTTP/3 off, clear **Settings → Security → HTTP/3**, then `Restart-Service LarisVMSWeb`.

## Strict script policy

With **Settings → Security → Strict script policy** on, browsers run only the scripts this server sends, so script injected into a page can't run. While it's off, browsers report anything the strict policy would have blocked, and the latest reports appear under the switch. If no reports show up after you've used the pages you rely on, turn it on. Turning it on takes effect on the next page load. If something stops working, turn it off again and check the reports.

## Certificates

The server's certificate is a `.pfx` file set in `appsettings.Production.json`. A renewed file is picked up within a minute without a restart. Nodes using direct streaming have their own certificate setting on the node's page.

## Encrypted secrets

Camera credentials, SMB credentials, node keys, email secrets, the Entra client secret, and the Active Directory service-account password are encrypted in the database, using the data-protection keys in `%ProgramData%\LarisVMS\keys`. Back up that folder, and never delete it during a deploy.

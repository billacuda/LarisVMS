# Security

IP allow lists, a separate live/playback port, certificates and encrypted secrets.

## Allow lists

**Settings → Security** has two IP allow lists. Enter one IP or CIDR block per line (for example `10.0.0.5` or `10.0.0.0/24`); lines starting with `#` are comments. A blank list allows everyone.

- **Management / API**: the admin pages and the REST API. A list that would block the address you're saving from is refused, so you can't lock yourself out.
- **Live view / playback**: live video, playback, thumbnails, export downloads and camera snapshots.

## Separate port

**Settings → Live view → Custom port** moves live and playback traffic onto its own HTTPS port, using the same certificate. This lets you firewall it separately, or expose only it outside the LAN. It takes effect after `Restart-Service LarisVMSWeb`. After that, video only answers on the new port and everything else only on the main port. Clear the field to go back to one port.

## Certificates

The server's certificate is a `.pfx` file set in `appsettings.Production.json`. A renewed file is picked up within a minute without a restart. Nodes using direct streaming have their own certificate setting on the node's page.

## Encrypted secrets

Camera credentials, SMB credentials, node keys and email secrets are encrypted in the database, using the data-protection keys in `%ProgramData%\LarisVMS\keys`. Back up that folder, and never delete it during a deploy.

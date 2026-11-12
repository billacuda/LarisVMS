# Security

IP allow lists, a separate live/playback port, the strict script policy, certificates, host names, updates, encrypted secrets, and extra protection for businesses.

## Allow lists

**Settings → Security** has two IP allow lists. Enter one IP or CIDR block per line (for example `10.0.0.5` or `10.0.0.0/24`); lines starting with `#` are comments. A blank list allows everyone.

- **Management / API**: the admin pages and the REST API. A list that would block the address you're saving from is refused, so you can't lock yourself out.
- **Live view / playback**: live video, playback, thumbnails, export downloads, and camera snapshots.

## Separate port

**Settings → Live view → Custom port** moves live and playback traffic onto its own HTTPS port, using the same certificate. This lets you firewall it separately, or expose only it outside the LAN. It takes effect after `Restart-Service LarisVMSWeb`. After that, video only answers on the new port and everything else only on the main port. Clear the field to go back to one port.

## HTTP/3

The server uses HTTP/2, and adds HTTP/3 on Windows 11, Windows Server 2022, or later. Browsers start on HTTP/2 and move to HTTP/3 on their own when they can reach it. HTTP/3 uses UDP on the HTTPS port (and the custom port, if set). The installer opens both TCP and UDP; on an older install, add a UDP rule for the port or run the current installer again. Until then, everything keeps working over HTTP/2. To turn HTTP/3 off, clear **Settings → Security → HTTP/3**, then `Restart-Service LarisVMSWeb`.

## Strict script policy

With **Settings → Security → Strict script policy** on, browsers run only the scripts this server sends, so script injected into a page can't run. It's on by default, and upgrading turns it on. If something stops working, turn it off: browsers then report anything the strict policy would have blocked, and the latest reports appear under the switch. Once no reports show up after you've used the pages you rely on, turn it on again. A change takes effect on the next page load.

## Certificates

The server's certificate is a `.pfx` file set in `appsettings.Production.json`. A renewed file is picked up within a minute without a restart. Nodes using direct streaming have their own certificate setting on the node's page.

## Camera certificates

Cameras reached over HTTPS have self-signed certificates that can't be checked against anything, so by default any certificate is accepted. Turn on **Settings → Security → Pin camera certificates** to have the server remember each camera address's certificate the first time it connects, and refuse a different one from then on, on the server and on the nodes. Something on the network posing as a camera then can't collect its password or send false events.

Remembered certificates are listed under the setting. If a camera's certificate really changes (a firmware update, a reset, or a new certificate), select **Forget** next to its address; the next certificate it presents is remembered. Until then, each refused connection, on the server or a node, is recorded in the audit log and Super Admins are told (at most once an hour per camera address), since something on the network may be posing as the camera. A camera address is remembered when the server first reaches it, usually when the camera is added; a node accepts an address the server hasn't reached yet.

## Host names

By default the server answers whatever host name a browser uses, so it works by IP address, short name, and full name alike. To answer only the names you use, set `AllowedHosts` in `appsettings.Production.json` to them, separated by semicolons (for example `"AllowedHosts": "vms.example.com;vms;localhost"`), then `Restart-Service LarisVMSWeb`. Include every name that nodes, proxies, and people use to reach it: anything else gets an error.

Behind a reverse proxy (IIS ARR, nginx, and so on), list its address in `appsettings.Production.json` as `"ReverseProxy": { "KnownProxies": [ "10.0.0.2" ] }`, then restart. The server then takes the client's real address and scheme from that proxy's `X-Forwarded-For` and `X-Forwarded-Proto` headers, so allow lists and the audit log see the person, not the proxy. Those headers are ignored from anything not on the list, and entirely while the list is empty.

## Updates

These checks stand between a download and a running install:

- **Release downloads** come with `SHA256SUMS.txt`. Check an installer before running it with `Get-FileHash <file> -Algorithm SHA256` and compare it with the line for that file, or run `sha256sum -c SHA256SUMS.txt`. When the installers are code-signed, Windows also shows the publisher.
- **Node and proxy updates** are signed by this server's signing key. A node or proxy keeps that key from when it registered and installs nothing that isn't signed with it, even if its connection to the server were tampered with. It also refuses a build that isn't newer than the one it runs. See [Installation](/Help/installation#updates).
- **The publisher's signature** (optional): a node or proxy built with the publisher's key (`-PublisherKeyPath`) also needs the publisher's signature on every update; other builds skip this check. That key is kept offline and never on the server, so even a server that's been broken into can't push its own code to nodes.
- **The connection** to the server is checked as set under **Nodes → Updates and registration → Server certificate check** (see [Nodes](/Help/nodes#server-certificate)).

## Encrypted secrets

Camera credentials, SMB credentials, node keys, email secrets, the Entra client secret, and the Active Directory service-account password are encrypted in the database, using the data-protection keys in `%ProgramData%\LarisVMS\keys`. Only SYSTEM, Administrators, and the LarisVMS services' accounts can open `%ProgramData%\LarisVMS`: any other access found there, such as a folder a local user created before LarisVMS was installed, is removed when a service starts. Back up that folder, and never delete it during a deploy.

## Extra protection for businesses

Out of the box, LarisVMS is set up so a home install just works. Everything below is optional and off by default; turn on what fits a business, or a server reachable from the internet:

- **Require two-factor sign-in**: Settings → Security (see [Two-factor sign-in](/Help/users#two-factor-sign-in)).
- **Confirm password for admin pages**: Settings → Security (see [Sessions](/Help/users#sessions)).
- **Temporary admin-set passwords**: Settings → Security (see [Passwords](/Help/users#passwords)).
- **Shorter sessions and an idle timeout**: each role under Settings → Roles (see [Roles](/Help/users#roles)).
- **Pin camera certificates**: Settings → Security (see [Camera certificates](#camera-certificates)).
- **Allow lists**: Settings → Security (see [Allow lists](#allow-lists)).
- **The setup token from every other computer**: set `"Setup": { "AlwaysRequireToken": true }` in `appsettings.Production.json` before running the setup wizard (see [Installation](/Help/installation#web-server)).
- **Pin the server certificate when installing a node or proxy**: `-ServerCertThumbprint` (see [Server certificate](/Help/nodes#server-certificate)).
- **One-time registration codes, with the shared key turned off**: Nodes → Updates and registration (see [One-time registration codes](/Help/nodes#one-time-registration-codes)).
- **Encrypted connections from the server to nodes**: on each node's page (see [Encrypted connections from the server](/Help/nodes#encrypted-connections-from-the-server)).
- **Forward the audit log to syslog**: Settings → Logs (see [Audit log](/Help/users#audit-log)).
- **Publisher-signed updates**, for builds you make yourself (see [Updates](#updates)).
- **A less privileged service account.** The LarisVMS services run as LocalSystem. You can run them under another account (`services.msc`, or `sc.exe config`), but it's then up to you to give that account what the service needs: the install folder, every storage and archive folder or share, and the certificate files. `%ProgramData%\LarisVMS` is handled for you: each LarisVMS service, at startup, limits it to SYSTEM, Administrators, and the accounts the LarisVMS services run as, and removes any other access there. Check the service's log after the change.

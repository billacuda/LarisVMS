# Nodes

Recorder nodes: status, streaming paths, media proxies, failover, maintenance, and updates.

**Nodes** in the sidebar lists every node with its status, address, version, camera count, and disk usage. Click a node to edit its settings. The **⋯** menu has these actions:

- **Restart service**: restarts the node's Windows service, not the machine. Recording pauses for a few seconds.
- **Reset auth**: clears the node's check-in token, for a node stuck failing authentication after a missed heartbeat. It has no effect on a healthy node.
- **Maintenance**: moves the node's cameras to its backup node until you turn maintenance off.
- **Delete**: removes the node. Its cameras become unassigned. Recordings are not deleted.

A node is **online** if it checked in within the last 2 minutes. A ⚠️ **clock** warning means the node's clock is more than a minute off from the server's, so check its time sync.

## Direct streaming

By default all live and playback video is relayed through the web server (**Proxy**). With **Direct**, browsers connect straight to each node's own HTTPS endpoint, which takes load off the server. Set the mode under **Settings → Live view** and override it per node.

Direct mode needs a **client endpoint host** that browsers can reach, and a certificate (a `.pfx` path on the node's page). **Allow self-signed** is for testing only, because viewers get a browser warning. A camera whose node has no healthy endpoint falls back to the proxy automatically.

## Encrypted connections from the server

The server and media proxies reach each node's video and control routes over plain HTTP on its live port (8554), which is fine on a trusted network. To encrypt that traffic, turn on **Encrypt connections from the server** on the node's page, then restart the node once (**Restart**). The node starts its HTTPS endpoint (with its own self-signed certificate if it has no `.pfx`) and reports the certificate, which the server then pins: the server and proxies accept only that certificate from the node, and the media token is no longer part of the address. Once it's encrypted, the node's plain port answers only `/health`.

The **Encrypted** column on the Nodes page shows 🔒 for each node that's encrypted, and ⏳ for one that's turned on but still needs a restart. The status under the switch shows the certificate in use, or that the server is still waiting for it (until then, the server keeps using plain HTTP, so nothing stops working). It's off by default. Self-signed certificates used for this are never offered to browsers; direct streaming still needs a real one.

## Proxies

A **media proxy** is a separate relay (`LarisVMS.Proxy`) between browsers and nodes, for remote sites or low-bandwidth links. Register proxies under **Settings → Media proxies**, then choose a primary and a backup proxy on each node's page. Browsers use the first healthy one.

## Failover

Give a node a **backup recorder node**, and when it goes down its cameras are recorded by the backup until it comes back. “Down” is decided by a vote between the node's partner, the server, and its proxy. **Maintenance** moves its cameras over on purpose and holds them there.

The node page warns when the backup has different AI hardware, or is recording-only. In those cases detection may be slower, or paused, during a failover.

## Updates

Nodes and proxies update themselves from approved builds (see [Installation](/Help/installation#updates)). **Nodes → Updates and registration → Auto-update** turns this on or off for every node.

The **Builds** section of the Nodes page shows the build each kind of node is running toward and any build waiting for approval, with **Approve** and **Reject**. **Build history** lists every build, including approved, superseded, and rejected ones.

## Server certificate

**Nodes → Updates and registration → Server certificate check** decides how nodes and proxies check this server's HTTPS certificate:

- **Trust on first use** (the default): a certificate from a trusted CA always works, including renewed and replaced ones. A self-signed certificate is remembered the first time a node connects and keeps working for as long as it stays the same, even past its expiry date. If it changes, nodes and proxies refuse it until they're re-registered.
- **Trusted certificate only**: only a certificate from a trusted CA that matches the server's name. Install one before choosing this.
- **Don't check**: any certificate. Not safe: anyone on the network path could pose as the server.

With trust on first use, a node trusts whatever certificate it sees the first time it connects. To have it check from the very first connection, pass the thumbprint shown under **Server certificate check** to the installer: `-ServerCertThumbprint` for `install-node.ps1` and `install-proxy.ps1`, or `SERVERCERTTHUMBPRINT=` for the MSI. The install commands on the Setup and Media proxies pages already include it. Behind a reverse proxy, use the thumbprint of the certificate the reverse proxy presents instead.

The self-signed certificate the server makes for itself lasts 10 years and isn't replaced on its own. **Regenerate self-signed certificate** replaces it; every node and proxy then has to be re-registered, and the Nodes page lists the ones that haven't checked in since. To re-register a node, run the node installer again, or run `LarisVMS.Node.exe --reset-server-pin` on it and restart its service (proxies have the same option).

## One-time registration codes

New nodes and proxies register with the shared **registration key**, or with a one-time code. A one-time code registers a single node or proxy, then stops working. Unused, it expires after 1 hour, 24 hours, or 7 days. To make one, go to **Nodes → Updates and registration**, choose how long it lasts, and select **Create code**. The code is shown once. Use it wherever the installer asks for the registration key. **Revoke** cancels a code that hasn't been used yet.

The shared key works for every install until it's changed. Once you use one-time codes, clear **Accept the shared registration key** so a copy of the key in an old script or email can't register anything. Nodes and proxies already registered aren't affected. Either way, a new node still waits for approval before it gets any cameras.

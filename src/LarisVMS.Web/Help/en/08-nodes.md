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

The self-signed certificate the server makes for itself lasts 10 years and isn't replaced on its own. **Regenerate self-signed certificate** replaces it; every node and proxy then has to be re-registered, and the Nodes page lists the ones that haven't checked in since. To re-register a node, run the node installer again, or run `LarisVMS.Node.exe --reset-server-pin` on it and restart its service (proxies have the same option).

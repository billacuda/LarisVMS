# Nodes

Recorder nodes: status, streaming paths, media proxies, failover, maintenance and updates.

**Settings → Nodes** lists every node with its status, address, version, camera count and disk usage. Click a node to edit its settings. The **⋯** menu has these actions:

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

Give a node a **backup recorder node**, and when it goes down its cameras are recorded by the backup until it comes back. “Down” is decided by a vote between the node's partner, the server and its proxy. **Maintenance** moves its cameras over on purpose and holds them there.

The node page warns when the backup has different AI hardware, or is recording-only. In those cases detection may be slower, or paused, during a failover.

## Updates

Nodes and proxies update themselves from approved builds (see [Installation](/Help/installation#updates)). **Settings → Node defaults → Auto-update** turns this on or off for every node.

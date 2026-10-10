# Troubleshooting

Common problems, and where to look.

## Where the logs are

- **Server**: **Logs → System logs**. Set the level and retention under **Settings → Logging**. Debug and Trace add detail but make large files.
- **Nodes**: `%ProgramData%\LarisVMS\logs\node-*.log` on each node.
- **AI detection**: `%ProgramData%\LarisVMS\logs\vision-*.log` on each node.
- **Auto-update**: `%ProgramData%\LarisVMS\logs\updater-*.log` on each node or proxy.

## A camera isn't recording

1. Is it **assigned to a node**, and is that node **online**?
2. Does the node have a **storage path**? The node list flags nodes without one.
3. Is the camera **enabled**, and do its streams look right? **Re-probe** after any change on the camera itself.
4. In Motion, Schedule or Event mode, check whether footage is being discarded (see [Recording](/Help/recording#modes)).

## Live view and playback stay on “connecting”

When a node streams directly to browsers (see [Direct streaming](/Help/nodes#direct-streaming)), the browser connects to the node's client port, 4200 by default. If the browser's console shows a failed `wss://<node>:<port>/live/…` connection, check that the node's firewall allows that port: the installer creates an inbound rule named **LarisVMS Node Client Endpoint**. Installers before 0.215.1 could drop that rule when upgrading or taking over a node; run the current Node installer again, or add the rule by hand.

## Active Directory sign-in or sync fails

Use **Test connection** under **Settings → Active Directory**; the last sync's error is shown on the same page.

- **Can't reach a domain controller:** check that the domain name resolves from the server and that port 636 (LDAPS) or 389 (LDAP) is open to a domain controller. With LDAPS, the domain controller needs a certificate this server trusts.
- **The server's own identity was rejected:** integrated security only works when the LarisVMS Web service runs on a domain-joined server, as LocalSystem or a domain account. Otherwise use a service account.
- **"Isn't in any group that has access":** the user signed in correctly but isn't in a linked group. Groups are matched as security groups, including nested ones.
- **Locked out of the admin pages after turning off local sign-in:** see [Active Directory](/Help/users#active-directory) for the database command that turns it back on.

## Live view pauses in a background window

Chrome throttles windows that aren't visible, so a live wall on a second monitor can freeze and then catch up. Live view recovers on its own. To prevent it, add these to the Chrome shortcut's target:

```
--disable-backgrounding-occluded-windows --disable-background-timer-throttling
```

## Every tile freezes, then reconnects at once

The node stalled, not the browser or the network. Look in the node log for `Node process health: tick … ms late`. A healthy node logs this about once a minute with lateness in single-digit milliseconds.

- If `gcPauseMs` is close to the lateness, the cause is garbage collection.
- If `gcPauseMs` is small but `pendingWorkItems` is in the tens, threads are blocked. Capture a dump while it's stalling: `dotnet-dump collect -p <LarisVMS.Node pid>`.

## AI detection stops but recording carries on

Usually the GPU driver was reset. The Windows System event log shows `nvlddmkm` event 153 or `Display` event 4101 at that moment. The vision service restarts itself after 60 seconds without a result. Repeated resets point at the GPU, its driver, cooling or power.

## A node or proxy didn't come back after an update

Start its service (`sc start LarisVMSNode`, or `LarisVMSProxy`), then check `updater-*.log` for why the restart failed. A downloaded update that never got applied is also noted in the node's or proxy's own log when it next starts, and is offered again on a later check-in.

## A node shows “clock”

The node's clock is more than a minute off from the server's. Fix its time sync. Clock skew affects schedules and the timeline.

## A node keeps failing to check in

Use **Reset auth** from the node's **⋯** menu. It clears the node's check-in token, and the node re-syncs on its next heartbeat.

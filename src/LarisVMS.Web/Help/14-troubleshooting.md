# Troubleshooting

Common problems, and where to look.

## Where the logs are

- **Server**: **Logs → System logs**. Set the level and retention under **Settings → Logging**. Debug and Trace add detail but make large files.
- **Nodes**: `%ProgramData%\LarisVMS\logs\node-*.log` on each node.
- **AI detection**: `%ProgramData%\LarisVMS\logs\vision-*.log` on each node.

## A camera isn't recording

1. Is it **assigned to a node**, and is that node **online**?
2. Does the node have a **storage path**? The node list flags nodes without one.
3. Is the camera **enabled**, and do its streams look right? **Re-probe** after any change on the camera itself.
4. In Motion, Schedule or Event mode, check whether footage is being discarded (see [Recording](/Help/recording#modes)).

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

## A node shows “clock”

The node's clock is more than a minute off from the server's. Fix its time sync. Clock skew affects schedules and the timeline.

## A node keeps failing to check in

Use **Reset auth** from the node's **⋯** menu. It clears the node's check-in token, and the node re-syncs on its next heartbeat.

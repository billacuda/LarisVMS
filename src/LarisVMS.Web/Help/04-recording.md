# Recording

Recording modes, schedules, event tags, pre/post-roll, and segment length.

## Modes

Every camera records continuously to its node. The **recording mode** decides which footage is *kept*:

| Mode | Keeps |
|---|---|
| **Continuous** | Everything. |
| **Motion** | Footage around motion: from a motion zone, the camera's own motion events, an integration, or AI detection (see [Motion](/Help/motion#source)). |
| **Schedule** | Footage that starts inside an enabled schedule window. |
| **Event** | Footage around an event tag rule that has *Drives recording* turned on. |
| **Manual only** | Nothing, unless a [manual recording](#manual-recording) is on. The camera is live view only the rest of the time. |

Footage that isn't kept is deleted shortly after it's written, rather than waiting for retention.

Motion, Schedule, and Event modes **fail open**. Until a camera has a motion zone, schedule window, or driving rule, it keeps everything instead of discarding it. The camera's page warns when this is happening. Manual only doesn't fail open, because keeping nothing is what it's for.

Set the global mode under **Settings → Recording defaults**, and override it on a camera's page.

## Manual recording

A manual recording keeps a camera's footage until someone stops it, whatever the camera's recording mode is. Start one with the record button on a [live tile](/Help/live-and-playback#live-tiles) or through the [API](/Help/api#recording). It stays on through restarts and upgrades.

It works alongside the camera's mode. A Motion camera keeps footage when it sees motion *or* when a manual recording is on, and events still tag the timeline as usual. On a Continuous camera, it only marks the timeline.

- Footage is kept in whole segments, so up to a segment before and after the recording is saved too. Pre-roll and post-roll don't apply.
- Saved footage follows the camera's normal retention. Add an [evidence lock](/Help/live-and-playback#evidence-locks) to keep it longer.
- It shows on the timeline as a red band. Change the color under **Settings → Events**.
- A camera that's offline saves nothing until it's back. The recording stays on.
- Upgrade recorder nodes along with the server. An older node doesn't know about manual recordings.

## Pre-roll and post-roll

In Motion and Event modes, **pre-roll** is how much footage before activity is kept (so you see someone walk in), and **post-roll** is how much after it ends.

## Segment length

Footage is written as files of a fixed length (60 seconds by default). Playback has to download part of a segment before it can start at the exact moment you pick. Shorter segments make seeking faster on high-resolution cameras, but create more files. Changing the length briefly restarts the camera's recording.

## Schedule

A camera's **Schedule** page holds its windows: days, a start time, and an end time. An end time before the start time crosses midnight. Times use the **recorder node's local clock**. Windows only matter in Schedule mode.

## Event tags

A camera's **Event tags** page turns ONVIF events from the camera (motion, alarms, analytics, tamper) into colored marks on the timeline.

- **Start topic**: the event that starts the tag. Pick one from *Observed topics*, which lists everything the camera has actually sent.
- **Stop topic**: optional. Leave it blank when the start topic carries its own true/false state (most motion and alarm topics do). Set it when the camera sends separate start and stop events.
- **Drives recording**: in Event or Motion mode, footage around this tag is kept.

A tag ends only when its stop event arrives, never on a timeout.

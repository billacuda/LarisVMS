# Motion detection

Motion sources, server-side motion, zones and the motion grid.

## Source

A camera can report motion in several ways:

- **Server-side motion**: the node compares video frames inside motion zones or the motion grid.
- **Camera events**: the camera's own ONVIF motion events.
- **Vendor integration**: for example Dahua / Amcrest smart events.
- **AI detection**: a detected moving object.

The camera's **Motion source** chooses which *one* of these decides what Motion mode keeps, so the timeline doesn't get duplicate entries for the same event. **Auto** picks the richest one that is set up. Event tag rules and AI object labels always tag the timeline regardless of this choice.

## Server-side motion

**Server-side motion** on the camera's page turns the node's own pixel motion detection on or off. Keep it on to tag plain motion on the timeline as a fallback, even when another source gates recording. Turn it off to save the CPU it uses.

## Zones

A camera's **Zones** page has two methods. Only one is active at a time, and switching keeps the other's settings.

**Grid**: click cells to mask them out (red). Cells turn amber while there's motion in them, which helps you find a swaying branch or a flickering light to mask. Changing the grid size clears the mask.

**Polygon zones**:

- **Motion**: an area to watch.
- **Ignore**: an area to exclude (a tree, a public sidewalk). Pixels in an Ignore zone never count, even where it overlaps a Motion zone.
- **Camera-side motion** and **Privacy** zones can be saved but are not active yet.

**Sensitivity** is the percentage of the zone's pixels that must change. Lower is more sensitive. Wide zones need lower values, because real movement covers only a small part of them. Start low (around 3%) and raise it only if wind or lighting changes trigger it.

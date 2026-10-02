# Storage & retention

Where recordings live, how long they're kept, the watermark and the archive volume.

## Storage paths

Each node records to its own **storage path**, set on the node's edit page or at install time. There is no global path. A node without a storage path records nothing, and the node list flags it.

## Retention

**Retention** is how many days footage is kept. It's set globally under **Settings → Storage & retention**, and can be overridden per node and per camera; the most specific value wins. 0 keeps footage forever.

A camera can also have a **storage quota** in GB. When it goes over, its oldest footage is removed first.

## Watermark

The **watermark** is a hard limit on how full a node's disk may get (90% by default). Above it, the node archives its oldest footage early, or deletes it if archiving isn't available, until the disk is back under the limit. The node list shows **storage full** while this is happening.

## Archive

A node can have an optional second **archive path**, such as an SMB share or a USB drive. It must be a separate location from the storage path. With archiving on, footage that ages out of retention is **moved** to the archive instead of being deleted, and kept until the **archive retention** is reached. Archive retention is counted from the original recording date, and 0 keeps forever.

Playback, thumbnails and exports work the same from either volume. The node list shows free space and an estimate of days left for both volumes. The estimate is based on the last 24 hours of writing.

## Leftover footage

When a camera moves to another node or is removed, its old footage stays on the previous node until it ages out. The ⚠️ count next to a node's status shows how many cameras still have footage there. It clears on its own.

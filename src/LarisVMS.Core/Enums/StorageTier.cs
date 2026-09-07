namespace LarisVMS.Core.Enums;

/// <summary>Which storage location a recorded <see cref="LarisVMS.Core.Entities.Segment"/>'s file
/// currently lives on. Primary is the node's fast recording volume; Archive is a secondary,
/// typically slower volume (SMB share / USB drive) footage is moved to when primary retention would
/// otherwise delete it, kept there until the separate archive retention is hit. The node's media
/// endpoints serve a segment from either location transparently — this value is what the web side
/// uses for the archive-expiry query, the playback "archived" indicator, and per-volume storage
/// stats, not for locating the file.</summary>
public enum StorageTier : byte
{
    Primary = 0,
    Archive = 1,
}

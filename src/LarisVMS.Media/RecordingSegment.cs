namespace LarisVMS.Media;

/// <summary>A completed (or best-effort-finalized) recording segment, ready to be reported to the
/// control plane. StartUtc/EndUtc are observed wall-clock boundaries (when this segment's file was
/// created and when the next one was), not parsed from ffmpeg's own filename timestamps: those are
/// when ffmpeg opened the file (and, in footage from before 0.223.2, in the node's local time).</summary>
public record RecordingSegment(string FilePath, DateTime StartUtc, DateTime EndUtc, long SizeBytes);

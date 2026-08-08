namespace Rcordr.Media;

/// <summary>A completed (or best-effort-finalized) recording segment, ready to be reported to the
/// control plane. StartUtc/EndUtc are observed wall-clock boundaries (when this segment's file was
/// created and when the next one was), not parsed from ffmpeg's own filename timestamps — those are
/// in the local system timezone by default (-strftime), while everything else in Rcordr is UTC, so
/// tracking boundaries independently avoids a timezone-conversion foot-gun for no real benefit.</summary>
public record RecordingSegment(string FilePath, DateTime StartUtc, DateTime EndUtc, long SizeBytes);

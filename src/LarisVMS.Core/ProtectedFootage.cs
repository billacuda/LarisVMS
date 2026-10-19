using LarisVMS.Core.Dtos;

namespace LarisVMS.Core;

/// <summary>
/// Evidence-locked footage ranges, looked up by camera — what the node checks before every deletion
/// (StorageManager's passes and NodeWorker's recording-mode discard). Built once per sweep from
/// <see cref="NodeConfigResponse.ProtectedRanges"/>.
/// </summary>
public sealed class ProtectedFootage
{
    /// <summary>A segment file only carries its last-write time reliably (its name is in the node's
    /// local time, and an archive copy gets a new creation time), so a file is treated as covering
    /// [last write − this, last write]. Longer than any segment, so a file that could hold even part of
    /// a locked range is kept; keeping a few extra minutes of footage is harmless.</summary>
    public static readonly TimeSpan FileSpanAllowance = TimeSpan.FromMinutes(15);

    public static readonly ProtectedFootage None = new([]);

    private readonly Dictionary<Guid, List<(DateTime From, DateTime To)>> _byCamera;

    public ProtectedFootage(IEnumerable<NodeConfigProtectedRangeDto>? ranges)
    {
        _byCamera = (ranges ?? [])
            .GroupBy(r => r.CameraId)
            .ToDictionary(g => g.Key, g => g.Select(r => (r.FromUtc, r.ToUtc)).ToList());
    }

    public bool IsEmpty => _byCamera.Count == 0;

    /// <summary>True when any part of [startUtc, endUtc] lies inside a protected range of this camera.</summary>
    public bool Covers(Guid cameraId, DateTime startUtc, DateTime endUtc)
        => _byCamera.TryGetValue(cameraId, out var ranges) && ranges.Any(r => startUtc <= r.To && endUtc >= r.From);

    /// <summary>Whether a segment file whose last write was <paramref name="lastWriteUtc"/> may hold
    /// locked footage. See <see cref="FileSpanAllowance"/>.</summary>
    public bool CoversFile(Guid cameraId, DateTime lastWriteUtc)
        => Covers(cameraId, lastWriteUtc - FileSpanAllowance, lastWriteUtc);
}

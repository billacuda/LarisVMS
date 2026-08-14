using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One camera's worth of one ExportJob — the actual unit of work a recorder node executes (an ffmpeg
/// concat of that camera's segments over the job's [FromUtc, ToUtc), taken from ExportJob directly
/// rather than duplicated here). CameraId and NodeId are plain columns with no FK/navigation, the
/// same way Segment.NodeId has none — this is a historical/audit record of what was exported and
/// from where, not something that should cascade-delete or block a Camera/Node from being removed
/// later.
/// </summary>
public class ExportJobItem
{
    public Guid Id { get; set; }
    public Guid ExportJobId { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>The camera's owning node, snapshotted by ExportJobDispatcher the moment it picks
    /// this item up (not re-read later) — a camera could theoretically be reassigned to a different
    /// node mid-export, and the node that actually has the segment files on local disk is the one
    /// that must run the concat, regardless of where Camera.NodeId points by the time the export
    /// finishes. Null while Queued; stays null if the camera had no assigned node at dispatch time,
    /// in which case Status goes straight to Failed instead of Running.</summary>
    public Guid? NodeId { get; set; }

    public ExportItemStatus Status { get; set; } = ExportItemStatus.Queued;

    /// <summary>Node-local path to the finished mp4, under that node's {storageRoot}/exports/ — what
    /// the download proxy resolves a signed download token against. Null until Done.</summary>
    public string? OutputFilePath { get; set; }
    public long? OutputSizeBytes { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public ExportJob ExportJob { get; set; } = null!;
}

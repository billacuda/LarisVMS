using NidusVMS.Core.Enums;

namespace NidusVMS.Core.Entities;

/// <summary>
/// One registered recorder-node build (NidusVMS.Node.exe), offered to nodes for self-update once
/// approved. Direct port of dploid's AgentVersion, deliberately trimmed: no IsPilotOnly/staged-rollout
/// column — NidusVMS has no pilot-group concept for nodes the way dploid does for agents, so there's
/// nothing for a flag like that to gate here. One row per registered build, normally created by
/// deploy.ps1 directly against the database (see deploy.ps1's own "Registering node build" step) as
/// Pending, and only offered to a heartbeating node once an admin flips it to Approved on
/// Admin/NodeBuilds — NodeBuildService.GetLatestForPlatformAsync only ever considers Approved rows,
/// picking the newest by UploadedAt, and NidusVMS.Web's heartbeat handler compares that against the
/// reporting node's own version (NodeVersionComparer.IsNewer) to decide whether to hand back update
/// info.
/// </summary>
public class NodeBuildVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>e.g. "0.47.0" — must System.Version.TryParse, same constraint NodeVersionComparer
    /// relies on for both sides of the newer-than comparison.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>e.g. "win-x64" — matches the RID build-node.ps1 publishes
    /// (NidusVMS.Node is Windows-only for now; see NodeConfigStore's doc comment), and what
    /// NidusVMS.Node itself now reports at registration (see Node.Platform).</summary>
    public string Platform { get; set; } = "win-x64";

    /// <summary>Full path to the stored binary on this Web server's own disk — outside the IIS site
    /// directory entirely (see NodeBuildService.DefaultRoot's doc comment), so deploy.ps1's robocopy
    /// /MIR never touches it.</summary>
    public string FilePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the stored file, computed while it was streamed to disk on
    /// upload — what NidusVMS.Node's UpdateService verifies its download against before applying it.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public string? Notes { get; set; }

    public NodeBuildStatus Status { get; set; } = NodeBuildStatus.Pending;

    public DateTime? ApprovedAt { get; set; }

    /// <summary>The approving admin's display name/email, whichever ClaimTypes.Name resolves to —
    /// same "who did this" convention as View.OwnerId elsewhere, just a display string instead of a
    /// foreign key since this is purely an audit trail, never queried against.</summary>
    public string? ApprovedBy { get; set; }
}

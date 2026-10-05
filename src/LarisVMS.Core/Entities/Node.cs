using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// A recorder node — a separate Windows Service process (LarisVMS.Node) that owns FFmpeg-based
/// recording for whichever cameras are assigned to it (Camera.NodeId). Auth is a bearer secret
/// `{nodeId}:{secret}`, SHA-256 hashed and compared with fixed-time equality by NodeAuthMiddleware —
/// the same shape as dploid's AgentAuthMiddleware.
///
/// Replay hardening (LarisVMS failover plan, phase 5), ported from dploid's agent protocol:
/// <see cref="CheckInNonce"/> is a single-use rolling value issued in each heartbeat response and
/// echoed on the next request — a replayed heartbeat is rejected 401. <see cref="PreviousApiKeyHash"/>
/// is now actually written: on a rotation the current hash moves there and a fresh secret's hash
/// takes <see cref="ApiKeyHash"/>, giving one grace window where either authenticates
/// (<see cref="AuthenticateAsync"/> already accepted both). Automatic time-based rotation is gated on
/// <see cref="SecretRotationDays"/> (0 = never; the plumbing is armed but off by default), and an
/// admin can force one — or clear a wedged nonce — from the Nodes page ("Reset node auth"). A node
/// that loses its nonce (a heartbeat response that never arrived) resends the stale one and is locked
/// out until an admin clears it — the same accepted recovery-by-admin risk dploid's Agent.cs documents.
/// </summary>
public class Node
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Where this node records to — its own local disk or a UNC share. Storage config is
    /// per-node with no global default: set when the node is added (install-node.ps1 -StorageRoot,
    /// carried into NodeService.RegisterAsync) or on Admin/Nodes. A node with this unset is served
    /// no camera config by NodeService.GetConfigAsync and records nothing until an admin sets it.</summary>
    public string? StorageRootPath { get; set; }

    /// <summary>Optional secondary (SMB / USB) volume this node moves aged-out footage to when
    /// archiving is enabled. Per-node, no global default; null = this node doesn't archive. Must be a
    /// path outside <see cref="StorageRootPath"/>.</summary>
    public string? ArchiveRootPath { get; set; }

    public string ApiKeyHash { get; set; } = string.Empty;
    public string? PreviousApiKeyHash { get; set; }

    /// <summary>Replay hardening (failover plan phase 5a): the single-use nonce this node must echo on
    /// its next heartbeat. Issued fresh in every heartbeat response (NodeHeartbeatResponse.NextNonce)
    /// and validated with fixed-time equality; a request whose <c>Nonce</c> doesn't match is a replay
    /// (or a corrupted store) and gets 401. Null means "never issued one yet" — an older node build
    /// that doesn't send the field simply never gets replay protection, and a node that presents no
    /// nonce against a non-null stored one is treated as that same not-yet-upgraded case (allowed, and
    /// the stored value is cleared) rather than as a replay. Cleared on (re-)registration and by the
    /// admin "Reset node auth" action. Not encrypted: a rolling 16-byte value with a ~30s useful life
    /// and no standalone value, same as dploid's Agent.CheckInNonce.</summary>
    public string? CheckInNonce { get; set; }

    /// <summary>Replay hardening (failover plan phase 5b): when the current secret was last rotated.
    /// Null = never rotated (still the registration secret). Compared against <see cref="SecretRotationDays"/>
    /// on each authenticated heartbeat to decide whether to hand back a fresh secret.</summary>
    public DateTime? ApiKeyRotatedAt { get; set; }

    /// <summary>Replay hardening (failover plan phase 5b): rotate this node's bearer secret once it is
    /// this many days old. <c>0</c> (the default) disables automatic rotation entirely — the plumbing
    /// is in place but stays dormant until an operator opts a node (or the global default) in. An
    /// admin can always force one rotation immediately from the Nodes page regardless of this value.</summary>
    public int SecretRotationDays { get; set; }

    /// <summary>Replay hardening (failover plan phase 5b): set by the admin "Reset node auth" action
    /// (rotate-secret variant) so the node's very next authenticated heartbeat is handed a fresh
    /// secret regardless of <see cref="SecretRotationDays"/>. Cleared the moment that rotation is
    /// issued.</summary>
    public bool PendingSecretRotation { get; set; }

    /// <summary>Mirrors dploid's Agent.AllowReregistration. Set by the admin "Reset node auth" action
    /// as an intent flag alongside clearing the nonce; the reclaim-an-existing-row path in
    /// RegisterAsync that would consume it is not wired up in this pass (the common recovery — a
    /// wedged nonce on a node that still has its NodeId+Secret — is handled by the nonce clear alone),
    /// so for now it is audit/telemetry only.</summary>
    public bool AllowReregistration { get; set; }

    public string? Version { get; set; }
    public string? Platform { get; set; }
    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The remote IP the node last authenticated from (register or heartbeat), captured
    /// server-side from the connection rather than self-reported — a node can't spoof what it
    /// doesn't get to say. Useful for spotting a node on the wrong subnet/VLAN or one whose IP
    /// changed unexpectedly.</summary>
    public string? LastIpAddress { get; set; }

    /// <summary>Free/total bytes on the storage root's volume, self-reported by the node on every
    /// heartbeat (it's the only side that can actually measure its own disk/SMB share). Drives the
    /// Admin/Nodes usage bar and the M4 "days of retention remaining" estimate.</summary>
    public long? StorageFreeBytes { get; set; }
    public long? StorageTotalBytes { get; set; }
    public DateTime? StorageStatsUpdatedAt { get; set; }

    /// <summary>Free/total bytes on the archive volume (<see cref="ArchiveRootPath"/> or the global
    /// Archive.RootPath), self-reported every heartbeat when an archive root is configured — drives
    /// the second Admin/Nodes usage bar and the per-volume "days of retention remaining" estimate.
    /// Null when no archive root is set or the node predates this field.</summary>
    public long? ArchiveFreeBytes { get; set; }
    public long? ArchiveTotalBytes { get; set; }
    public DateTime? ArchiveStatsUpdatedAt { get; set; }

    /// <summary>True while this node's primary volume is over the storage watermark — footage is
    /// being archived early (or, if archiving is unavailable, deleted early) to keep the disk under
    /// the limit. Set/cleared on every heartbeat; <see cref="StoragePressureSince"/> records when the
    /// current stretch of pressure began. Surfaced as a warning next to the node on Admin/Nodes.</summary>
    public bool StoragePressureActive { get; set; }
    public DateTime? StoragePressureSince { get; set; }

    /// <summary>How far this node's own OS clock disagrees with the web server's, measured every
    /// heartbeat: (web server's receive-time UtcNow) - (SentAtUtc the node stamped when building the
    /// request). Positive means the node's clock is behind the server's, negative means ahead.
    /// Includes whatever network latency the heartbeat round trip had — negligible next to a real
    /// drift/DST bug (which reads in minutes or hours, not the sub-second latency a LAN heartbeat
    /// actually has), so this is precise enough to catch "NTP isn't running on this machine" without
    /// needing a real NTP client. Same diagnostic idea M8's ONVIF UtcTime/segment-time comparison
    /// already used to catch the camera-side DST bug (0.44.0/0.45.0), applied to the node's own OS
    /// clock instead of a camera's ONVIF layer.</summary>
    public double? ClockSkewSeconds { get; set; }
    public DateTime? ClockSkewMeasuredAt { get; set; }

    /// <summary>Host load from the latest heartbeat (NodeHostStats), for the dashboard: CPU percent busy,
    /// physical memory used/total, and network receive/send bytes per second across active adapters.
    /// All null from a node that predates this or can't measure it; HostStatsUpdatedAt is when the
    /// reading arrived.</summary>
    public double? CpuPercent { get; set; }
    public long? MemoryUsedBytes { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? NetReceiveBytesPerSec { get; set; }
    public long? NetSendBytesPerSec { get; set; }
    public DateTime? HostStatsUpdatedAt { get; set; }

    /// <summary>Port the node's own Kestrel host listens on for media (M5) — plain HTTP, LAN-only,
    /// reachable from LarisVMS.Web, which proxies browser live/playback traffic through it. Combined
    /// with LastIpAddress, this is the address LarisVMS.Web dials. Since the failover plan's phase 1 a
    /// browser can <em>also</em> be pointed straight at the node over HTTPS on
    /// <see cref="ClientEndpointReportedPort"/> — see the fields below.</summary>
    public int? LivePort { get; set; }

    // ── Failover plan phase 1: direct-to-node streaming ──────────────────────────
    // Admin-editable; nullable string/bool overrides inherit the matching global LiveView setting.

    /// <summary>Per-node override of the global <c>LiveView.DirectStreaming</c> toggle: the enum name
    /// <c>"Proxy"</c> (relay every byte through LarisVMS.Web, the original behaviour) or
    /// <c>"Direct"</c> (hand the browser the node's own HTTPS address so live/playback bytes skip the
    /// central hop). Null = inherit the global setting.</summary>
    public string? DirectStreamingMode { get; set; }

    /// <summary>Per-node override of the global <c>LiveView.AllowInsecureClientEndpoint</c> flag. When
    /// effectively true the node's client HTTPS endpoint may come up on an auto-generated self-signed
    /// certificate (setup/testing only — viewers click through a browser warning), and Web's health
    /// checks against it skip certificate validation. Null = inherit the global setting.</summary>
    public bool? AllowInsecureClientEndpoint { get; set; }

    /// <summary>The routable FQDN a browser uses to reach this node's client HTTPS endpoint directly —
    /// must match the certificate's CN/SAN. Admin-entered; null disables direct mode for this node
    /// regardless of the toggle (the ticket endpoint falls back to proxy).</summary>
    public string? ClientEndpointHost { get; set; }

    /// <summary>Filesystem path (on the node host, or a share it can read) to the <c>.pfx</c> the
    /// client HTTPS endpoint should present. Plain text — a path is not a secret. A local
    /// <c>client-endpoint.json</c> on the node host overrides this.</summary>
    public string? ClientCertPfxPath { get; set; }

    /// <summary>Password for <see cref="ClientCertPfxPath"/>. Encrypted at rest
    /// (EncryptedNullableStringConverter, same as <see cref="MediaSigningKey"/>); pushed to the node
    /// decrypted over the HTTPS control channel, exactly as camera credentials already are.</summary>
    public string? ClientCertPfxPassword { get; set; }

    // Heartbeat-reported by the node, informational — drives the Admin/Nodes status/badge and the
    // ticket endpoint's "is this node's client endpoint actually healthy" fallback check.

    /// <summary>The port the node reports its client HTTPS endpoint is actually listening on, or null
    /// if it isn't running one.</summary>
    public int? ClientEndpointReportedPort { get; set; }

    /// <summary>NotAfter of the certificate the node's client endpoint is currently serving. A value
    /// in the past (or null) means the ticket endpoint should not route clients here.</summary>
    public DateTime? ClientEndpointCertNotAfter { get; set; }

    /// <summary>True when that certificate is the node's auto-generated self-signed fallback rather
    /// than a real pfx — every ticket and the Admin node row flag the stream as insecure while so.</summary>
    public bool? ClientEndpointCertIsSelfSigned { get; set; }

    /// <summary>The last error the node hit standing up or reloading its client endpoint (bad pfx
    /// path, wrong password, bind failure), or null when it is healthy.</summary>
    public string? ClientEndpointLastError { get; set; }

    // ── Failover plan phase 2: media proxy assignment ────────────────────────────
    /// <summary>The media proxy browsers are routed through for this node's cameras, and a backup
    /// used when the primary is unhealthy. Null = no proxy (go direct to the node's client endpoint,
    /// phase 1, or through central). FKs to <see cref="MediaProxy"/>; a proxy delete nulls these.</summary>
    public Guid? PrimaryProxyId { get; set; }
    public MediaProxy? PrimaryProxy { get; set; }
    public Guid? BackupProxyId { get; set; }
    public MediaProxy? BackupProxy { get; set; }

    // ── Failover plan phase 3: recording failover to a backup node ───────────────

    /// <summary>The node that adopts this node's cameras for recording (and live/playback) while this
    /// node is <see cref="NodeFailoverState.FailedOverAway"/>. Null = this node has no backup, so its
    /// cameras simply stop recording if it goes down. Two nodes pointing at each other are mutual
    /// backups. Self-FK, <c>DeleteBehavior.NoAction</c> (a self-referencing nullable FK trips SQL
    /// Server's multiple-cascade-paths check); <see cref="Camera.BackupNodeIdOverride"/> overrides it
    /// per camera.</summary>
    public Guid? BackupNodeId { get; set; }
    public Node? BackupNode { get; set; }

    /// <summary>Recording-failover state, written only by <c>RecordingFailoverService</c> (quorum) or
    /// an admin maintenance toggle, read by the recording-node resolver. The hot path never does a
    /// live health check — it trusts this persisted value.</summary>
    public NodeFailoverState FailoverState { get; set; } = NodeFailoverState.Normal;

    /// <summary>When the current non-<see cref="NodeFailoverState.Normal"/> stretch began — for the
    /// Admin UI and audit. Null while <see cref="FailoverState"/> is Normal.</summary>
    public DateTime? FailoverSinceUtc { get; set; }

    /// <summary>Why this node is <see cref="NodeFailoverState.FailedOverAway"/> — quorum agreed it was
    /// offline, or an admin put it in maintenance. Null unless FailedOverAway.</summary>
    public NodeFailoverReason? FailoverReason { get; set; }

    /// <summary>Admin-set: this node is in maintenance. The quorum probe is skipped, its cameras move
    /// to the backup immediately (<see cref="NodeFailoverReason.Maintenance"/>), and they do not fail
    /// back until this is turned off — even if the node's own <c>/health</c> is green throughout.</summary>
    public bool MaintenanceMode { get; set; }
    public DateTime? MaintenanceSinceUtc { get; set; }

    /// <summary>Display name/email of the admin who last toggled maintenance — audit-trail string,
    /// same convention as <c>NodeBuildVersion.ApprovedBy</c>.</summary>
    public string? MaintenanceBy { get; set; }

    /// <summary>"Recording only" mode: when true, <c>NodeService.GetConfigAsync</c> forces
    /// <c>AiDetectionEnabled = false</c> on every camera DTO for this node — its own cameras and any
    /// it adopts during a failover — so the Vision Service never starts and a low-power backup node
    /// that suddenly carries many cameras keeps recording rather than collapsing under inference load.
    /// ServerMotion, ONVIF event tagging and recording itself are unaffected. Pure server-side DTO
    /// shaping, no node binary change.</summary>
    public bool DisableAiObjectDetection { get; set; }

    /// <summary>Failover plan phase 3: this node's <em>outgoing</em> quorum votes — a JSON object
    /// keyed by the subject node id, each value <c>{ serviceRunning, checkedAtUtc, detail }</c> from
    /// this node's own <c>/health</c> probe of the nodes it backs up. Written each heartbeat from
    /// <see cref="LarisVMS.Core.Dtos.NodeHeartbeatRequest.PartnerHealthReports"/>; read by
    /// <c>RecordingFailoverService</c> which scans every node's (and proxy's) outgoing votes for the
    /// subject it is evaluating. Not a secret — a stale-tolerant health opinion. Null until this node
    /// first reports one.</summary>
    public string? PartnerHealthReportsJson { get; set; }

    /// <summary>HMAC key used to sign the short-lived media tokens LarisVMS.Web issues for a live
    /// view request — generated once at registration, encrypted at rest like every other sensitive
    /// column. The node validates a token locally against this key with no DB round trip.</summary>
    public string? MediaSigningKey { get; set; }

    /// <summary>M17: JSON array of ffmpeg encoder names this node's own ffmpeg build/hardware
    /// actually offers (from `FfmpegCapabilityProber`), among the known set LarisVMS.Media checks
    /// for (libx264/libx265, h264_qsv/hevc_qsv, h264_nvenc/hevc_nvenc, h264_amf/hevc_amf) — probed
    /// once at node startup (hardware doesn't change while the process is running) and refreshed on
    /// every heartbeat like Version, not just once at registration, so a node upgrading its ffmpeg
    /// build or GPU driver is reflected without needing to re-register. Null for a node that hasn't
    /// reported yet or is running a pre-M17 build.</summary>
    public string? DetectedEncodersJson { get; set; }

    /// <summary>Object detection plan decision 2: admin-editable per-node override for which AI
    /// accelerator this node's LarisVMS.Vision.Service instance should use — the same
    /// override-or-resolve-automatically shape StorageRootPath already has. Null/Auto means resolve
    /// automatically from AccelCapabilityProber's own local probe (see AccelSelection); an explicit
    /// choice is honored even if it means no Vision Service instance runs (e.g. Nvidia chosen on a
    /// node with no NVIDIA GPU) — see NodeWorker's accelerator-resolution logic for the fallback.</summary>
    public AiAccelerator? AiAccelerator { get; set; }

    /// <summary>Object detection plan decision 2: JSON array of accelerators this node's own
    /// AccelCapabilityProber actually detected as available — self-reported every heartbeat, purely
    /// informational (drives the Admin UI's "here's what this node can actually see" readout), the
    /// same shape and same reasoning as DetectedEncodersJson above. Never itself the thing a node
    /// acts on — the node always uses its own freshly-probed local result, not a value fetched back
    /// from the server. Null for a node that hasn't reported yet or predates this field.</summary>
    public string? DetectedAcceleratorsJson { get; set; }

    public ICollection<Camera> Cameras { get; set; } = [];
}

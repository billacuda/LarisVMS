using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

public class Camera
{
    /// <summary>Object detection plan: whether this camera's node should watch it for AI object
    /// detection at all. Independent of MotionDetectionSource below — a camera can have AI
    /// detection enabled purely for its own always-on object tagging (decision 9) without AI ever
    /// being the primary source that gates Motion-mode recording. Requires the owning node to have
    /// resolved a usable accelerator (Node.AiAccelerator) — see NodeWorker.ReconcileVision.</summary>
    public bool AiDetectionEnabled { get; set; }

    /// <summary>Object detection plan decision 9: which single generic "something moved" signal
    /// (ServerMotion/CameraEvent/Integration/AiDetection) drives this camera's Motion-mode
    /// keep/discard decision and reports plain motion spans, when RecordingMode is Motion. Ignored
    /// entirely outside Motion mode. Null means "not yet configured" — NodeWorker's fallback
    /// (fail-open, same philosophy as every other "nothing configured yet" case in this app) is to
    /// behave like Continuous rather than silently discarding everything.</summary>
    public MotionDetectionSource? MotionDetectionSource { get; set; }

    /// <summary>Detection/hardware-acceleration overhaul, pass 0: whether NodeWorker runs a
    /// ServerMotion session for this camera at all, independent of MotionDetectionSource above.
    /// MotionDetectionSource only decides which source *gates Motion-mode recording* when several
    /// are configured — before this field existed, ServerMotion still ran and consumed CPU even when
    /// AiDetection (or another source) was the chosen gate, purely to keep tagging the timeline with
    /// plain motion as a fallback in case something triggered motion without AI detecting an object.
    /// That fallback is genuinely useful (RecordingMode != Motion always shows it; even in Motion
    /// mode with a different primary source, ShouldReportGenericMotion previously suppressed the
    /// *report* but the frame-diff itself still ran) — so this defaults to true, preserving every
    /// camera's current behavior on upgrade. Set false to stop the session outright and free the CPU
    /// it costs, on a camera where the fallback isn't wanted.</summary>
    public bool ServerMotionEnabled { get; set; } = true;

    /// <summary>Detection/hardware-acceleration overhaul pass 3c-2: which of the two mutually
    /// exclusive ways of answering "which pixels count as motion" is currently active — Polygon
    /// (Zone rows) or Grid (MotionGridSize/MotionGridMask below). Switching never deletes the
    /// inactive method's own configuration — every Zone row survives a switch to Grid, and
    /// MotionGridMask survives a switch back to Polygon — so a camera can have both set up and flip
    /// between them losslessly. Only ServerMotion/Ignore zones are affected; Privacy and CameraMotion
    /// zones keep working identically in either mode (see NodeWorker.ReconcileMotion's own doc
    /// comment for exactly how the two masking mechanisms combine).
    ///
    /// Defaults Grid (the user's own explicit call — simpler to get started with than drawing
    /// polygons) for a brand-new camera only. This default has no effect on an existing camera: the
    /// AddMotionGridColumns migration that introduced this column already backfilled every existing
    /// row to Polygon explicitly, preserving whatever zones it already had configured rather than
    /// silently switching a live camera to an empty, fully-unmasked grid.</summary>
    public MotionRegionMode MotionRegionMode { get; set; } = MotionRegionMode.Grid;

    /// <summary>Grid mode's own cell count per side (16/32/64 — enforced at the application layer,
    /// not the schema). Cells divide the frame evenly, so they're non-square on a non-square frame;
    /// see MotionGrid's own doc comment for the exact pixel-to-cell mapping. 32, not 16, is the
    /// default: on a typical outdoor camera a 16-cell grid gives cells coarse enough that one
    /// overhanging branch forces masking a much wider area (e.g. the driveway beneath it) than
    /// actually needed.</summary>
    public int MotionGridSize { get; set; } = 32;

    /// <summary>Base64-encoded bitset, MotionGridSize*MotionGridSize bits (row-major, cell (row,col)
    /// at bit row*MotionGridSize+col) — a set bit means that cell is masked (excluded from motion,
    /// the Grid-mode equivalent of an Ignore zone). Null/empty means nothing is masked (watch the
    /// whole frame) — see MotionGrid.Rasterize. Resized (and cleared) whenever MotionGridSize
    /// changes; changing size deliberately does not attempt to remap an existing mask onto a
    /// different cell count.</summary>
    public string? MotionGridMask { get; set; }

    /// <summary>Grid mode's own equivalent of a ServerMotion zone's Sensitivity — needed because
    /// Grid mode's single aggregate region (see NodeWorker.ReconcileMotion) still needs a threshold
    /// to compare its own frame score against before opening a span, the same way every Polygon zone
    /// already does with its own Sensitivity. Same 0.03 default Zone.Sensitivity itself uses, for the
    /// same reason (see Zone.Sensitivity's own doc comment for the real-camera-frame measurement
    /// behind that number).</summary>
    public double MotionGridSensitivity { get; set; } = 0.03;

    public Guid Id { get; set; }

    /// <summary>Owning recorder node. Nullable until Nodes exist (M3) — a camera can be registered
    /// and probed before it has anywhere to record to.</summary>
    public Guid? NodeId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int OnvifPort { get; set; } = 80;

    /// <summary>The ONVIF device service address (XAddr) returned by WS-Discovery or entered
    /// manually — the entry point every other ONVIF call is made relative to.</summary>
    public string DeviceServiceUri { get; set; } = string.Empty;

    /// <summary>Encrypted at rest via SecretProtection (EncryptedNullableStringConverter).</summary>
    public string? Username { get; set; }

    /// <summary>Encrypted at rest via SecretProtection (EncryptedNullableStringConverter).</summary>
    public string? Password { get; set; }

    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>Which vendor integration this camera needs beyond plain ONVIF, as
    /// ICameraIntegrationProvider.Key — set automatically from the make/model reported during
    /// probing (see CameraIntegrations.Detect), null for the majority of cameras that need nothing.
    /// Stored rather than re-derived on demand so a node's config generation doesn't depend on
    /// re-running detection, and so an unrecognized key from a newer/older version degrades to "no
    /// integration" instead of failing.</summary>
    public string? IntegrationKey { get; set; }

    /// <summary>Which physical sensor of a multi-sensor device (e.g. an Axis quad-lens) this camera
    /// row represents — the ONVIF VideoSourceToken its profiles draw from. Null for the ordinary
    /// single-sensor case, and for every camera added before multi-channel support existed: null
    /// means "use every profile this device reports", exactly the pre-existing behavior. When set,
    /// probing considers only the profiles belonging to this sensor, so several Camera rows can share
    /// one device (same Host/DeviceServiceUri/credentials) while each records its own lens
    /// independently — which is what lets the whole recording/View/Live/Playback pipeline treat a
    /// multi-sensor device as N ordinary cameras with no changes of its own.</summary>
    public string? VideoSourceToken { get; set; }

    public string? TimeZoneId { get; set; }

    /// <summary>Per-camera storage cap; null shares the pool with cameras that have no quota. See
    /// the plan's storage-manager section (M4) for eviction order.</summary>
    public long? QuotaBytes { get; set; }

    public CameraLensType LensType { get; set; } = CameraLensType.Standard;
    public string? DewarpConfigJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastProbedAt { get; set; }

    /// <summary>Every camera group this camera belongs to — a camera can be in any number of groups,
    /// but (enforced at the application layer, not the schema, by CameraGroupPolicy) all of them must
    /// share the same top-level ancestor: a camera belongs to exactly one Site, and any number of
    /// groups/sub-groups beneath it. Many-to-many (CameraGroupMemberships join table, EF-managed, no
    /// explicit entity class needed since it carries no columns of its own).</summary>
    public ICollection<CameraGroup> Groups { get; set; } = [];

    public Node? Node { get; set; }
    public CameraCapabilities? Capabilities { get; set; }
    public ICollection<CameraStream> Streams { get; set; } = [];
}

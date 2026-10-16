namespace LarisVMS.Core.Entities;

/// <summary>
/// The server's key for signing tamper evidence: chain checkpoints and export manifests. ECDSA P-256
/// with SHA-256. Created on first use; the private key is stored encrypted (SecretProtection) and the
/// public key in PEM, which anyone can use to check a signature. A retired key is kept so everything
/// it signed still verifies.
/// </summary>
public class SigningKey
{
    public Guid Id { get; set; }

    /// <summary>"ECDSA-P256-SHA256".</summary>
    public string Algorithm { get; set; } = "ECDSA-P256-SHA256";

    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>PKCS#8 private key, base64, encrypted at rest by the column converter.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RetiredAt { get; set; }
}

/// <summary>The signed manifest for one export item — the file the Verify export page checks the
/// exported video against. See ExportManifestBuilder for the format.</summary>
public class ExportManifest
{
    public Guid ExportJobItemId { get; set; }
    public string Json { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Where each camera's segment hash chain currently ends — the link the next recorded
/// segment chains onto. One row per camera, updated under a per-camera lock as segments arrive.</summary>
public class SegmentChainHead
{
    public Guid CameraId { get; set; }
    public long SegmentId { get; set; }
    public string ChainHash { get; set; } = string.Empty;
    public DateTime SegmentEndUtc { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A signed snapshot of one camera's chain head (see SegmentChain.CheckpointPayload). Taken
/// hourly when the chain has moved, and for every export manifest: rewriting recorded footage and its
/// hashes after the fact would also need these signatures, which need the server's private key.</summary>
public class ChainCheckpoint
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }
    public long SegmentId { get; set; }
    public string ChainHash { get; set; } = string.Empty;
    public DateTime SegmentEndUtc { get; set; }
    public DateTime SignedUtc { get; set; }
    public Guid KeyId { get; set; }

    /// <summary>Base64 IEEE P1363 (r‖s) signature of the checkpoint payload.</summary>
    public string Signature { get; set; } = string.Empty;
}

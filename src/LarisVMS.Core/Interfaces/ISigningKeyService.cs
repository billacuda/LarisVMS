namespace LarisVMS.Core.Interfaces;

/// <summary>One signature: P1363 (r‖s, what WebCrypto verifies) and DER (what openssl verifies), both
/// base64, plus the key that made it.</summary>
public record SignatureResult(Guid KeyId, string Algorithm, string PublicKeyPem, string Signature, string SignatureDer);

public record PublicSigningKey(Guid Id, string Algorithm, string PublicKeyPem, DateTime CreatedAt, DateTime? RetiredAt);

/// <summary>The server's tamper-evidence signing key (ECDSA P-256 / SHA-256): signs chain
/// checkpoints and export manifests. The key is created on first use.</summary>
public interface ISigningKeyService
{
    Task<SignatureResult> SignAsync(string payload, CancellationToken ct = default);

    /// <summary>Checks a P1363 signature made by the given key; false for an unknown key.</summary>
    Task<bool> VerifyAsync(Guid keyId, string payload, string signature, CancellationToken ct = default);

    Task<List<PublicSigningKey>> ListPublicKeysAsync(CancellationToken ct = default);

    /// <summary>The key that signs now (created on first use), PEM.</summary>
    Task<string> GetActivePublicKeyPemAsync(CancellationToken ct = default);
}

using System.Security.Cryptography;
using System.Text;

namespace LarisVMS.Core.Security;

/// <summary>
/// Signatures on what the server tells nodes and proxies to install: an update (the binary's and the
/// Vision Service's SHA-256) or a native library. The server signs with its ECDSA P-256 signing key
/// (SigningKeyService); a node or proxy keeps the server's public key from registration (or, for one
/// registered before this, from its first signed offer) and refuses anything unsigned or signed by a
/// different key. The download's hash is checked against the signed one, so a swapped binary and a
/// swapped hash are both caught, even when certificate checks are off.
/// </summary>
public static class UpdateSignature
{
    public static string UpdatePayload(string platform, string version, string sha256, string? visionSha256) =>
        $"larisvms-update:v1|{platform}|{version}|{sha256.ToLowerInvariant()}|{visionSha256?.ToLowerInvariant() ?? ""}";

    public static string NativePayload(string name, string sha256) =>
        $"larisvms-native:v1|{name}|{sha256.ToLowerInvariant()}";

    /// <summary>P1363 (r‖s) signature, base64, against a PEM public key.</summary>
    public static bool Verify(string publicKeyPem, string payload, string signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether to trust an offer. <paramref name="keyToPin"/> is set when nothing was pinned
    /// yet and the offer verified with the key it came with: the caller saves it.</summary>
    public static bool Check(string? pinnedKeyPem, string? offeredKeyPem, string? signature, string payload,
        out string? keyToPin, out string error)
    {
        keyToPin = null;
        error = "";
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(offeredKeyPem))
        {
            error = "the offer isn't signed";
            return false;
        }
        if (pinnedKeyPem is not null && Normalize(pinnedKeyPem) != Normalize(offeredKeyPem))
        {
            error = "it's signed by a different key than this machine trusts (re-register it if the server's signing key really changed)";
            return false;
        }
        if (!Verify(pinnedKeyPem ?? offeredKeyPem, payload, signature))
        {
            error = "the signature doesn't match";
            return false;
        }
        if (pinnedKeyPem is null) keyToPin = offeredKeyPem;
        return true;
    }

    private static string Normalize(string pem) => string.Concat(pem.Where(c => !char.IsWhiteSpace(c)));
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LarisVMS.Infrastructure.Notifications;

/// <summary>
/// Standard Web Push, done with the framework's own crypto — no third-party library:
/// <list type="bullet">
/// <item>VAPID (RFC 8292): the server's P-256 key pair, and the ES256-signed JWT that identifies it to
/// the browser vendor's push service.</item>
/// <item>Message encryption (RFC 8291, "aes128gcm" content coding from RFC 8188): ECDH with the
/// browser's key, HKDF with its auth secret, AES-128-GCM.</item>
/// </list>
/// Base64url everywhere, as the browser's PushSubscription and applicationServerKey use it.
/// </summary>
public static class WebPushProtocol
{
    /// <summary>The record size written into the aes128gcm header. A notification is one record.</summary>
    private const int RecordSize = 4096;

    public sealed record VapidKeys(string PublicKey, string PrivateKey);

    /// <summary>A new VAPID key pair: the public key as the 65-byte uncompressed point the browser's
    /// applicationServerKey expects, the private key as the 32-byte scalar.</summary>
    public static VapidKeys GenerateVapidKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        return new VapidKeys(Base64Url(UncompressedPoint(p.Q)), Base64Url(p.D!));
    }

    /// <summary>The Authorization header value for one push request: <c>vapid t=&lt;jwt&gt;, k=&lt;public key&gt;</c>.
    /// The JWT's audience is the push service's origin; it is valid for 12 hours.</summary>
    public static string VapidAuthorization(Uri endpoint, string subject, VapidKeys keys, DateTime nowUtc)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = new DateTimeOffset(nowUtc.AddHours(12)).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + claims);

        using var ecdsa = ImportPrivateKey(keys);
        // ECDsa.SignData's default output is the IEEE P1363 r||s form that JWS ES256 requires.
        var signature = ecdsa.SignData(signingInput, HashAlgorithmName.SHA256);
        return $"vapid t={header}.{claims}.{Base64Url(signature)}, k={keys.PublicKey}";
    }

    /// <summary>Encrypts <paramref name="payload"/> for one browser subscription (RFC 8291), with a
    /// fresh ephemeral key and salt.</summary>
    public static byte[] Encrypt(byte[] payload, string browserPublicKeyP256dh, string browserAuthSecret)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(payload, FromBase64Url(browserPublicKeyP256dh), FromBase64Url(browserAuthSecret),
            ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Deterministic core of <see cref="Encrypt(byte[], string, string)"/>, for tests.</summary>
    internal static byte[] Encrypt(byte[] payload, byte[] uaPublic, byte[] authSecret, ECDiffieHellman asKey, byte[] salt)
    {
        if (payload.Length > RecordSize - 17 - 86)
            throw new ArgumentException("Push payload is too large for one record.", nameof(payload));

        var asPublic = UncompressedPoint(asKey.ExportParameters(false).Q);
        var (cek, nonce) = DeriveKeys(asKey, uaPublic, asPublic, authSecret, salt, isSender: true);

        // One record, the last: payload followed by the 0x02 delimiter, no extra padding.
        var plaintext = new byte[payload.Length + 1];
        payload.CopyTo(plaintext, 0);
        plaintext[^1] = 0x02;

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Header: salt (16) | record size (uint32 BE) | key id length (1) | key id = sender public key (65).
        var body = new byte[16 + 4 + 1 + asPublic.Length + ciphertext.Length + tag.Length];
        var offset = 0;
        salt.CopyTo(body, offset); offset += 16;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset, 4), RecordSize); offset += 4;
        body[offset++] = (byte)asPublic.Length;
        asPublic.CopyTo(body, offset); offset += asPublic.Length;
        ciphertext.CopyTo(body, offset); offset += ciphertext.Length;
        tag.CopyTo(body, offset);
        return body;
    }

    /// <summary>The receiving side of RFC 8291 — what a browser does. Used by the tests to check
    /// <see cref="Encrypt(byte[], byte[], byte[], ECDiffieHellman, byte[])"/>.</summary>
    internal static byte[] Decrypt(byte[] body, ECDiffieHellman uaKey, byte[] authSecret)
    {
        var salt = body[..16];
        var idLength = body[20];
        var asPublic = body[21..(21 + idLength)];
        var record = body[(21 + idLength)..];
        var uaPublic = UncompressedPoint(uaKey.ExportParameters(false).Q);

        using var asKey = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = PointFrom(asPublic) });
        var (cek, nonce) = DeriveKeys(uaKey, asPublic, uaPublic, authSecret, salt, isSender: false, peer: asKey);

        var ciphertext = record[..^16];
        var tag = record[^16..];
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, ciphertext, tag, plaintext);
        var end = Array.LastIndexOf(plaintext, (byte)0x02);
        return plaintext[..end];
    }

    private static (byte[] Cek, byte[] Nonce) DeriveKeys(ECDiffieHellman own, byte[] peerPublic, byte[] ownPublic,
        byte[] authSecret, byte[] salt, bool isSender, ECDiffieHellman? peer = null)
    {
        byte[] sharedSecret;
        if (peer is not null)
        {
            sharedSecret = own.DeriveRawSecretAgreement(peer.PublicKey);
        }
        else
        {
            using var peerKey = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = PointFrom(peerPublic) });
            sharedSecret = own.DeriveRawSecretAgreement(peerKey.PublicKey);
        }

        // key_info = "WebPush: info" || 0x00 || ua_public || as_public
        var uaPublic = isSender ? peerPublic : ownPublic;
        var asPublic = isSender ? ownPublic : peerPublic;
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);

        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        return (cek, nonce);
    }

    private static ECDsa ImportPrivateKey(VapidKeys keys)
        => ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = FromBase64Url(keys.PrivateKey),
            Q = PointFrom(FromBase64Url(keys.PublicKey)),
        });

    private static byte[] UncompressedPoint(ECPoint q) => Concat([0x04], q.X!, q.Y!);

    private static ECPoint PointFrom(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04) throw new ArgumentException("Expected an uncompressed P-256 point.");
        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] };
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}

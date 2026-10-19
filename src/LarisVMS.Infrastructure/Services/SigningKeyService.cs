using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>The tamper-evidence signing key. ECDSA P-256 over SHA-256; the newest unretired key
/// signs, and every key ever created is kept so old signatures still verify. The private key column
/// is encrypted at rest (SecretProtection). Losing it only means new signatures come from a new key —
/// unlike a footage encryption key, nothing becomes unreadable.</summary>
public class SigningKeyService(ApplicationDbContext db) : ISigningKeyService
{
    public const string Algorithm = "ECDSA-P256-SHA256";
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public async Task<SignatureResult> SignAsync(string payload, CancellationToken ct = default)
    {
        var key = await GetOrCreateActiveAsync(ct);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key.PrivateKey), out _);
        var data = Encoding.UTF8.GetBytes(payload);
        var p1363 = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var der = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new SignatureResult(key.Id, key.Algorithm, key.PublicKeyPem, Convert.ToBase64String(p1363), Convert.ToBase64String(der));
    }

    public async Task<bool> VerifyAsync(Guid keyId, string payload, string signature, CancellationToken ct = default)
    {
        var pem = await db.SigningKeys.AsNoTracking().Where(k => k.Id == keyId).Select(k => k.PublicKeyPem).FirstOrDefaultAsync(ct);
        if (pem is null) return false;
        return Verify(pem, payload, signature);
    }

    /// <summary>Checks a P1363 signature against a PEM public key.</summary>
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

    public async Task<List<PublicSigningKey>> ListPublicKeysAsync(CancellationToken ct = default)
    {
        await GetOrCreateActiveAsync(ct);
        return await db.SigningKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt)
            .Select(k => new PublicSigningKey(k.Id, k.Algorithm, k.PublicKeyPem, k.CreatedAt, k.RetiredAt))
            .ToListAsync(ct);
    }

    public async Task<string> GetActivePublicKeyPemAsync(CancellationToken ct = default) => (await GetOrCreateActiveAsync(ct)).PublicKeyPem;

    private async Task<SigningKey> GetOrCreateActiveAsync(CancellationToken ct)
    {
        var active = await ActiveAsync(ct);
        if (active is not null) return active;

        await CreateGate.WaitAsync(ct);
        try
        {
            active = await ActiveAsync(ct);
            if (active is not null) return active;
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var key = new SigningKey
            {
                Id = Guid.NewGuid(),
                Algorithm = Algorithm,
                PublicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem(),
                PrivateKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                CreatedAt = DateTime.UtcNow,
            };
            db.SigningKeys.Add(key);
            await db.SaveChangesAsync(ct);
            return key;
        }
        finally
        {
            CreateGate.Release();
        }
    }

    private Task<SigningKey?> ActiveAsync(CancellationToken ct) =>
        db.SigningKeys.AsNoTracking().Where(k => k.RetiredAt == null).OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);
}

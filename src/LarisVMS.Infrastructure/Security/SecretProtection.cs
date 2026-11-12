using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LarisVMS.Infrastructure.Security;

/// <summary>
/// Application-wide encryption for sensitive column values stored in the database — camera
/// credentials (Username/Password), SMB storage-target credentials, and node media signing keys.
///
/// Backed by ASP.NET Data Protection, DPAPI-wrapped on Windows (see Program.cs). Values carry a
/// <see cref="Prefix"/> marker so decryption is only attempted on values we actually encrypted
/// (legacy plaintext rows are passed through and get encrypted on their next save), and so
/// re-protecting an already-protected value is a no-op.
///
/// <see cref="Configure"/> must be called once at startup before any database access. At design time
/// (migrations) it is never configured, so the converter passes values through unchanged — encryption
/// affects data only, never schema.
///
/// Unlike rsolva's version, <see cref="Unprotect"/> throws on a decryption failure rather than
/// returning the raw ciphertext — for camera/SMB credentials, silently handing back an unusable
/// "enc:v1:..." string as a password is worse than a loud error at the call site (frcastr's
/// ProtectedStringConverter argues the same point for its domain).
/// </summary>
public static class SecretProtection
{
    private const string Prefix = "enc:v1:";
    private static IDataProtector? _protector;

    // "Rcordr.SensitiveData.v1", deliberately never renamed (not to NidusVMS, and not to LarisVMS
    // either): this purpose string is a cryptographic discriminator baked into every already-encrypted
    // Camera/SMB credential and node media signing key in the live database, not a display name —
    // changing it makes every one of those permanently undecryptable (see Program.cs's
    // SetApplicationName for the matching issue and full rationale; both were swept by the project's
    // earlier blind Rcordr->NidusVMS rename and had to be reverted here specifically). Applies equally
    // to the current NidusVMS->LarisVMS rename. No user ever sees this string.
    public static void Configure(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("Rcordr.SensitiveData.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (_protector is null) return plaintext;                 // not configured (design time)
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already encrypted
        return Prefix + _protector.Protect(plaintext);
    }

    /// <summary>Like <see cref="Unprotect"/>, but a value that isn't encrypted is refused rather than
    /// passed through as legacy plaintext: for columns that have only ever been written encrypted (keys
    /// made after encryption existed), where a plain value can only have been put there directly in the
    /// database to plant a key of someone else's choosing.</summary>
    public static string? UnprotectStrict(string? stored) => UnprotectStrict(stored, _protector);

    internal static string? UnprotectStrict(string? stored, IDataProtector? protector)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (protector is null) return stored;                  // not configured (design time, tests)
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            throw new System.Security.Cryptography.CryptographicException(
                "A key stored in the database isn't encrypted, so it was changed outside LarisVMS; it isn't used.");
        return protector.Unprotect(stored[Prefix.Length..]);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;       // legacy plaintext
        if (_protector is null) return stored;
        return _protector.Unprotect(stored[Prefix.Length..]);
    }
}

/// <summary>
/// EF Core value converter that encrypts a string column on the way to the database and decrypts it
/// on the way back. Applied to sensitive columns in ApplicationDbContext.OnModelCreating.
/// </summary>
public sealed class EncryptedStringConverter() : ValueConverter<string, string>(
    v => SecretProtection.Protect(v)!,
    v => SecretProtection.Unprotect(v)!);

/// <summary><see cref="EncryptedStringConverter"/> for columns that have only ever held encrypted
/// values (keys): a plain value read back is refused (<see cref="SecretProtection.UnprotectStrict"/>).</summary>
public sealed class StrictEncryptedStringConverter() : ValueConverter<string, string>(
    v => SecretProtection.Protect(v)!,
    v => SecretProtection.UnprotectStrict(v)!);

/// <summary>Nullable counterpart of <see cref="EncryptedStringConverter"/> for optional columns.</summary>
public sealed class EncryptedNullableStringConverter() : ValueConverter<string?, string?>(
    v => SecretProtection.Protect(v),
    v => SecretProtection.Unprotect(v));

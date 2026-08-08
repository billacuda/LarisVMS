using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Rcordr.Infrastructure.Security;

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

    public static void Configure(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("Rcordr.SensitiveData.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        if (_protector is null) return plaintext;                 // not configured (design time)
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext; // already encrypted
        return Prefix + _protector.Protect(plaintext);
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

/// <summary>Nullable counterpart of <see cref="EncryptedStringConverter"/> for optional columns.</summary>
public sealed class EncryptedNullableStringConverter() : ValueConverter<string?, string?>(
    v => SecretProtection.Protect(v),
    v => SecretProtection.Unprotect(v));

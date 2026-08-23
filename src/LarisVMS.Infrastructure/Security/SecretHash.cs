using System.Security.Cryptography;
using System.Text;

namespace LarisVMS.Infrastructure.Security;

/// <summary>
/// One-way hashing for a generated, high-entropy secret (a node's own registration secret, an API
/// key) — plain SHA-256, not PasswordHasher/bcrypt/PBKDF2, which exist for user-chosen passwords that
/// need slow, salted hashing to resist guessing. A 32-byte random value has no guessable structure for
/// a slow hash to defend against, so a fast digest plus fixed-time comparison is the right tool.
/// Extracted from NodeService (M3), which hashed Node.ApiKeyHash the same way before this existed.
/// </summary>
public static class SecretHash
{
    public static string Hash(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool Matches(string secret, string hash)
        => FixedTimeEquals(Hash(secret), hash);

    /// <summary>Plain constant-time string comparison — for a value that's already plaintext on both
    /// sides (a registration key read back from Settings), not a hash comparison.</summary>
    public static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

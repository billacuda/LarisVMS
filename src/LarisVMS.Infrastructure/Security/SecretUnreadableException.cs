using System.Security.Cryptography;

namespace LarisVMS.Infrastructure.Security;

/// <summary>
/// An encrypted value in the database couldn't be decrypted, nearly always because the data-protection
/// key it was encrypted with isn't in this server's <c>keys</c> folder: a database restored on, or
/// connected to from, another machine without that folder.
/// </summary>
public sealed class SecretUnreadableException(Exception inner) : CryptographicException(
    $"A password or key in the database can't be read, because the data-protection key it was encrypted with isn't in {KeysFolder}. " +
    $"Copy the old server's keys folder there and restart LarisVMS. ({inner.Message})", inner)
{
    /// <summary>The data-protection key ring (Program.cs).</summary>
    public static string KeysFolder { get; } = Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "keys");

    /// <summary>Whether <paramref name="ex"/> is one, or was caused by one (EF Core wraps what a value
    /// converter throws in some queries).</summary>
    public static bool IsIn(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SecretUnreadableException) return true;
        return false;
    }
}

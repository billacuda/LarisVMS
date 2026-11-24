using System.Security.Cryptography;
using System.Text;

namespace LarisVMS.Core.Security;

/// <summary>
/// Encrypts small secrets so that they can only be read back on this machine: a node's or proxy's
/// registration (node.config, proxy.config) and the Web server's database password.
///
/// On Windows this is DPAPI with machine scope, exactly as before (existing files read unchanged).
/// Elsewhere it is AES-256-GCM with a key derived (HKDF-SHA256) from <c>/etc/machine-id</c> and the
/// caller's <paramref name="entropy"/>. machine-id is readable by every local user, so on Linux the
/// real protection is the file's owner-only mode (see <see cref="WriteFile"/>); the encryption makes a
/// copy taken off the machine — in a backup, say — useless elsewhere, which is what DPAPI's machine
/// scope gives on Windows.
/// </summary>
public static class MachineSecret
{
    // "LVMS" + format version 1.
    private static readonly byte[] Magic = "LVMS\u0001"u8.ToArray();
    private static readonly byte[] Salt = "LarisVMS.MachineSecret.v1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Where the machine ID is read from, first match wins. The data folder's own
    /// <c>machine-id</c> comes first: only the Docker images' entrypoint writes it, because a
    /// recreated container gets a new <c>/etc/machine-id</c> and would lose every saved secret. On a
    /// normal install the file doesn't exist, so the system's machine ID is used as before.</summary>
    internal static string[] MachineIdPaths =>
        [Path.Combine(LarisPaths.DataRoot, "machine-id"), "/etc/machine-id", "/var/lib/dbus/machine-id"];

    public static byte[] Protect(byte[] plaintext, byte[]? entropy)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plaintext, entropy, DataProtectionScope.LocalMachine)
            : ProtectWithMachineId(plaintext, entropy, ReadMachineId());
    }

    /// <summary>The non-Windows format, with the machine ID given (tests run it on any OS).</summary>
    internal static byte[] ProtectWithMachineId(byte[] plaintext, byte[]? entropy, byte[] machineId)
    {
        var key = DeriveKey(entropy, machineId);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[Magic.Length + NonceSize + TagSize + plaintext.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext,
            output.AsSpan(Magic.Length + NonceSize + TagSize),
            output.AsSpan(Magic.Length + NonceSize, TagSize),
            Magic);
        return output;
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the data was protected on another
    /// machine, with other entropy, or has been altered.</summary>
    public static byte[] Unprotect(byte[] data, byte[]? entropy)
    {
        ArgumentNullException.ThrowIfNull(data);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(data, entropy, DataProtectionScope.LocalMachine)
            : UnprotectWithMachineId(data, entropy, ReadMachineId());
    }

    internal static byte[] UnprotectWithMachineId(byte[] data, byte[]? entropy, byte[] machineId)
    {
        if (data.Length < Magic.Length + NonceSize + TagSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Not a LarisVMS machine secret, or written by a newer version.");
        var key = DeriveKey(entropy, machineId);
        var plaintext = new byte[data.Length - Magic.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            data.AsSpan(Magic.Length, NonceSize),
            data.AsSpan(Magic.Length + NonceSize + TagSize),
            data.AsSpan(Magic.Length + NonceSize, TagSize),
            plaintext,
            Magic);
        return plaintext;
    }

    public static string ProtectString(string plaintext, byte[]? entropy) =>
        Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(plaintext), entropy));

    public static string UnprotectString(string protectedBase64, byte[]? entropy) =>
        Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedBase64), entropy));

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>, readable and writable by
    /// the owner only off Windows (the mode is set before any content is written, through a temporary
    /// file that replaces the old one). On Windows the data folder's ACL already covers it.</summary>
    public static void WriteFile(string path, byte[] bytes)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, bytes);
            return;
        }

        var temp = path + ".tmp";
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write(bytes);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] DeriveKey(byte[]? entropy, byte[] machineId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, machineId, 32, Salt, entropy ?? []);

    private static byte[] ReadMachineId()
    {
        foreach (var path in MachineIdPaths)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id)
                    return Encoding.ASCII.GetBytes(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next one
            }
        }
        throw new CryptographicException(
            "This machine has no machine ID (/etc/machine-id), so LarisVMS can't protect its secrets. " +
            "Run 'systemd-machine-id-setup' as root, then start the service again.");
    }
}

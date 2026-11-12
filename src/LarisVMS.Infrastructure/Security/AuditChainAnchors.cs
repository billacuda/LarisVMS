using System.Security.Cryptography;
using System.Text.Json;
using LarisVMS.Core.Security;

namespace LarisVMS.Infrastructure.Security;

/// <summary>Where the audit chain's <see cref="AuditChainAnchor"/> is kept, outside the database.</summary>
public interface IAuditChainAnchors
{
    /// <summary>Null when there's none yet. Throws <see cref="CryptographicException"/> when the
    /// record exists but can't be read (changed, or the server's keys are gone).</summary>
    AuditChainAnchor? Read();

    /// <summary>Writes it only when there's none yet: an anchor is never replaced.</summary>
    void WriteIfMissing(AuditChainAnchor anchor);
}

/// <summary>
/// The anchor in a file in the server's protected data folder (%ProgramData%\LarisVMS), encrypted
/// with the server's data-protection keys, so someone who can only reach the database can neither
/// read nor change it. After deliberately starting over with a new, empty database, delete the file
/// (the audit log integrity check says so) and a new one is made with the next entry.
/// </summary>
public sealed class AuditChainAnchorFile(string path) : IAuditChainAnchors
{
    public static string DefaultPath => Path.Combine(LocalDataAcl.Root, "audit-chain.dat");

    private readonly Lock _lock = new();

    public AuditChainAnchor? Read()
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return null;
            var text = SecretProtection.UnprotectStrict(File.ReadAllText(path).Trim())
                       ?? throw new CryptographicException("The audit chain record is empty.");
            try
            {
                return JsonSerializer.Deserialize<AuditChainAnchor>(text)
                       ?? throw new CryptographicException("The audit chain record is empty.");
            }
            catch (JsonException ex)
            {
                throw new CryptographicException("The audit chain record can't be read.", ex);
            }
        }
    }

    public void WriteIfMissing(AuditChainAnchor anchor)
    {
        lock (_lock)
        {
            if (File.Exists(path)) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, SecretProtection.Protect(JsonSerializer.Serialize(anchor)));
            LocalDataAcl.ProtectFile(path);
        }
    }
}

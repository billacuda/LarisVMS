namespace LarisVMS.Core.Entities;

/// <summary>
/// The single row (<see cref="SingletonId"/>) that anchors the audit log's hash chain: the key
/// entries are sealed with, the newest entry's hash, and the hash of the last entry retention
/// removed. <see cref="Seal"/> covers both hashes with the same key, so removing the newest or
/// oldest entries directly in the database shows up as well as changing one in the middle.
/// </summary>
public class AuditChainState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Base64 HMAC key. Encrypted at rest with the server's data-protection keys, which
    /// live on the server and not in the database.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Hash of the newest entry ("" before the first).</summary>
    public string LastHash { get; set; } = string.Empty;

    /// <summary>Hash of the newest entry retention removed, or null if it never removed a sealed one.</summary>
    public string? PrunedThroughHash { get; set; }

    /// <summary>HMAC over <see cref="LastHash"/> and <see cref="PrunedThroughHash"/>.</summary>
    public string Seal { get; set; } = string.Empty;

    /// <summary>Bumped to take the row lock before appending, so two writers can't both chain onto
    /// the same entry.</summary>
    public long LockVersion { get; set; }

    /// <summary>Id of the newest entry sent to the syslog server, or null when forwarding is off.</summary>
    public long? ForwardedThroughId { get; set; }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Footage encryption keys. One master key (random 32 bytes, stored encrypted by Data Protection) wraps
/// a data key per camera per UTC day with AES-256-GCM. The master key's backup is a JSON file sealed
/// with a passphrase (PBKDF2-SHA256 → AES-256-GCM); restoring it after a server rebuild brings every
/// data key back, since those live in the database wrapped by it.
/// </summary>
public class FootageKeyService(ApplicationDbContext db) : IFootageKeyService
{
    public const string BackupFormat = "LarisVMS footage master key backup v1";
    public const int BackupIterations = 600_000;
    public const int MinPassphraseLength = 12;
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    private sealed record BackupFile(string format, Guid keyId, DateTime createdUtc, string kdf, int iterations,
        string salt, string nonce, string ciphertext, string tag);

    public async Task<FootageKeyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var key = await db.FootageMasterKeys.AsNoTracking().OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        return key is null ? new FootageKeyStatus(false, null, null, null) : new FootageKeyStatus(true, key.Id, key.CreatedAt, key.BackupDownloadedAt);
    }

    public async Task<FootageKeyStatus> CreateMasterKeyAsync(CancellationToken ct = default)
    {
        await CreateGate.WaitAsync(ct);
        try
        {
            if (!await db.FootageMasterKeys.AnyAsync(ct))
            {
                db.FootageMasterKeys.Add(new FootageMasterKey
                {
                    Id = Guid.NewGuid(),
                    Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            CreateGate.Release();
        }
        return await GetStatusAsync(ct);
    }

    public async Task<string?> ExportBackupAsync(string passphrase, CancellationToken ct = default)
    {
        if (passphrase.Length < MinPassphraseLength) throw new ArgumentException($"Use a passphrase of at least {MinPassphraseLength} characters.");
        var master = await db.FootageMasterKeys.OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        if (master is null) return null;

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, BackupIterations, HashAlgorithmName.SHA256, 32);
        var plain = Convert.FromBase64String(master.Key);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(wrappingKey, 16)) gcm.Encrypt(nonce, plain, cipher, tag, master.Id.ToByteArray());

        master.BackupDownloadedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return JsonSerializer.Serialize(new BackupFile(BackupFormat, master.Id, DateTime.SpecifyKind(master.CreatedAt, DateTimeKind.Utc),
            "PBKDF2-SHA256", BackupIterations, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
            Convert.ToBase64String(cipher), Convert.ToBase64String(tag)), new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<FootageKeyRestoreResult> RestoreBackupAsync(string backupJson, string passphrase, CancellationToken ct = default)
    {
        BackupFile? backup;
        try { backup = JsonSerializer.Deserialize<BackupFile>(backupJson); }
        catch (JsonException) { return FootageKeyRestoreResult.NotABackup; }
        if (backup is null || backup.format != BackupFormat || backup.iterations is < 100_000 or > 10_000_000)
            return FootageKeyRestoreResult.NotABackup;

        byte[] key;
        try
        {
            var wrappingKey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), Convert.FromBase64String(backup.salt),
                backup.iterations, HashAlgorithmName.SHA256, 32);
            var cipher = Convert.FromBase64String(backup.ciphertext);
            key = new byte[cipher.Length];
            using var gcm = new AesGcm(wrappingKey, 16);
            gcm.Decrypt(Convert.FromBase64String(backup.nonce), cipher, Convert.FromBase64String(backup.tag), key, backup.keyId.ToByteArray());
        }
        catch (FormatException) { return FootageKeyRestoreResult.NotABackup; }
        catch (CryptographicException) { return FootageKeyRestoreResult.WrongPassphrase; }

        var existing = await db.FootageMasterKeys.OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (existing.Id != backup.keyId) return FootageKeyRestoreResult.DifferentKeyInstalled;
            existing.BackupDownloadedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return FootageKeyRestoreResult.AlreadyInstalled;
        }

        db.FootageMasterKeys.Add(new FootageMasterKey
        {
            Id = backup.keyId,
            Key = Convert.ToBase64String(key),
            CreatedAt = backup.createdUtc,
            // Restored from a backup, so one exists by definition.
            BackupDownloadedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return FootageKeyRestoreResult.Restored;
    }

    public async Task<(Guid KeyId, byte[] Key)?> GetOrCreateDataKeyAsync(Guid cameraId, DateOnly day, CancellationToken ct = default)
    {
        var master = await db.FootageMasterKeys.AsNoTracking().OrderBy(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        if (master is null || master.BackupDownloadedAt is null) return null;

        var existing = await db.FootageKeys.AsNoTracking().FirstOrDefaultAsync(k => k.CameraId == cameraId && k.Day == day, ct);
        if (existing is not null) return (existing.Id, Unwrap(existing, master));

        var key = RandomNumberGenerator.GetBytes(32);
        var row = new FootageKey { Id = Guid.NewGuid(), CameraId = cameraId, Day = day, MasterKeyId = master.Id };
        row.Wrapped = Wrap(key, row, master);
        db.FootageKeys.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return (row.Id, key);
        }
        catch (DbUpdateException)
        {
            // Another request created this camera-day's key first (unique index): use that one.
            db.Entry(row).State = EntityState.Detached;
            var winner = await db.FootageKeys.AsNoTracking().FirstAsync(k => k.CameraId == cameraId && k.Day == day, ct);
            return (winner.Id, Unwrap(winner, master));
        }
    }

    public async Task<(Guid CameraId, byte[] Key)?> GetDataKeyAsync(Guid keyId, CancellationToken ct = default)
    {
        var row = await db.FootageKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (row is null) return null;
        var master = await db.FootageMasterKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == row.MasterKeyId, ct);
        return master is null ? null : (row.CameraId, Unwrap(row, master));
    }

    private static byte[] Aad(FootageKey row) => [.. row.Id.ToByteArray(), .. row.CameraId.ToByteArray()];

    private static string Wrap(byte[] key, FootageKey row, FootageMasterKey master)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(Convert.FromBase64String(master.Key), 16);
        gcm.Encrypt(nonce, key, cipher, tag, Aad(row));
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    private static byte[] Unwrap(FootageKey row, FootageMasterKey master)
    {
        var all = Convert.FromBase64String(row.Wrapped);
        var key = new byte[all.Length - 28];
        using var gcm = new AesGcm(Convert.FromBase64String(master.Key), 16);
        gcm.Decrypt(all.AsSpan(0, 12), all.AsSpan(12, key.Length), all.AsSpan(12 + key.Length, 16), key, Aad(row));
        return key;
    }
}

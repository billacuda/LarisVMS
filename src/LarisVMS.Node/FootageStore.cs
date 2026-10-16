using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>One footage file to encrypt in place.</summary>
public sealed record FootageEncryptionJob(Guid CameraId, string Path, DateTime RecordedUtc);

/// <summary>Thrown when an encrypted file's data key can't be had right now (server unreachable, or
/// the key is unknown to it). Readers treat it like a missing file.</summary>
public sealed class FootageKeyUnavailableException(string message) : IOException(message);

/// <summary>
/// The one way footage (recordings, talk audio, cached images) is read and written on this node, so
/// encryption at rest (FootageCrypto) works everywhere without each reader knowing about it. Reads
/// detect an encrypted file by its header and decrypt on the fly; plain files pass straight through,
/// so turning encryption on or off never strands anything.
///
/// Data keys come from the server (per camera, per UTC day) and are held in memory only. ffmpeg can't
/// read an encrypted file, so <see cref="FfmpegInputAsync"/> hands it a loopback URL instead
/// (/footage-plain/{token}, served with range support so seeking still works).
/// </summary>
public sealed class FootageStore(NodeApiClient api, int livePort, ILogger<FootageStore> logger)
{
    private static readonly TimeSpan LoopbackLifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<Guid, byte[]> _keys = new();
    private readonly ConcurrentDictionary<(Guid CameraId, DateOnly Day), Guid> _dayKeys = new();
    private readonly ConcurrentDictionary<string, (string Path, DateTime ExpiresUtc)> _loopback = new();
    private readonly Channel<FootageEncryptionJob> _queue = Channel.CreateUnbounded<FootageEncryptionJob>();

    public ChannelReader<FootageEncryptionJob> EncryptionQueue => _queue.Reader;

    /// <summary>Queues a finished file for encryption (FootageEncryptionWorker decides whether the
    /// camera wants it).</summary>
    public void EnqueueEncryption(Guid cameraId, string path, DateTime recordedUtc) =>
        _queue.Writer.TryWrite(new FootageEncryptionJob(cameraId, path, recordedUtc));

    // ── Keys ────────────────────────────────────────────────────────────────

    public async Task<byte[]> KeyAsync(Guid keyId, CancellationToken ct)
    {
        if (_keys.TryGetValue(keyId, out var cached)) return cached;
        FootageKeyResponse? response;
        try { response = await api.GetFootageKeyAsync(keyId, ct); }
        catch (HttpRequestException ex) { throw new FootageKeyUnavailableException($"Couldn't fetch footage key {keyId} from the server: {ex.Message}"); }
        if (response is null) throw new FootageKeyUnavailableException($"The server doesn't know footage key {keyId}.");
        var key = Convert.FromBase64String(response.Key);
        _keys[keyId] = key;
        return key;
    }

    /// <summary>The camera's data key for the UTC day of <paramref name="atUtc"/>, or null when the
    /// server says encryption isn't set up. Throws when the server can't be reached.</summary>
    public async Task<(Guid KeyId, byte[] Key)?> DayKeyAsync(Guid cameraId, DateTime atUtc, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc));
        if (_dayKeys.TryGetValue((cameraId, day), out var known) && _keys.TryGetValue(known, out var key)) return (known, key);
        var response = await api.GetFootageKeyForDayAsync(cameraId, day, ct);
        if (response is null) return null;
        var bytes = Convert.FromBase64String(response.Key);
        _keys[response.KeyId] = bytes;
        _dayKeys[(cameraId, day)] = response.KeyId;
        return (response.KeyId, bytes);
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>The file's original bytes, decrypted when needed. Seekable; Length is the plaintext
    /// length.</summary>
    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        try
        {
            if (!FootageCrypto.IsEncrypted(fs)) return fs;
            var header = FootageCrypto.ReadHeader(fs);
            var key = await KeyAsync(header.KeyId, ct);
            return new FootageDecryptStream(fs, key);
        }
        catch
        {
            await fs.DisposeAsync();
            throw;
        }
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        await using var stream = await OpenReadAsync(path, ct);
        var buffer = new byte[stream.Length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    /// <summary>SHA-256 of the file's original (plaintext) bytes, lowercase hex — the tamper-evidence
    /// fingerprint is of the recording, not of how it happens to be stored.</summary>
    public async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = await OpenReadAsync(path, ct);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>What to hand ffmpeg as an input: the path itself for a plain file, or a loopback URL
    /// that serves the decrypted bytes (with range support) for an encrypted one. The key is fetched
    /// up front so ffmpeg never meets a key failure mid-read.</summary>
    public async Task<string> FfmpegInputAsync(string path, CancellationToken ct)
    {
        var keyId = FootageCrypto.ReadKeyId(path);
        if (keyId is null) return path;
        await KeyAsync(keyId.Value, ct);

        var now = DateTime.UtcNow;
        foreach (var (stale, entry) in _loopback)
        {
            if (entry.ExpiresUtc < now) _loopback.TryRemove(stale, out _);
        }
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _loopback[token] = (path, now + LoopbackLifetime);
        return $"http://127.0.0.1:{livePort}/footage-plain/{token}";
    }

    public static bool IsLoopbackInput(string input) => input.StartsWith("http://127.0.0.1:", StringComparison.Ordinal);

    /// <summary>The file a /footage-plain token stands for, while it's valid.</summary>
    public string? ResolveLoopback(string token) =>
        _loopback.TryGetValue(token, out var entry) && entry.ExpiresUtc >= DateTime.UtcNow ? entry.Path : null;

    /// <summary>Sends a cached image (thumbnail, snapshot), decrypting it when needed.</summary>
    public async Task SendFileAsync(HttpContext ctx, string path, CancellationToken ct)
    {
        if (!FootageCrypto.IsEncrypted(path))
        {
            await ctx.Response.SendFileAsync(path, ct);
            return;
        }
        var bytes = await ReadAllBytesAsync(path, ct);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct);
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    /// <summary>Writes a cached image atomically (via ThumbnailCapture.SaveToCacheAsync), sealed with
    /// the camera's key for today when <paramref name="encryptForCamera"/> is set. If the key can't be
    /// had, the image is written plain rather than lost — the encryption sweep picks it up later.</summary>
    public async Task WriteImageAsync(string path, byte[] bytes, Guid? encryptForCamera, CancellationToken ct)
    {
        var toWrite = bytes;
        if (encryptForCamera is { } cameraId)
        {
            try
            {
                if (await DayKeyAsync(cameraId, DateTime.UtcNow, ct) is { } key) toWrite = FootageCrypto.EncryptBytes(bytes, key.KeyId, key.Key);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogWarning(ex, "Couldn't get today's footage key for camera {CameraId}; caching {Path} unencrypted for now.", cameraId, path);
            }
        }
        await LarisVMS.Media.ThumbnailCapture.SaveToCacheAsync(path, toWrite, ct);
    }

    /// <summary>Encrypts a finished file in place: sealed into a temporary file next to it, then
    /// swapped in, keeping the original's timestamps (retention reads them). True when the file is
    /// encrypted afterwards; false to try again later (no key yet, or the file is busy).</summary>
    public async Task<bool> EncryptFileAsync(string path, Guid cameraId, DateTime recordedUtc, CancellationToken ct)
    {
        if (!File.Exists(path)) return true; // deleted by retention meanwhile: nothing to do
        if (FootageCrypto.IsEncrypted(path)) return true;

        (Guid KeyId, byte[] Key)? key;
        try { key = await DayKeyAsync(cameraId, recordedUtc, ct); }
        catch (HttpRequestException) { return false; }
        if (key is null) return false;

        var tmp = path + ".enc-tmp";
        try
        {
            var info = new FileInfo(path);
            var (created, modified) = (info.CreationTimeUtc, info.LastWriteTimeUtc);
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await FootageCrypto.EncryptAsync(input, output, key.Value.KeyId, key.Value.Key, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            File.SetCreationTimeUtc(tmp, created);
            File.SetLastWriteTimeUtc(tmp, modified);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Busy (being played back or exported) or gone: leave the original alone and retry later.
            logger.LogDebug(ex, "Couldn't encrypt {Path} yet; will retry.", path);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* best effort */ }
            return false;
        }
    }
}

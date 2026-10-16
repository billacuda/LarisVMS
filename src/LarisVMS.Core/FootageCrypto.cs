using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LarisVMS.Core;

/// <summary>
/// Footage encryption at rest. An encrypted file keeps its name; it starts with a 64-byte header and
/// the rest is the original file in 64 KB chunks, each sealed with AES-256-GCM:
///
///   header  "LVENC1\0\0" (8) | version (1) | reserved (3) | chunk size (u32 LE) | key id (16, Guid)
///           | plaintext length (i64 LE) | salt (8) | reserved (16)
///   chunk i ciphertext (up to chunk size) ‖ GCM tag (16)
///
/// Each chunk's nonce is salt ‖ i (u32 big-endian) and its associated data is the header ‖ i (u32 LE),
/// so a chunk can't be moved, swapped between files, or the header altered without the tag failing.
/// Chunks make the file seekable: playback range requests and ffmpeg seeks decrypt only the chunks
/// they touch (<see cref="FootageDecryptStream"/>).
///
/// The key id names a per-camera, per-day data key held (wrapped by the master key) on the server.
/// </summary>
public static class FootageCrypto
{
    public const int HeaderSize = 64;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 64 * 1024;
    public const int KeySize = 32;
    private static readonly byte[] Magic = "LVENC1\0\0"u8.ToArray();
    private const byte Version = 1;

    public sealed record Header(Guid KeyId, int ChunkSize, long PlaintextLength, byte[] Salt, byte[] Raw);

    /// <summary>True when the file starts with the encrypted-footage magic.</summary>
    public static bool IsEncrypted(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16);
            return IsEncrypted(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsEncrypted(Stream stream)
    {
        Span<byte> head = stackalloc byte[8];
        var position = stream.Position;
        var read = stream.ReadAtLeast(head, 8, throwOnEndOfStream: false);
        stream.Position = position;
        return read == 8 && head.SequenceEqual(Magic);
    }

    public static Header ReadHeader(Stream stream)
    {
        var raw = new byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(raw);
        if (!raw.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("Not an encrypted footage file.");
        if (raw[8] != Version) throw new InvalidDataException($"Unsupported footage encryption version {raw[8]}.");
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12, 4));
        var keyId = new Guid(raw.AsSpan(16, 16));
        var length = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(32, 8));
        if (chunkSize <= 0 || chunkSize > 16 * 1024 * 1024 || length < 0) throw new InvalidDataException("Corrupt footage header.");
        return new Header(keyId, chunkSize, length, raw.AsSpan(40, 8).ToArray(), raw);
    }

    /// <summary>The key id an encrypted file was sealed with, or null for a plain file.</summary>
    public static Guid? ReadKeyId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, HeaderSize);
            return IsEncrypted(stream) ? ReadHeader(stream).KeyId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    public static long EncryptedLength(long plaintextLength, int chunkSize = DefaultChunkSize) =>
        HeaderSize + plaintextLength + ChunkCount(plaintextLength, chunkSize) * TagSize;

    internal static long ChunkCount(long plaintextLength, int chunkSize) => (plaintextLength + chunkSize - 1) / chunkSize;

    /// <summary>Encrypts <paramref name="input"/> (read to its end; its length must be known) into
    /// <paramref name="output"/>.</summary>
    public static async Task EncryptAsync(Stream input, Stream output, Guid keyId, byte[] key, CancellationToken ct = default,
        int chunkSize = DefaultChunkSize)
    {
        if (key.Length != KeySize) throw new ArgumentException("Footage keys are 32 bytes.", nameof(key));
        var length = input.Length - input.Position;
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[8] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), chunkSize);
        keyId.TryWriteBytes(header.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32, 8), length);
        RandomNumberGenerator.Fill(header.AsSpan(40, 8));
        await output.WriteAsync(header, ct);

        using var gcm = new AesGcm(key, TagSize);
        var plain = new byte[chunkSize];
        var cipher = new byte[chunkSize + TagSize];
        var nonce = new byte[12];
        header.AsSpan(40, 8).CopyTo(nonce);
        var aad = new byte[HeaderSize + 4];
        header.CopyTo(aad, 0);
        long remaining = length;
        for (uint index = 0; remaining > 0; index++)
        {
            var n = (int)Math.Min(chunkSize, remaining);
            await input.ReadExactlyAsync(plain.AsMemory(0, n), ct);
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
            BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(HeaderSize), index);
            gcm.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), cipher.AsSpan(n, TagSize), aad);
            await output.WriteAsync(cipher.AsMemory(0, n + TagSize), ct);
            remaining -= n;
        }
    }

    /// <summary>Encrypts a small buffer (a cached thumbnail) in one call.</summary>
    public static byte[] EncryptBytes(byte[] plaintext, Guid keyId, byte[] key)
    {
        using var input = new MemoryStream(plaintext, writable: false);
        using var output = new MemoryStream();
        EncryptAsync(input, output, keyId, key).GetAwaiter().GetResult();
        return output.ToArray();
    }
}

/// <summary>
/// A read-only, seekable view of an encrypted footage file's plaintext. Decrypts one chunk at a time
/// and keeps the last one, so sequential reads and short seeks decrypt each chunk once. A chunk whose
/// tag doesn't verify (tampered, truncated, or the wrong key) throws <see cref="CryptographicException"/>.
/// </summary>
public sealed class FootageDecryptStream : Stream
{
    private readonly Stream _inner;
    private readonly FootageCrypto.Header _header;
    private readonly AesGcm _gcm;
    private readonly byte[] _cipher;
    private readonly byte[] _plain;
    private readonly byte[] _nonce = new byte[12];
    private readonly byte[] _aad = new byte[FootageCrypto.HeaderSize + 4];
    private long _cachedChunk = -1;
    private int _cachedLength;
    private long _position;

    public FootageDecryptStream(Stream inner, byte[] key)
    {
        _inner = inner;
        _header = FootageCrypto.ReadHeader(inner);
        _gcm = new AesGcm(key, FootageCrypto.TagSize);
        _cipher = new byte[_header.ChunkSize + FootageCrypto.TagSize];
        _plain = new byte[_header.ChunkSize];
        _header.Salt.CopyTo(_nonce, 0);
        _header.Raw.CopyTo(_aad, 0);
    }

    public Guid KeyId => _header.KeyId;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _header.PlaintextLength;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.Length == 0) return 0;
        var chunk = _position / _header.ChunkSize;
        LoadChunk(chunk);
        var within = (int)(_position - chunk * _header.ChunkSize);
        var n = Math.Min(buffer.Length, _cachedLength - within);
        _plain.AsSpan(within, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    private void LoadChunk(long chunk)
    {
        if (chunk == _cachedChunk) return;
        var length = (int)Math.Min(_header.ChunkSize, Length - chunk * _header.ChunkSize);
        _inner.Position = FootageCrypto.HeaderSize + chunk * (_header.ChunkSize + FootageCrypto.TagSize);
        _inner.ReadExactly(_cipher.AsSpan(0, length + FootageCrypto.TagSize));
        BinaryPrimitives.WriteUInt32BigEndian(_nonce.AsSpan(8), (uint)chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(_aad.AsSpan(FootageCrypto.HeaderSize), (uint)chunk);
        _gcm.Decrypt(_nonce, _cipher.AsSpan(0, length), _cipher.AsSpan(length, FootageCrypto.TagSize), _plain.AsSpan(0, length), _aad);
        _cachedChunk = chunk;
        _cachedLength = length;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gcm.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

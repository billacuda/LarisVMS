using System.Security.Cryptography;

namespace LarisVMS.Core;

/// <summary>
/// Tamper evidence for recorded footage. Every segment file is hashed (SHA-256) on the node as it's
/// recorded, and the server links each camera's segments into a hash chain: a segment's chain hash
/// covers its own content hash, its time range, its camera and the previous segment's chain hash, so
/// changing, removing or reordering any recorded segment breaks every link after it. Signed
/// checkpoints of each camera's latest link (ChainCheckpoint) pin the chain down over time.
///
/// The formula lives here, and once more in the Verify export page's JavaScript (exports-verify.js),
/// which recomputes the links in an exported manifest — keep the two identical.
/// </summary>
public static class SegmentChain
{
    /// <summary>The chain hash, as 64 lowercase hex characters:
    /// SHA-256( prev chain hash (32 bytes; all zeros for a camera's first link)
    ///        ‖ content hash (32 bytes)
    ///        ‖ camera id (16 bytes, Guid.ToByteArray order)
    ///        ‖ start UTC ticks (8 bytes, little-endian)
    ///        ‖ end UTC ticks (8 bytes, little-endian) ).</summary>
    public static string Link(string? prevChainHashHex, string contentSha256Hex, Guid cameraId, DateTime startUtc, DateTime endUtc)
    {
        var buffer = new byte[32 + 32 + 16 + 8 + 8];
        if (prevChainHashHex is not null) Convert.FromHexString(prevChainHashHex).CopyTo(buffer, 0);
        Convert.FromHexString(contentSha256Hex).CopyTo(buffer, 32);
        cameraId.ToByteArray().CopyTo(buffer, 64);
        BitConverter.TryWriteBytes(buffer.AsSpan(80, 8), DateTime.SpecifyKind(startUtc, DateTimeKind.Utc).Ticks);
        BitConverter.TryWriteBytes(buffer.AsSpan(88, 8), DateTime.SpecifyKind(endUtc, DateTimeKind.Utc).Ticks);
        if (!BitConverter.IsLittleEndian)
        {
            buffer.AsSpan(80, 8).Reverse();
            buffer.AsSpan(88, 8).Reverse();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    /// <summary>A 64-character lowercase hex SHA-256, or false.</summary>
    public static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>The text a chain checkpoint's signature covers.</summary>
    public static string CheckpointPayload(Guid cameraId, long segmentId, string chainHash, DateTime segmentEndUtc, DateTime signedUtc) =>
        $"LarisVMS-chain-v1|{cameraId:D}|{segmentId}|{chainHash}|{DateTime.SpecifyKind(segmentEndUtc, DateTimeKind.Utc):o}|{DateTime.SpecifyKind(signedUtc, DateTimeKind.Utc):o}";

    /// <summary>SHA-256 of a file as lowercase hex, read sequentially in large blocks.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}

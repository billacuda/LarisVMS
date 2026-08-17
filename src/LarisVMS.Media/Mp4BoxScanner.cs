using System.Buffers;
using System.Buffers.Binary;

namespace LarisVMS.Media;

/// <summary>
/// Incrementally scans a fragmented-MP4 byte stream for top-level box boundaries: where the "init
/// segment" (ftyp + moov — everything an MSE SourceBuffer needs before it can accept any fragment)
/// ends, and where each moof+mdat fragment ends. A late-joining live viewer needs exactly those
/// leading bytes handed to it before any fragment, and then whole fragments only.
///
/// Box types are compared as big-endian uint32 rather than decoded to strings. A four-character
/// string per box sounds free, but this runs over every byte ffmpeg emits, for every camera, forever
/// — it was measurably the only allocation left on the no-viewer path.
///
/// Both shapes are offered: a span for callers holding one contiguous buffer, and a
/// <see cref="ReadOnlySequence{T}"/> for callers reading through a <c>PipeReader</c>, whose buffers
/// are pooled and may be split across segments. Neither ever copies the bytes it scans.
/// </summary>
public static class Mp4BoxScanner
{
    // 'moov', 'moof', 'mdat' as big-endian uint32.
    private const uint Moov = 0x6D6F6F76;
    private const uint Moof = 0x6D6F6F66;
    private const uint Mdat = 0x6D646174;

    /// <summary>Given all bytes received so far from the start of the stream, returns the number of
    /// leading bytes that make up the init segment (through the end of the top-level "moov" box), or
    /// null if not yet fully received. A stream that's still arriving looks identical to one that's
    /// stalled or malformed from here, so this never throws — it just waits for more bytes.</summary>
    public static int? TryFindInitSegmentEnd(ReadOnlySpan<byte> buffer)
        => ScanSpan(buffer, stopAt: Moov, requireMoofFirst: false);

    /// <summary>Given bytes starting exactly on a top-level box boundary, returns the length of the
    /// complete fMP4 fragment at the front (a moof box plus the mdat that follows it, including any
    /// styp that precedes them), or null if the whole thing hasn't arrived yet.
    ///
    /// Live viewers must be fed whole fragments, never arbitrary slices of the byte stream. A viewer
    /// joining mid-stream gets the cached init segment followed by whatever comes next, and if that
    /// starts partway through a moof or mdat the decoder rejects it outright —
    /// CHUNK_DEMUXER_ERROR_APPEND_FAILED, "Failed to prepare video sample for decode". The same
    /// applies to back-pressure: dropping a raw chunk punches a hole through the middle of a box,
    /// whereas dropping a whole fragment is just a gap, which MSE handles.</summary>
    public static int? TryFindFragmentEnd(ReadOnlySpan<byte> buffer)
        => ScanSpan(buffer, stopAt: Mdat, requireMoofFirst: true);

    /// <inheritdoc cref="TryFindInitSegmentEnd(ReadOnlySpan{byte})"/>
    public static long? TryFindInitSegmentEnd(in ReadOnlySequence<byte> buffer)
        => buffer.IsSingleSegment
            ? TryFindInitSegmentEnd(buffer.FirstSpan)
            : ScanSequence(buffer, stopAt: Moov, requireMoofFirst: false);

    /// <inheritdoc cref="TryFindFragmentEnd(ReadOnlySpan{byte})"/>
    public static long? TryFindFragmentEnd(in ReadOnlySequence<byte> buffer)
        => buffer.IsSingleSegment
            ? TryFindFragmentEnd(buffer.FirstSpan)
            : ScanSequence(buffer, stopAt: Mdat, requireMoofFirst: true);

    private static int? ScanSpan(ReadOnlySpan<byte> buffer, uint stopAt, bool requireMoofFirst)
    {
        var offset = 0;
        var seenMoof = false;

        while (offset + 8 <= buffer.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));
            var type = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 4, 4));

            long boxSize;
            int headerSize;
            if (size == 1)
            {
                // 64-bit "largesize" extension — the real size lives in the 8 bytes right after the
                // type. mdat genuinely can exceed 4 GB, so this is handled rather than rejected.
                if (offset + 16 > buffer.Length) return null;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(offset + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                return null; // "box extends to end of file" — meaningless mid-stream
            }
            else
            {
                boxSize = size;
                headerSize = 8;
            }

            if (boxSize < headerSize || offset + boxSize > buffer.Length) return null;

            offset += (int)boxSize;
            if (type == Moof) seenMoof = true;
            // Requiring the moof first means a stray leading box can't be mistaken for a complete
            // fragment on its own, and that a resync landing mid-stream doesn't emit garbage.
            if (type == stopAt && (!requireMoofFirst || seenMoof)) return offset;
        }
        return null;
    }

    private static long? ScanSequence(in ReadOnlySequence<byte> buffer, uint stopAt, bool requireMoofFirst)
    {
        var reader = new SequenceReader<byte>(buffer);
        var seenMoof = false;
        Span<byte> header = stackalloc byte[16];

        while (reader.Remaining >= 8)
        {
            // Copied into a stack buffer rather than sliced, since a box header can straddle two
            // pooled segments. 16 bytes at most, never heap.
            var headerBytes = (int)Math.Min(16, reader.Remaining);
            if (!reader.TryCopyTo(header[..headerBytes])) return null;

            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);

            long boxSize;
            if (size == 1)
            {
                if (headerBytes < 16) return null;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if (boxSize < 16) return null;
            }
            else if (size == 0)
            {
                return null;
            }
            else
            {
                boxSize = size;
                if (boxSize < 8) return null;
            }

            if (boxSize > reader.Remaining) return null;

            reader.Advance(boxSize);
            if (type == Moof) seenMoof = true;
            if (type == stopAt && (!requireMoofFirst || seenMoof)) return reader.Consumed;
        }
        return null;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace LarisVMS.Media;

/// <summary>
/// Incrementally scans a fragmented-MP4 byte stream for top-level box boundaries, specifically to
/// find where the "init segment" (ftyp + moov — everything an MSE SourceBuffer needs before it can
/// accept any fragment) ends and the first moof/mdat fragment begins. A late-joining live viewer
/// needs exactly those leading bytes handed to it before any fragment; this only scans for the
/// boundary, it never touches the bytes themselves.
/// </summary>
public static class Mp4BoxScanner
{
    /// <summary>Given all bytes received so far from the start of the stream, returns the number of
    /// leading bytes that make up the init segment (through the end of the top-level "moov" box), or
    /// null if not yet fully received. A stream that's still arriving looks identical to one that's
    /// stalled or malformed from here, so this never throws — it just waits for more bytes.</summary>
    public static int? TryFindInitSegmentEnd(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;
        while (offset + 8 <= buffer.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset, 4));
            var type = Encoding.ASCII.GetString(buffer.Slice(offset + 4, 4));

            long boxSize;
            int headerSize;
            if (size == 1)
            {
                // 64-bit "largesize" extension — the real size lives in the 8 bytes right after the
                // type, not expected for the small init-segment boxes this is looking for, but
                // handled so a well-formed stream never gets stuck misreading a box size.
                if (offset + 16 > buffer.Length) return null;
                boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(buffer.Slice(offset + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                return null; // "box extends to end of file" — meaningless mid-stream, treat as not-yet-resolvable
            }
            else
            {
                boxSize = size;
                headerSize = 8;
            }

            if (boxSize < headerSize || offset + boxSize > buffer.Length) return null;

            offset += (int)boxSize;
            if (type == "moov") return offset;
        }
        return null;
    }
}

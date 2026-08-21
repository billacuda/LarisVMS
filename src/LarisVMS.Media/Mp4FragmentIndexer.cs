using System.Buffers.Binary;

namespace LarisVMS.Media;

/// <summary>One fragment's location and media time within a recorded segment file: ByteOffset is
/// where its top-level 'moof' box starts (the exact point a partial fetch must begin from — MSE
/// requires a fragment to start there, never mid-moof/mdat), MediaTimeSeconds is that fragment's own
/// video-track timestamp (moof/traf/tfdt, converted via the video track's mdhd timescale), which is
/// what a client compares against its desired seek offset.</summary>
public readonly record struct Mp4Fragment(long ByteOffset, double MediaTimeSeconds);

/// <summary>
/// Builds a byte-offset/media-time index for one recorded segment file, so Playback's "jump into the
/// middle of a segment" (M18 follow-up) can start a fetch at the fragment nearest the target instant
/// instead of downloading everything before it. Every segment file this app produces is already a
/// fragmented MP4 with the exact box shape MSE itself requires — ftyp, moov(empty), then a sequence
/// of moof+mdat pairs, one per keyframe (RecordingSession.BuildTeeOutputs' own
/// "+frag_keyframe+empty_moov+default_base_moof" movflags) — so this index is built by walking box
/// *headers* only, in one pass over the file with plain Seek/Read: read 8 (or 16, for a 64-bit
/// largesize) bytes, note the box, seek past its declared size, repeat. mdat's payload — the actual
/// encoded video/audio bytes, the overwhelming majority of a segment's size — is never read, only
/// skipped over by its declared length, so this stays fast (a handful of small reads) regardless of
/// how large the file is.
///
/// Box offsets/layouts below (tkhd/mdhd/tfhd/tfdt track_ID and timescale field positions, hdlr's
/// handler_type) were verified against a real recorded segment from this deployment, not assumed from
/// the spec alone — see the CHANGELOG entry that introduced this for the exact bytes inspected.
///
/// Deliberately its own class rather than an extension of Mp4BoxScanner: that class works over
/// already-buffered spans/sequences (the live-fragment-arriving-over-a-pipe case, where nothing is
/// ever seekable) and only ever needs "where does the next top-level box end" — it has no reason to
/// understand track/timescale/timestamp semantics at all. This one needs true random access (a
/// completed file on disk) and reaches three levels deeper into the box tree (moov/trak/mdia/mdhd,
/// moof/traf/tfhd+tfdt) than that scanner ever does.
/// </summary>
public static class Mp4FragmentIndexer
{
    /// <summary>Never throws — a stream that isn't the fragmented-MP4 shape this app produces (an
    /// unrelated file, a truncated/corrupted one, or simply not fully written yet) just yields
    /// whatever prefix could be parsed, empty in the worst case. Callers must treat "no fragments" or
    /// "no video-track fragment among them" as "no index available" and fall back to serving the
    /// whole file, never as an error.</summary>
    public static IReadOnlyList<Mp4Fragment> Build(Stream stream)
    {
        var fragments = new List<Mp4Fragment>();
        try
        {
            long length = stream.Length;
            var trackTimescales = new Dictionary<uint, int>();
            uint? videoTrackId = null;

            long offset = 0;
            while (offset + 8 <= length)
            {
                if (!TryReadBoxHeader(stream, offset, length, out var boxSize, out var boxType, out var headerLen))
                    break;
                var bodyStart = offset + headerLen;
                var boxEnd = offset + boxSize;
                if (boxEnd > length || boxEnd <= offset) break; // malformed — stop rather than loop/throw

                if (boxType == "moov")
                {
                    ParseMoov(stream, bodyStart, boxEnd, trackTimescales, ref videoTrackId);
                }
                else if (boxType == "moof" && videoTrackId is { } vTrackId
                    && trackTimescales.TryGetValue(vTrackId, out var vTimescale) && vTimescale > 0)
                {
                    if (TryReadFragmentVideoTime(stream, bodyStart, boxEnd, vTrackId, vTimescale, out var seconds))
                        fragments.Add(new Mp4Fragment(offset, seconds));
                }

                offset = boxEnd;
            }
        }
        catch (IOException) { /* best-effort — return whatever was collected before the failure */ }
        catch (ObjectDisposedException) { }

        return fragments;
    }

    /// <summary>Finds every 'trak', identifies which one is the video track (its 'hdlr' declares
    /// handler_type 'vide') and records every track's own mdhd timescale — needed even for non-video
    /// tracks only insofar as videoTrackId's own timescale must be looked up by trackTimescales after
    /// this returns; recording all of them (not just the video one) is simplest, since which trak is
    /// visited first isn't guaranteed to be video.</summary>
    private static void ParseMoov(Stream stream, long start, long end, Dictionary<uint, int> trackTimescales, ref uint? videoTrackId)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            if (!TryReadBoxHeader(stream, offset, end, out var boxSize, out var boxType, out var headerLen)) return;
            var bodyStart = offset + headerLen;
            var boxEnd = offset + boxSize;
            if (boxEnd > end || boxEnd <= offset) return;

            if (boxType == "trak")
            {
                ParseTrak(stream, bodyStart, boxEnd, trackTimescales, ref videoTrackId);
            }

            offset = boxEnd;
        }
    }

    private static void ParseTrak(Stream stream, long start, long end, Dictionary<uint, int> trackTimescales, ref uint? videoTrackId)
    {
        uint? trackId = null;
        int? timescale = null;
        var isVideo = false;

        var offset = start;
        while (offset + 8 <= end)
        {
            if (!TryReadBoxHeader(stream, offset, end, out var boxSize, out var boxType, out var headerLen)) return;
            var bodyStart = offset + headerLen;
            var boxEnd = offset + boxSize;
            if (boxEnd > end || boxEnd <= offset) return;

            switch (boxType)
            {
                case "tkhd":
                    trackId = ReadTrackId(stream, bodyStart, boxEnd);
                    break;
                case "mdia":
                    ParseMdia(stream, bodyStart, boxEnd, out timescale, out isVideo);
                    break;
            }

            offset = boxEnd;
        }

        if (trackId is { } id)
        {
            if (timescale is { } ts) trackTimescales[id] = ts;
            if (isVideo) videoTrackId = id;
        }
    }

    private static void ParseMdia(Stream stream, long start, long end, out int? timescale, out bool isVideo)
    {
        timescale = null;
        isVideo = false;

        var offset = start;
        while (offset + 8 <= end)
        {
            if (!TryReadBoxHeader(stream, offset, end, out var boxSize, out var boxType, out var headerLen)) return;
            var bodyStart = offset + headerLen;
            var boxEnd = offset + boxSize;
            if (boxEnd > end || boxEnd <= offset) return;

            if (boxType == "mdhd")
            {
                timescale = ReadMdhdTimescale(stream, bodyStart, boxEnd);
            }
            else if (boxType == "hdlr")
            {
                isVideo = ReadHdlrIsVideo(stream, bodyStart, boxEnd);
            }

            offset = boxEnd;
        }
    }

    /// <summary>tkhd body: version(1)+flags(3), then version0: creation_time(4)+modification_time(4)
    /// +track_ID(4); version1: creation_time(8)+modification_time(8)+track_ID(4) — same 4-byte
    /// track_ID field either way, just at a different offset depending on the widened time fields.</summary>
    private static uint? ReadTrackId(Stream stream, long bodyStart, long bodyEnd)
    {
        if (!TryReadBytes(stream, bodyStart, 1, out var versionByte)) return null;
        var version = versionByte[0];
        var trackIdOffset = version == 1 ? 20 : 12;
        if (bodyStart + trackIdOffset + 4 > bodyEnd) return null;
        return TryReadBytes(stream, bodyStart + trackIdOffset, 4, out var idBytes)
            ? BinaryPrimitives.ReadUInt32BigEndian(idBytes) : null;
    }

    /// <summary>mdhd body: version(1)+flags(3), then version0: creation_time(4)+modification_time(4)
    /// +timescale(4); version1: creation_time(8)+modification_time(8)+timescale(4).</summary>
    private static int? ReadMdhdTimescale(Stream stream, long bodyStart, long bodyEnd)
    {
        if (!TryReadBytes(stream, bodyStart, 1, out var versionByte)) return null;
        var version = versionByte[0];
        var timescaleOffset = version == 1 ? 20 : 12;
        if (bodyStart + timescaleOffset + 4 > bodyEnd) return null;
        return TryReadBytes(stream, bodyStart + timescaleOffset, 4, out var tsBytes)
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(tsBytes) : null;
    }

    /// <summary>hdlr body: version(1)+flags(3)+pre_defined(4)+handler_type(4) — 'vide' for the video
    /// track, 'soun' for audio; nothing else in this body is needed here.</summary>
    private static bool ReadHdlrIsVideo(Stream stream, long bodyStart, long bodyEnd)
    {
        if (bodyStart + 12 > bodyEnd) return false;
        if (!TryReadBytes(stream, bodyStart + 8, 4, out var handlerBytes)) return false;
        return handlerBytes[0] == 'v' && handlerBytes[1] == 'i' && handlerBytes[2] == 'd' && handlerBytes[3] == 'e';
    }

    /// <summary>Walks this moof's 'traf' children looking for the one whose 'tfhd' names the video
    /// track, then reads that traf's own 'tfdt' (baseMediaDecodeTime) and converts it to seconds via
    /// the video track's timescale. False (no fragment recorded for this moof) if the video track's
    /// traf/tfhd/tfdt isn't found — a malformed or unexpected fragment shape is simply skipped, not
    /// fatal to indexing the rest of the file.</summary>
    private static bool TryReadFragmentVideoTime(Stream stream, long start, long end, uint videoTrackId, int videoTimescale, out double seconds)
    {
        seconds = 0;
        var offset = start;
        while (offset + 8 <= end)
        {
            if (!TryReadBoxHeader(stream, offset, end, out var boxSize, out var boxType, out var headerLen)) return false;
            var bodyStart = offset + headerLen;
            var boxEnd = offset + boxSize;
            if (boxEnd > end || boxEnd <= offset) return false;

            if (boxType == "traf" && TryReadTrafVideoTime(stream, bodyStart, boxEnd, videoTrackId, videoTimescale, out seconds))
                return true;

            offset = boxEnd;
        }
        return false;
    }

    private static bool TryReadTrafVideoTime(Stream stream, long start, long end, uint videoTrackId, int videoTimescale, out double seconds)
    {
        seconds = 0;
        uint? trackId = null;
        long? baseMediaDecodeTime = null;

        var offset = start;
        while (offset + 8 <= end)
        {
            if (!TryReadBoxHeader(stream, offset, end, out var boxSize, out var boxType, out var headerLen)) return false;
            var bodyStart = offset + headerLen;
            var boxEnd = offset + boxSize;
            if (boxEnd > end || boxEnd <= offset) return false;

            switch (boxType)
            {
                case "tfhd":
                    if (TryReadBytes(stream, bodyStart + 4, 4, out var idBytes))
                        trackId = BinaryPrimitives.ReadUInt32BigEndian(idBytes);
                    break;
                case "tfdt":
                    baseMediaDecodeTime = ReadTfdt(stream, bodyStart, boxEnd);
                    break;
            }

            offset = boxEnd;
        }

        if (trackId != videoTrackId || baseMediaDecodeTime is not { } bmdt) return false;
        seconds = (double)bmdt / videoTimescale;
        return true;
    }

    /// <summary>tfdt body: version(1)+flags(3), then baseMediaDecodeTime as a uint32 (version 0) or
    /// uint64 (version 1) right after.</summary>
    private static long? ReadTfdt(Stream stream, long bodyStart, long bodyEnd)
    {
        if (!TryReadBytes(stream, bodyStart, 1, out var versionByte)) return null;
        var version = versionByte[0];
        if (version == 1)
        {
            if (bodyStart + 12 > bodyEnd) return null;
            return TryReadBytes(stream, bodyStart + 4, 8, out var wide)
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(wide) : null;
        }
        if (bodyStart + 8 > bodyEnd) return null;
        return TryReadBytes(stream, bodyStart + 4, 4, out var narrow)
            ? BinaryPrimitives.ReadUInt32BigEndian(narrow) : null;
    }

    /// <summary>Reads one box header at <paramref name="offset"/>: the ordinary 8-byte (size, type)
    /// form, or the 16-byte form when size's 32-bit field is exactly 1 (ISO 14496-12's "largesize"
    /// extension — the real size is a uint64 right after the type). size == 0 ("box extends to the
    /// end of the enclosing container") resolves against <paramref name="containerEnd"/> rather than
    /// treated as malformed, since ffmpeg's muxer can legally emit that for a trailing box.</summary>
    private static bool TryReadBoxHeader(Stream stream, long offset, long containerEnd, out long boxSize, out string boxType, out int headerLen)
    {
        boxSize = 0;
        boxType = "";
        headerLen = 8;
        if (!TryReadBytes(stream, offset, 8, out var header)) return false;

        var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
        boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);

        if (size32 == 1)
        {
            if (!TryReadBytes(stream, offset + 8, 8, out var largesize)) return false;
            boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(largesize);
            headerLen = 16;
        }
        else if (size32 == 0)
        {
            boxSize = containerEnd - offset;
        }
        else
        {
            boxSize = size32;
        }

        return boxSize >= headerLen;
    }

    private static bool TryReadBytes(Stream stream, long offset, int count, out byte[] buffer)
    {
        buffer = new byte[count];
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false; // short read (EOF) — treat as "couldn't read this field"
                read += n;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (NotSupportedException) { return false; } // non-seekable stream — shouldn't happen for a FileStream, but never throw from here
    }
}

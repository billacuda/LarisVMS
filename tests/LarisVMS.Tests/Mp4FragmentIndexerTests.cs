using System.Buffers.Binary;
using System.Text;
using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the byte-offset/media-time index Playback's "jump into the middle of a segment" (M18
/// follow-up) uses to skip straight to a fragment near a scrub target instead of downloading
/// everything before it. Built against synthetic boxes (a real segment file isn't available in a
/// unit test) whose field layouts were themselves verified against a real recorded segment from a
/// live deployment before this was written — see the CHANGELOG entry that introduced this.
/// </summary>
public class Mp4FragmentIndexerTests
{
    private static byte[] Box(string type, byte[] body)
    {
        var size = 8 + body.Length;
        var box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(params byte[][] chunks) => chunks.SelectMany(c => c).ToArray();

    private static void WriteU32(byte[] buf, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset), value);

    // version0 tkhd: version/flags(4) + creation(4) + modification(4) + track_ID(4) + reserved(4) + duration(4)
    private static byte[] Tkhd(uint trackId)
    {
        var body = new byte[24];
        WriteU32(body, 12, trackId);
        return Box("tkhd", body);
    }

    // version0 mdhd: version/flags(4) + creation(4) + modification(4) + timescale(4) + duration(4) + language(2) + pre_defined(2)
    private static byte[] Mdhd(uint timescale)
    {
        var body = new byte[20];
        WriteU32(body, 12, timescale);
        return Box("mdhd", body);
    }

    // hdlr: version/flags(4) + pre_defined(4) + handler_type(4) + reserved(12) + name(0)
    private static byte[] Hdlr(string handlerType)
    {
        var body = new byte[20];
        Encoding.ASCII.GetBytes(handlerType).CopyTo(body, 8);
        return Box("hdlr", body);
    }

    private static byte[] Trak(uint trackId, uint timescale, string handlerType) =>
        Box("trak", Concat(Tkhd(trackId), Box("mdia", Concat(Mdhd(timescale), Hdlr(handlerType)))));

    private static byte[] Moov(params byte[][] traks) => Box("moov", Concat(traks));

    // version0 tfhd: version/flags(4) + track_ID(4)
    private static byte[] Tfhd(uint trackId)
    {
        var body = new byte[8];
        WriteU32(body, 4, trackId);
        return Box("tfhd", body);
    }

    // version1 tfdt: version(1)=1, flags=0, baseMediaDecodeTime(8)
    private static byte[] Tfdt(ulong baseMediaDecodeTime)
    {
        var body = new byte[12];
        body[0] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4), baseMediaDecodeTime);
        return Box("tfdt", body);
    }

    private static byte[] Traf(uint trackId, ulong baseMediaDecodeTime) =>
        Box("traf", Concat(Tfhd(trackId), Tfdt(baseMediaDecodeTime)));

    private static byte[] Moof(params byte[][] trafs) => Box("moof", Concat(trafs));

    private static byte[] Mdat(int payloadSize) => Box("mdat", new byte[payloadSize]);

    private const uint VideoTrackId = 1;
    private const uint AudioTrackId = 2;
    private const uint VideoTimescale = 90000;
    private const uint AudioTimescale = 32000;

    private static byte[] StandardMoov() => Moov(
        Trak(VideoTrackId, VideoTimescale, "vide"),
        Trak(AudioTrackId, AudioTimescale, "soun"));

    [Fact]
    public void FindsEveryFragmentWithItsVideoTrackMediaTime()
    {
        var stream = new MemoryStream(Concat(
            Box("ftyp", new byte[12]),
            StandardMoov(),
            Moof(Traf(VideoTrackId, 0), Traf(AudioTrackId, 0)), Mdat(500),
            Moof(Traf(VideoTrackId, VideoTimescale), Traf(AudioTrackId, AudioTimescale)), Mdat(500),
            Moof(Traf(VideoTrackId, VideoTimescale * 2), Traf(AudioTrackId, AudioTimescale * 2)), Mdat(500)));

        var fragments = Mp4FragmentIndexer.Build(stream);

        Assert.Equal(3, fragments.Count);
        Assert.Equal(0.0, fragments[0].MediaTimeSeconds);
        Assert.Equal(1.0, fragments[1].MediaTimeSeconds, precision: 6);
        Assert.Equal(2.0, fragments[2].MediaTimeSeconds, precision: 6);
    }

    [Fact]
    public void FragmentByteOffsetPointsAtTheMoofBoxItself()
    {
        var ftyp = Box("ftyp", new byte[12]);
        var moov = StandardMoov();
        var moof = Moof(Traf(VideoTrackId, 0), Traf(AudioTrackId, 0));
        var stream = new MemoryStream(Concat(ftyp, moov, moof, Mdat(500)));

        var fragments = Mp4FragmentIndexer.Build(stream);

        Assert.Single(fragments);
        Assert.Equal(ftyp.Length + moov.Length, fragments[0].ByteOffset);
    }

    [Fact]
    public void UsesTheVideoTracksTimescaleNotTheAudioTracksToConvertTime()
    {
        // Audio's tfdt is deliberately a very different value (a real AAC track at 32kHz advances
        // far faster than 90kHz video does for the same wall-clock time) — if the indexer ever used
        // the wrong track's timfdt/timescale pairing, this would resolve to a wrong instant instead
        // of the video track's true one.
        var stream = new MemoryStream(Concat(
            Box("ftyp", new byte[12]),
            StandardMoov(),
            Moof(Traf(AudioTrackId, 96000), Traf(VideoTrackId, 45000)), Mdat(500)));

        var fragments = Mp4FragmentIndexer.Build(stream);

        Assert.Single(fragments);
        Assert.Equal(0.5, fragments[0].MediaTimeSeconds, precision: 6);
    }

    [Fact]
    public void SkipsAFragmentWhoseTrafNamesNoKnownVideoTrack()
    {
        // A traf for some other track_ID entirely (not video, not audio) — the indexer must not
        // mistake it for the video track's own timing, and must not fail the whole file over it.
        var stream = new MemoryStream(Concat(
            Box("ftyp", new byte[12]),
            StandardMoov(),
            Moof(Traf(99, 12345)),
            Mdat(500),
            Moof(Traf(VideoTrackId, VideoTimescale)), Mdat(500)));

        var fragments = Mp4FragmentIndexer.Build(stream);

        Assert.Single(fragments);
        Assert.Equal(1.0, fragments[0].MediaTimeSeconds, precision: 6);
    }

    [Fact]
    public void ReturnsEmptyForAPlainNonFragmentedFile()
    {
        // ftyp+moov with no video track at all (e.g. a handler_type this never matches) and no
        // fragments — must come back empty, not throw, so the caller falls back to whole-file.
        var stream = new MemoryStream(Concat(Box("ftyp", new byte[12]), Box("moov", new byte[8])));

        Assert.Empty(Mp4FragmentIndexer.Build(stream));
    }

    [Fact]
    public void ReturnsEmptyForAnEmptyStream()
    {
        Assert.Empty(Mp4FragmentIndexer.Build(new MemoryStream()));
    }

    [Fact]
    public void NeverThrowsOnATruncatedSecondFragment()
    {
        var prefix = Concat(
            Box("ftyp", new byte[12]),
            StandardMoov(),
            Moof(Traf(VideoTrackId, 0)), Mdat(500));
        var secondMoof = Moof(Traf(VideoTrackId, VideoTimescale));
        // Only 4 of the second moof's bytes ever arrived — short of even its own 8-byte header, the
        // realistic "still being written" shape for a segment file mid-recording.
        var truncated = Concat(prefix, secondMoof.AsSpan(0, 4).ToArray());
        var stream = new MemoryStream(truncated);

        var fragments = Mp4FragmentIndexer.Build(stream);

        // The first, fully-present fragment is still found; the truncated second one simply isn't —
        // and this must not throw getting there.
        Assert.Single(fragments);
        Assert.Equal(0.0, fragments[0].MediaTimeSeconds);
    }

    [Fact]
    public void NeverThrowsWhenMoovItselfIsIncomplete()
    {
        var ftyp = Box("ftyp", new byte[12]);
        var moov = StandardMoov();
        var stream = new MemoryStream(Concat(ftyp, moov)[..^10]);

        var fragments = Mp4FragmentIndexer.Build(stream);

        Assert.Empty(fragments);
    }
}

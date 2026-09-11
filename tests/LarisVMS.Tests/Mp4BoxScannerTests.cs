using System.Buffers;
using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the box-boundary scan the live-view fanout relies on to know where an fMP4 init segment
/// (ftyp+moov) ends and the first fragment (moof+mdat) begins — built and tested against synthetic
/// boxes since a real camera isn't available in a unit test.
/// </summary>
public class Mp4BoxScannerTests
{
    private static byte[] Box(string type, int payloadSize)
    {
        var size = 8 + payloadSize;
        var box = new byte[size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        return box;
    }

    private static byte[] Concat(params byte[][] chunks) => chunks.SelectMany(c => c).ToArray();

    [Fact]
    public void ReturnsNullWhenOnlyFtypReceived()
    {
        var buffer = Box("ftyp", 12);

        Assert.Null(Mp4BoxScanner.TryFindInitSegmentEnd(buffer));
    }

    [Fact]
    public void ReturnsBoundaryRightAfterMoovWhenComplete()
    {
        var ftyp = Box("ftyp", 12);
        var moov = Box("moov", 8);
        var buffer = Concat(ftyp, moov);

        var end = Mp4BoxScanner.TryFindInitSegmentEnd(buffer);

        Assert.Equal(ftyp.Length + moov.Length, end);
    }

    [Fact]
    public void IgnoresFragmentBoxesAfterMoov()
    {
        var ftyp = Box("ftyp", 12);
        var moov = Box("moov", 8);
        var moof = Box("moof", 4);
        var mdat = Box("mdat", 4);
        var buffer = Concat(ftyp, moov, moof, mdat);

        var end = Mp4BoxScanner.TryFindInitSegmentEnd(buffer);

        Assert.Equal(ftyp.Length + moov.Length, end);
    }

    [Fact]
    public void ReturnsNullWhenMoovBoxIsOnlyPartiallyReceived()
    {
        var ftyp = Box("ftyp", 12);
        var moov = Box("moov", 8);
        var buffer = Concat(ftyp, moov)[..^3]; // moov's last 3 bytes haven't arrived yet

        Assert.Null(Mp4BoxScanner.TryFindInitSegmentEnd(buffer));
    }

    [Fact]
    public void ReturnsNullOnEmptyBuffer()
    {
        Assert.Null(Mp4BoxScanner.TryFindInitSegmentEnd(ReadOnlySpan<byte>.Empty));
    }

    // --- TryFindFragmentEnd: what a live viewer is allowed to be handed ---

    [Fact]
    public void FragmentEndIsRightAfterTheMdatClosingTheMoof()
    {
        var buffer = Concat(Box("moof", 40), Box("mdat", 200));

        Assert.Equal(48 + 208, Mp4BoxScanner.TryFindFragmentEnd(buffer));
    }

    [Fact]
    public void FragmentIsIncompleteUntilTheWholeMdatHasArrived()
    {
        var full = Concat(Box("moof", 40), Box("mdat", 200));

        // One byte short is still incomplete — this is the case that used to be forwarded anyway,
        // leaving the decoder to choke on a truncated box.
        Assert.Null(Mp4BoxScanner.TryFindFragmentEnd(full.AsSpan(0, full.Length - 1)));
        Assert.NotNull(Mp4BoxScanner.TryFindFragmentEnd(full));
    }

    [Fact]
    public void AMoofAloneIsNotACompleteFragment()
    {
        Assert.Null(Mp4BoxScanner.TryFindFragmentEnd(Box("moof", 40)));
    }

    [Fact]
    public void AnMdatWithNoPrecedingMoofDoesNotEndAFragment()
    {
        // Guards against mistaking a resync landing mid-stream for a complete fragment.
        Assert.Null(Mp4BoxScanner.TryFindFragmentEnd(Box("mdat", 200)));
    }

    [Fact]
    public void ALeadingStypIsIncludedInTheFragment()
    {
        var buffer = Concat(Box("styp", 16), Box("moof", 40), Box("mdat", 200));

        Assert.Equal(24 + 48 + 208, Mp4BoxScanner.TryFindFragmentEnd(buffer));
    }

    [Fact]
    public void OnlyTheFirstFragmentIsReturnedWhenSeveralAreBuffered()
    {
        var first = Concat(Box("moof", 40), Box("mdat", 200));
        var buffer = Concat(first, Box("moof", 40), Box("mdat", 500));

        Assert.Equal(first.Length, Mp4BoxScanner.TryFindFragmentEnd(buffer));
    }

    // With the live pipe leg now muxed at frag_duration=500ms, a single PipeReader read commonly
    // holds many small keyframe-less fragments back to back. DrainStdoutAsync peels them off one at a
    // time in a loop; this proves the scan keeps finding exactly one boundary per iteration and
    // never runs the slices together.
    [Fact]
    public void PeelsOffManySmallFragmentsOneAtATime()
    {
        var fragments = Enumerable.Range(0, 12)
            .Select(i => Concat(Box("moof", 24), Box("mdat", 40 + i))) // varied sizes so an off-by-one would misalign
            .ToArray();
        var buffer = Concat(fragments);

        var consumed = 0;
        foreach (var expected in fragments)
        {
            var end = Mp4BoxScanner.TryFindFragmentEnd(buffer.AsSpan(consumed));
            Assert.Equal(expected.Length, end);
            consumed += end!.Value;
        }
        Assert.Equal(buffer.Length, consumed);
        Assert.Null(Mp4BoxScanner.TryFindFragmentEnd(buffer.AsSpan(consumed)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(50)]
    public void PeelsOffManySmallFragmentsAcrossSegmentSplits(int chunk)
    {
        var fragments = Enumerable.Range(0, 10)
            .Select(i => Concat(Box("moof", 24), Box("mdat", 32 + i)))
            .ToArray();
        var whole = Concat(fragments);

        var consumed = 0;
        foreach (var expected in fragments)
        {
            var remaining = whole[consumed..];
            var end = Mp4BoxScanner.TryFindFragmentEnd(Segmented(remaining, chunk));
            Assert.Equal(expected.Length, end);
            consumed += (int)end!.Value;
        }
        Assert.Equal(whole.Length, consumed);
    }

    [Fact]
    public void HandlesA64BitMdatSize()
    {
        // mdat is the one box that legitimately uses the largesize form.
        var moof = Box("moof", 40);
        var mdat = new byte[16 + 100];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        System.Text.Encoding.ASCII.GetBytes("mdat").CopyTo(mdat, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);

        Assert.Equal(moof.Length + mdat.Length, Mp4BoxScanner.TryFindFragmentEnd(Concat(moof, mdat)));
    }

    [Fact]
    public void ReturnsNullOnEmptyBufferForFragments()
    {
        Assert.Null(Mp4BoxScanner.TryFindFragmentEnd(ReadOnlySpan<byte>.Empty));
    }

    // --- ReadOnlySequence overloads: what PipeReader actually hands over ---

    /// <summary>Builds a genuinely multi-segment sequence, splitting at <paramref name="chunk"/>
    /// bytes. A single-segment sequence would silently take the span fast path and prove nothing
    /// about the case this exists for.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] data, int chunk)
    {
        Segment? first = null, last = null;
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            var count = Math.Min(chunk, data.Length - offset);
            var next = new Segment(data.AsMemory(offset, count), last);
            first ??= next;
            last = next;
        }
        if (first is null) return ReadOnlySequence<byte>.Empty;
        return new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, Segment? previous)
        {
            Memory = memory;
            RunningIndex = previous is null ? 0 : previous.RunningIndex + previous.Memory.Length;
            if (previous is not null) previous.Next = this;
        }
    }

    [Theory]
    // Chunk sizes chosen to split inside the moof header, inside the mdat header, and mid-payload —
    // a box header straddling two pooled segments is the whole reason the sequence path exists.
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(64)]
    public void AFragmentIsFoundAcrossAnySegmentSplit(int chunk)
    {
        var fragment = Concat(Box("moof", 40), Box("mdat", 200));
        var sequence = Segmented(fragment, chunk);

        Assert.False(sequence.IsSingleSegment || chunk >= fragment.Length, "test must exercise the multi-segment path");
        Assert.Equal(fragment.Length, Mp4BoxScanner.TryFindFragmentEnd(sequence));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(13)]
    public void AnInitSegmentIsFoundAcrossAnySegmentSplit(int chunk)
    {
        var init = Concat(Box("ftyp", 12), Box("moov", 64));
        var trailing = Concat(init, Box("moof", 40));

        Assert.Equal(init.Length, Mp4BoxScanner.TryFindInitSegmentEnd(Segmented(trailing, chunk)));
    }

    [Fact]
    public void AnIncompleteFragmentIsNullAcrossSegments()
    {
        var full = Concat(Box("moof", 40), Box("mdat", 200));

        Assert.Null(Mp4BoxScanner.TryFindFragmentEnd(Segmented(full[..^1], 7)));
    }

    [Fact]
    public void TheSequenceAndSpanOverloadsAgree()
    {
        // The single-segment fast path and the SequenceReader walk must never disagree, or a
        // fragment boundary would shift depending on how the pipe happened to segment its buffers.
        var stream = Concat(
            Box("styp", 16), Box("moof", 40), Box("mdat", 500),
            Box("moof", 40), Box("mdat", 100));

        var viaSpan = Mp4BoxScanner.TryFindFragmentEnd(stream.AsSpan());
        foreach (var chunk in new[] { 1, 2, 7, 33, 128, 4096 })
        {
            Assert.Equal(viaSpan, Mp4BoxScanner.TryFindFragmentEnd(Segmented(stream, chunk)));
        }
    }

    [Fact]
    public void A64BitMdatIsHandledAcrossSegments()
    {
        var moof = Box("moof", 40);
        var mdat = new byte[16 + 100];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        System.Text.Encoding.ASCII.GetBytes("mdat").CopyTo(mdat, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
        var fragment = Concat(moof, mdat);

        // Split at 12 lands inside the largesize field itself.
        Assert.Equal(fragment.Length, Mp4BoxScanner.TryFindFragmentEnd(Segmented(fragment, 12)));
    }

    [Fact]
    public void ScanningAllocatesNothing()
    {
        // Box types used to be decoded to 4-char strings, one per box per call — on a path that sees
        // every byte ffmpeg emits, for every camera, forever.
        var stream = Concat(Box("moof", 40), Box("mdat", 4096));
        Mp4BoxScanner.TryFindFragmentEnd(stream.AsSpan()); // warm up

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) Mp4BoxScanner.TryFindFragmentEnd(stream.AsSpan());
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}

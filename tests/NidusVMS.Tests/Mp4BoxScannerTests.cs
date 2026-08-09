using NidusVMS.Media;

namespace NidusVMS.Tests;

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
}

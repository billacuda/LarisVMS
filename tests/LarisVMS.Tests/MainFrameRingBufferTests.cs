using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Pass 3a: MainFrameRingBuffer buffers recent Main-stream fMP4 fragments in RAM so a later, on-demand
/// caller can fetch a recent instant to decode. Exercises the pure eviction/lookup logic directly,
/// with explicit timestamps (not DateTime.UtcNow) so eviction-by-time is deterministic.
/// </summary>
public class MainFrameRingBufferTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static byte[] Init(int seed = 1) => [(byte)seed, (byte)seed, (byte)seed];
    private static byte[] Fragment(int seed) => [(byte)seed, (byte)seed, (byte)seed, (byte)seed];

    [Fact]
    public void EmptyBufferReturnsNullRatherThanThrowing()
    {
        var ring = new MainFrameRingBuffer();
        Assert.Null(ring.TryGet(BaseTime));
    }

    [Fact]
    public void OnFragmentWithNoInitSegmentIsANoOp()
    {
        var ring = new MainFrameRingBuffer();
        ring.OnFragment(null, Fragment(1), BaseTime);
        Assert.Null(ring.TryGet(BaseTime));
    }

    [Fact]
    public void TryGetReturnsTheFragmentClosestToTheRequestedInstant()
    {
        var ring = new MainFrameRingBuffer();
        var init = Init();
        ring.OnFragment(init, Fragment(1), BaseTime);
        ring.OnFragment(init, Fragment(2), BaseTime.AddSeconds(2));
        ring.OnFragment(init, Fragment(3), BaseTime.AddSeconds(4));

        var result = ring.TryGet(BaseTime.AddSeconds(2.2));

        Assert.NotNull(result);
        Assert.Equal(init, result.Value.InitSegment);
        Assert.Equal(Fragment(2), result.Value.Fragment);
    }

    [Fact]
    public void EvictsFragmentsOlderThanTheTimeWindow()
    {
        var ring = new MainFrameRingBuffer(window: TimeSpan.FromSeconds(10));
        var init = Init();
        ring.OnFragment(init, Fragment(1), BaseTime);
        // 11s later — the first fragment is now outside the 10s window and must be evicted.
        ring.OnFragment(init, Fragment(2), BaseTime.AddSeconds(11));

        // Asking for an instant near the (now-evicted) first fragment should return the only
        // remaining one, not the evicted one.
        var result = ring.TryGet(BaseTime);

        Assert.NotNull(result);
        Assert.Equal(Fragment(2), result.Value.Fragment);
    }

    [Fact]
    public void EvictsOldestFragmentsFirstWhenOverTheByteCeiling()
    {
        // Each fragment is 4 bytes; a 10-byte ceiling allows at most 2 of them.
        var ring = new MainFrameRingBuffer(window: TimeSpan.FromHours(1), maxBytes: 10);
        var init = Init();
        ring.OnFragment(init, Fragment(1), BaseTime);
        ring.OnFragment(init, Fragment(2), BaseTime.AddSeconds(1));
        ring.OnFragment(init, Fragment(3), BaseTime.AddSeconds(2));

        // Fragment 1 should have been evicted for space; asking near its own timestamp should now
        // resolve to fragment 2, the oldest survivor.
        var result = ring.TryGet(BaseTime);

        Assert.NotNull(result);
        Assert.Equal(Fragment(2), result.Value.Fragment);
    }

    [Fact]
    public void ChangingTheInitSegmentClearsPriorFragments()
    {
        var ring = new MainFrameRingBuffer();
        var firstInit = Init(1);
        // A distinct array instance with byte-identical content — proves detection is by reference,
        // matching RecordingSession's own LiveInitSegment, which is reassigned to a fresh array on
        // every ffmpeg restart even if (in principle) the bytes came out the same.
        var secondInit = Init(1);
        ring.OnFragment(firstInit, Fragment(1), BaseTime);
        ring.OnFragment(secondInit, Fragment(2), BaseTime.AddSeconds(1));

        var result = ring.TryGet(BaseTime);

        Assert.NotNull(result);
        Assert.Same(secondInit, result.Value.InitSegment);
        Assert.Equal(Fragment(2), result.Value.Fragment); // fragment 1 was dropped, not just superseded
    }
}

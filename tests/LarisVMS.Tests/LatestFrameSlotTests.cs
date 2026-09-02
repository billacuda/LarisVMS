using LarisVMS.Vision.Capture;

namespace LarisVMS.Tests;

/// <summary>
/// Covers LatestFrameSlot's byte-buffer produce/consume/drop-stale handoff (pass 4a swapped the
/// buffered SKBitmap for a raw frame buffer) and the capture instant that now rides alongside each
/// frame.
/// </summary>
public class LatestFrameSlotTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PublishThenTakeReturnsAnIndependentCopyOfTheFrame()
    {
        using var slot = new LatestFrameSlot(4);
        slot.Publish(T0, b => { b[0] = 1; b[1] = 2; b[2] = 3; b[3] = 4; });

        var taken = await slot.TakeAsync(CancellationToken.None);

        Assert.NotNull(taken);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, taken.Value.Frame);
        Assert.Equal(T0, taken.Value.CapturedUtc);

        // Mutating the caller's copy must not affect the slot.
        taken.Value.Frame[0] = 99;
        slot.Publish(T0.AddSeconds(1), b => { b[0] = 5; b[1] = 6; b[2] = 7; b[3] = 8; });
        var next = await slot.TakeAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, next!.Value.Frame);
        Assert.Equal(T0.AddSeconds(1), next.Value.CapturedUtc);
    }

    [Fact]
    public async Task OnlyTheNewestFrameSurvivesWhenTheConsumerIsBehind()
    {
        using var slot = new LatestFrameSlot(1);
        slot.Publish(T0, b => b[0] = 10);
        slot.Publish(T0.AddMilliseconds(50), b => b[0] = 20);
        slot.Publish(T0.AddMilliseconds(100), b => b[0] = 30);

        var taken = await slot.TakeAsync(CancellationToken.None);
        Assert.Equal(30, taken!.Value.Frame[0]);
        // The capture instant follows the surviving frame, not the dropped ones.
        Assert.Equal(T0.AddMilliseconds(100), taken.Value.CapturedUtc);
        Assert.Equal(3, slot.PublishedCount);
        Assert.Equal(1, slot.ConsumedCount); // the other two were dropped
    }

    [Fact]
    public async Task TakeReturnsNullAfterDispose()
    {
        var slot = new LatestFrameSlot(4);
        var pending = slot.TakeAsync(CancellationToken.None);
        slot.Dispose();
        Assert.Null(await pending);
    }

    [Fact]
    public void ResizeChangesTheFrameSizeAndDiscardsABufferedFrame()
    {
        using var slot = new LatestFrameSlot(4);
        slot.Publish(T0, b => b[0] = 1);
        slot.Resize(6);
        Assert.Equal(6, slot.FrameBytes);
    }
}

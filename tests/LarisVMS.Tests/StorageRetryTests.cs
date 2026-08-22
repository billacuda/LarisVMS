using LarisVMS.Node;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the retry-with-backoff a transient storage I/O error against the shared recording storage
/// gets before a caller gives up — confirmed live (2026-08-21) as a real need: a dropped SMB session
/// surfaced as IOException from both a directory enumeration and, plausibly, individual file opens
/// sharing the same UNC path. Uses NullLogger so these run fast and don't depend on log wiring; the
/// short built-in delays (a few seconds worst case) mean these tests are the one place in the suite
/// that isn't near-instant, but that's the real behavior being verified, not something to fake away.
/// </summary>
public class StorageRetryTests
{
    [Fact]
    public async Task ExecuteAsyncReturnsImmediatelyOnFirstSuccess()
    {
        var calls = 0;
        var result = await StorageRetry.ExecuteAsync(NullLogger.Instance, "test", () =>
        {
            calls++;
            return Task.FromResult(42);
        }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsyncRetriesOnIOExceptionAndSucceedsOnceItClears()
    {
        var calls = 0;
        var result = await StorageRetry.ExecuteAsync(NullLogger.Instance, "test", () =>
        {
            calls++;
            if (calls < 3) throw new IOException("An unexpected network error occurred.");
            return Task.FromResult(7);
        }, CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExecuteAsyncGivesUpAfterExhaustingRetriesAndRethrowsTheLastException()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<IOException>(() => StorageRetry.ExecuteAsync<int>(NullLogger.Instance, "test", () =>
        {
            calls++;
            throw new IOException("still down");
        }, CancellationToken.None));

        Assert.Equal("still down", ex.Message);
        // Initial attempt plus one retry per configured delay (3 today) — proves it doesn't retry
        // forever. Not hardcoding the exact delay count as a magic "4" here to avoid this test
        // silently going stale if StorageRetry's own Delays array is ever resized — it just needs to
        // be small and bounded, not unbounded.
        Assert.True(calls is > 1 and <= 10, $"expected a small bounded number of attempts, got {calls}");
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotCatchNonIOExceptions()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StorageRetry.ExecuteAsync<int>(NullLogger.Instance, "test", () =>
        {
            calls++;
            throw new InvalidOperationException("not a storage problem");
        }, CancellationToken.None));

        Assert.Equal(1, calls); // no retry for an exception type this isn't meant to mask
    }

    [Fact]
    public void ExecuteRetriesSynchronouslyAndSucceedsOnceItClears()
    {
        var calls = 0;
        var result = StorageRetry.Execute(NullLogger.Instance, "test", () =>
        {
            calls++;
            if (calls < 2) throw new IOException("An unexpected network error occurred.");
            return "ok";
        });

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }
}

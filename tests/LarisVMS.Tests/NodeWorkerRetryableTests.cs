using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// Locks in the fix for a confirmed multi-hour reporting outage (nvr1, 2026-08-21 16:36→18:56): an
/// HttpClient timeout throws TaskCanceledException, which derives from OperationCanceledException, so
/// the old `ex is not OperationCanceledException` filter let it escape, faulting the whole report loop
/// and silently killing every node→web report until the service restarted — while recording carried on
/// writing files nobody ever heard about. See NodeWorker.IsRetryable's own doc comment.
/// </summary>
public class NodeWorkerRetryableTests
{
    [Fact]
    public void AnHttpTimeoutIsRetryableWhenOurOwnTokenIsNotCancelled()
    {
        // Exactly what HttpClient throws on Timeout: a TaskCanceledException, with no cancellation of
        // the token *we* passed in. This is the case that caused the outage.
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        Assert.True(NodeWorker.IsRetryable(ex, CancellationToken.None));
    }

    [Fact]
    public void ARealShutdownIsNotRetryable()
    {
        // Our own token cancelled — the loop should end cleanly rather than log-and-retry forever.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(NodeWorker.IsRetryable(new OperationCanceledException(cts.Token), cts.Token));
        Assert.False(NodeWorker.IsRetryable(new TaskCanceledException(), cts.Token));
    }

    [Fact]
    public void AnOrdinaryFailureIsRetryableEitherWay()
    {
        using var cts = new CancellationTokenSource();

        Assert.True(NodeWorker.IsRetryable(new HttpRequestException("connection refused"), cts.Token));
        cts.Cancel();
        // Still retryable by this predicate even mid-shutdown — the loop's own `while
        // (!ct.IsCancellationRequested)` is what actually ends it, and swallowing a genuine transport
        // error on the way out is harmless next to letting one kill reporting outright.
        Assert.True(NodeWorker.IsRetryable(new HttpRequestException("connection refused"), cts.Token));
    }
}

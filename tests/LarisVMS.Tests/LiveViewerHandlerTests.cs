using System.Net.WebSockets;
using System.Threading.Channels;
using LarisVMS.Media;
using LarisVMS.Node;
using Microsoft.Extensions.Logging.Abstractions;

namespace LarisVMS.Tests;

/// <summary>
/// Covers LiveViewerHandler's slow-viewer handling: when a viewer's socket write can't keep up, the
/// per-viewer queue must NOT silently drop a fragment from the middle of the stream (that splices an
/// unrecoverable gap into the browser's SourceBuffer). Instead the handler stops forwarding, drains
/// what is already queued, and closes the socket with a policy-violation status so the client
/// reconnects cleanly with a fresh init segment.
/// </summary>
public class LiveViewerHandlerTests
{
    private sealed class FakeLiveSource : ILiveSource
    {
        public event Action<byte[]>? LiveFragmentReceived;
        public Task<byte[]> WaitForLiveInitSegmentAsync(CancellationToken ct) => Task.FromResult(new byte[] { 0xFF });
        public void Raise(byte[] fragment) => LiveFragmentReceived?.Invoke(fragment);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<byte[]> Sent = [];
        public WebSocketCloseStatus? ClosedWith { get; private set; }
        public string? ClosedReason { get; private set; }

        public void ReleaseSends() => _gate.TrySetResult();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            Sent.Add(buffer.ToArray());
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = closeStatus;
            ClosedReason = statusDescription;
            return Task.CompletedTask;
        }

        public override WebSocketState State => ClosedWith is null ? WebSocketState.Open : WebSocketState.Closed;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => new TaskCompletionSource<WebSocketReceiveResult>().Task; // never called by the handler
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Abort() { }
        public override void Dispose() { }
        public override WebSocketCloseStatus? CloseStatus => ClosedWith;
        public override string? CloseStatusDescription => ClosedReason;
        public override string? SubProtocol => null;
    }

    [Fact]
    public async Task AViewerThatFallsBehindIsClosedCleanlyWithoutSplicingAGap()
    {
        var source = new FakeLiveSource();
        var socket = new FakeWebSocket();

        // Run the handler; its first SendAsync (the init segment) blocks on the gate, so the read
        // loop never drains the channel while we flood it.
        var run = LiveViewerHandler.RunAsync(socket, source, NullLogger.Instance, CancellationToken.None);

        // Give the handler a moment to subscribe before raising fragments.
        await Task.Delay(50);

        // Far more fragments than the queue can hold — each tagged with its index in byte 0.
        const int produced = 400;
        for (var i = 0; i < produced; i++) source.Raise([(byte)(i & 0xFF), 1, 2, 3]);

        socket.ReleaseSends();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Closed with the "fell behind" signal, not a normal closure.
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.ClosedWith);
        Assert.Equal("viewer fell behind — reconnect", socket.ClosedReason);

        // First message is the init segment; every fragment after it is a strictly contiguous run
        // from index 0 — never a value skipped, which is what a DropOldest hole would have produced.
        Assert.Equal(new byte[] { 0xFF }, socket.Sent[0]);
        var forwardedIndices = socket.Sent.Skip(1).Select(m => m[0]).ToList();
        Assert.NotEmpty(forwardedIndices);
        Assert.Equal(Enumerable.Range(0, forwardedIndices.Count).Select(i => (byte)i), forwardedIndices);
    }

    [Fact]
    public async Task AViewerThatKeepsUpGetsANormalClosureWhenTheStreamEnds()
    {
        var source = new FakeLiveSource();
        var socket = new FakeWebSocket();
        socket.ReleaseSends(); // sends complete immediately — this viewer keeps up

        using var cts = new CancellationTokenSource();
        var run = LiveViewerHandler.RunAsync(socket, source, NullLogger.Instance, cts.Token);
        await Task.Delay(50);

        for (var i = 0; i < 10; i++) source.Raise([(byte)i]);
        await Task.Delay(100);
        cts.Cancel(); // client disconnects / handler shuts down

        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWith);
        Assert.Equal(new byte[] { 0xFF }, socket.Sent[0]);
        Assert.Equal(11, socket.Sent.Count); // init + 10 fragments, all forwarded
    }
}

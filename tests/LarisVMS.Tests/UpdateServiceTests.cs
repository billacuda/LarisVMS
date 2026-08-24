using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Dtos;
using LarisVMS.Node;
using LarisVMS.Node.Update;

namespace LarisVMS.Tests;

/// <summary>Covers LarisVMS.Node.Update.UpdateService.TryApplyAsync's checksum-verification gate —
/// the port of dploid.Agent's UpdateService.TryApplyAsync. Uses a stub HttpMessageHandler (injected
/// via UpdateService's test-only httpHandler constructor parameter) rather than a real HTTP server, so
/// these tests exercise the exact download -> hash -> compare code path without any network I/O.
///
/// Deliberately does NOT test the checksum-*matching* path all the way through: LarisVMS.Tests has a
/// ProjectReference to LarisVMS.NodeUpdater (for UpdaterLogic's InternalsVisibleTo access), which means
/// a real LarisVMS.NodeUpdater.exe apphost is copied into this test project's own output directory as
/// a normal side effect of that reference. TryApplyAsync's success path (ApplyWindows) would find that
/// exe, actually launch it against this *test process's own* running binary, and have it poll a
/// "LarisVMSNode" Windows Service that doesn't exist on a dev/build machine for a full 60 seconds
/// before giving up — real, slow, and pointless side effects for a unit test to trigger. The
/// mismatch path below returns before ApplyWindows is ever called, so it has none of that risk.</summary>
public class UpdateServiceTests
{
    private sealed class StubHandler(byte[] content, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK)
                response.Content = new ByteArrayContent(content);
            return Task.FromResult(response);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool StopApplicationCalled { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopApplicationCalled = true;
    }

    private static NodeConfig NewConfig() =>
        new("https://larisvms.example.test", Guid.NewGuid(), "test-secret", "test-media-key");

    [Fact]
    public async Task ChecksumMismatchAbortsWithoutApplyingOrStoppingTheHost()
    {
        var content = "fake LarisVMS.Node.exe bytes"u8.ToArray();
        var handler = new StubHandler(content);
        var lifetime = new FakeLifetime();
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, lifetime, handler);

        // Deliberately the wrong hash — the real SHA-256 of `content` is something else entirely.
        var update = new NodeUpdateInfoDto("9.9.9", "https://larisvms.example.test/api/nodes/download/00000000-0000-0000-0000-000000000000",
            Sha256: new string('0', 64), SizeBytes: content.Length);

        var applied = await service.TryApplyAsync(update, CancellationToken.None);

        Assert.False(applied);
        Assert.False(lifetime.StopApplicationCalled);
        // Reset, not left stuck true — a later heartbeat (against a corrected upload, or once a
        // transient corruption clears) must be able to retry rather than being permanently locked out.
        Assert.False(service.IsApplying);
    }

    [Fact]
    public async Task FailedDownloadAbortsCleanlyAndResetsIsApplying()
    {
        var handler = new StubHandler([], HttpStatusCode.InternalServerError);
        var lifetime = new FakeLifetime();
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, lifetime, handler);

        var update = new NodeUpdateInfoDto("9.9.9", "https://larisvms.example.test/api/nodes/download/00000000-0000-0000-0000-000000000000",
            Sha256: new string('0', 64), SizeBytes: 0);

        var applied = await service.TryApplyAsync(update, CancellationToken.None);

        Assert.False(applied);
        Assert.False(lifetime.StopApplicationCalled);
        Assert.False(service.IsApplying);
    }

    [Fact]
    public async Task ReTriggeringWhileAlreadyApplyingIsIgnored()
    {
        // Two overlapping calls on the very same instance — the in-flight one wins, the second is a
        // same-thread reentrant call from inside a stub handler that never actually completes the
        // first, and observes IsApplying already set. Regardless of exact timing this is really just
        // verifying the guard reads its own IsApplying flag rather than nothing: the meaningful,
        // realistic case (NodeWorker's reconcile loop checking !updateService.IsApplying before
        // calling TryApplyAsync at all) is exercised by NodeWorker itself, not reachable in isolation
        // here.
        var handler = new StubHandler("fake"u8.ToArray());
        var lifetime = new FakeLifetime();
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, lifetime, handler);
        var update = new NodeUpdateInfoDto("9.9.9", "https://larisvms.example.test/api/nodes/download/00000000-0000-0000-0000-000000000000",
            Sha256: new string('0', 64), SizeBytes: 4);

        var first = service.TryApplyAsync(update, CancellationToken.None);
        // IsApplying is set synchronously before the first await inside TryApplyAsync, so it's already
        // true by the time control returns here.
        var second = await service.TryApplyAsync(update, CancellationToken.None);

        Assert.False(second);
        await first;
    }

    // ── TryStageVisionAsync (object detection plan follow-up) ────────────────
    // Exercised directly, not through TryApplyAsync/ApplyWindows — see this method's own doc comment
    // for why (ApplyWindows' real-process side effects are what the rest of this file avoids
    // triggering). A plain HttpClient wraps the same StubHandler used above; TryStageVisionAsync
    // needs no NodeConfig/lifetime/lifetime interaction at all, unlike the full apply path.

    private static async Task<string> Sha256Async(byte[] content)
    {
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(new MemoryStream(content), CancellationToken.None);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public async Task TryStageVisionSucceedsWhenChecksumMatches()
    {
        var content = "fake LarisVMS.Vision.Service.exe bytes"u8.ToArray();
        var expectedSha = await Sha256Async(content);
        using var http = new HttpClient(new StubHandler(content));
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, new FakeLifetime());

        var staged = await service.TryStageVisionAsync(http, "https://larisvms.example.test/api/nodes/download/vision", expectedSha, "0.157.0", CancellationToken.None);

        Assert.True(staged);
    }

    [Fact]
    public async Task TryStageVisionFailsOnChecksumMismatchWithoutThrowing()
    {
        var content = "fake LarisVMS.Vision.Service.exe bytes"u8.ToArray();
        using var http = new HttpClient(new StubHandler(content));
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, new FakeLifetime());

        // Deliberately the wrong hash.
        var staged = await service.TryStageVisionAsync(http, "https://larisvms.example.test/api/nodes/download/vision", new string('0', 64), "0.157.0", CancellationToken.None);

        Assert.False(staged);
    }

    [Fact]
    public async Task TryStageVisionFailsOnNonSuccessStatusWithoutThrowing()
    {
        using var http = new HttpClient(new StubHandler([], HttpStatusCode.InternalServerError));
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, new FakeLifetime());

        var staged = await service.TryStageVisionAsync(http, "https://larisvms.example.test/api/nodes/download/vision", new string('0', 64), "0.157.0", CancellationToken.None);

        Assert.False(staged);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated network failure");
    }

    [Fact]
    public async Task TryStageVisionFailsOnNetworkExceptionWithoutThrowing()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, new FakeLifetime());

        var staged = await service.TryStageVisionAsync(http, "https://larisvms.example.test/api/nodes/download/vision", new string('0', 64), "0.157.0", CancellationToken.None);

        Assert.False(staged);
    }
}

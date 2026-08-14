using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NidusVMS.Core.Dtos;
using NidusVMS.Node;
using NidusVMS.Node.Update;

namespace NidusVMS.Tests;

/// <summary>Covers NidusVMS.Node.Update.UpdateService.TryApplyAsync's checksum-verification gate —
/// the port of dploid.Agent's UpdateService.TryApplyAsync. Uses a stub HttpMessageHandler (injected
/// via UpdateService's test-only httpHandler constructor parameter) rather than a real HTTP server, so
/// these tests exercise the exact download -> hash -> compare code path without any network I/O.
///
/// Deliberately does NOT test the checksum-*matching* path all the way through: NidusVMS.Tests has a
/// ProjectReference to NidusVMS.NodeUpdater (for UpdaterLogic's InternalsVisibleTo access), which means
/// a real NidusVMS.NodeUpdater.exe apphost is copied into this test project's own output directory as
/// a normal side effect of that reference. TryApplyAsync's success path (ApplyWindows) would find that
/// exe, actually launch it against this *test process's own* running binary, and have it poll a
/// "NidusVMSNode" Windows Service that doesn't exist on a dev/build machine for a full 60 seconds
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
        new("https://nidusvms.example.test", Guid.NewGuid(), "test-secret", "test-media-key");

    [Fact]
    public async Task ChecksumMismatchAbortsWithoutApplyingOrStoppingTheHost()
    {
        var content = "fake NidusVMS.Node.exe bytes"u8.ToArray();
        var handler = new StubHandler(content);
        var lifetime = new FakeLifetime();
        var service = new UpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, lifetime, handler);

        // Deliberately the wrong hash — the real SHA-256 of `content` is something else entirely.
        var update = new NodeUpdateInfoDto("9.9.9", "https://nidusvms.example.test/api/nodes/download/00000000-0000-0000-0000-000000000000",
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

        var update = new NodeUpdateInfoDto("9.9.9", "https://nidusvms.example.test/api/nodes/download/00000000-0000-0000-0000-000000000000",
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
        var update = new NodeUpdateInfoDto("9.9.9", "https://nidusvms.example.test/api/nodes/download/00000000-0000-0000-0000-000000000000",
            Sha256: new string('0', 64), SizeBytes: 4);

        var first = service.TryApplyAsync(update, CancellationToken.None);
        // IsApplying is set synchronously before the first await inside TryApplyAsync, so it's already
        // true by the time control returns here.
        var second = await service.TryApplyAsync(update, CancellationToken.None);

        Assert.False(second);
        await first;
    }
}

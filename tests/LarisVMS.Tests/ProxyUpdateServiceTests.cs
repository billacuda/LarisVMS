using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Dtos;
using LarisVMS.Proxy;

namespace LarisVMS.Tests;

/// <summary>Covers ProxyUpdateService.TryApplyAsync's checksum-verification gate — the proxy analogue
/// of UpdateServiceTests. Uses a stub HttpMessageHandler (injected via the test-only httpHandler
/// constructor parameter) so the download → hash → compare path runs with no network I/O.
///
/// Deliberately does NOT exercise the checksum-*matching* success path: LarisVMS.Tests has a
/// ProjectReference to LarisVMS.Proxy, so a real LarisVMS.Proxy.exe apphost is copied into this test
/// project's output — TryApplyAsync's success path (ApplyWindows) would find LarisVMS.NodeUpdater.exe
/// next to it and actually launch it against this test process, polling a "LarisVMSProxy" Windows
/// Service that doesn't exist for a full 60s. The mismatch/failure paths below all return before
/// ApplyWindows is ever reached. Same reasoning as UpdateServiceTests' own doc comment.</summary>
public class ProxyUpdateServiceTests
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

    private static ProxyConfig NewConfig() =>
        new("https://larisvms.example.test", Guid.NewGuid(), "test-secret");

    private static ProxyUpdateInfoDto Update(string sha256, long sizeBytes) =>
        new("9.9.9", "https://larisvms.example.test/api/proxies/download/00000000-0000-0000-0000-000000000000",
            sha256, sizeBytes);

    [Fact]
    public async Task ChecksumMismatchAbortsWithoutApplyingOrStoppingTheHost()
    {
        var content = "fake LarisVMS.Proxy.exe bytes"u8.ToArray();
        var lifetime = new FakeLifetime();
        var service = new ProxyUpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, lifetime,
            new StubHandler(content));

        var applied = await service.TryApplyAsync(Update(new string('0', 64), content.Length), CancellationToken.None);

        Assert.False(applied);
        Assert.False(lifetime.StopApplicationCalled);
        // Reset, not left stuck true — a later check-in against a corrected upload must be able to retry.
        Assert.False(service.IsApplying);
    }

    [Fact]
    public async Task FailedDownloadAbortsCleanlyAndResetsIsApplying()
    {
        var lifetime = new FakeLifetime();
        var service = new ProxyUpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, lifetime,
            new StubHandler([], HttpStatusCode.InternalServerError));

        var applied = await service.TryApplyAsync(Update(new string('0', 64), 0), CancellationToken.None);

        Assert.False(applied);
        Assert.False(lifetime.StopApplicationCalled);
        Assert.False(service.IsApplying);
    }

    [Fact]
    public async Task ReTriggeringWhileAlreadyApplyingIsIgnored()
    {
        var service = new ProxyUpdateService(NewConfig(), insecureTls: false, NullLogger.Instance, new FakeLifetime(),
            new StubHandler("fake"u8.ToArray()));

        var first = service.TryApplyAsync(Update(new string('0', 64), 4), CancellationToken.None);
        // IsApplying is set synchronously before the first await inside TryApplyAsync.
        var second = await service.TryApplyAsync(Update(new string('0', 64), 4), CancellationToken.None);

        Assert.False(second);
        await first;
    }
}

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Footage encryption at rest: the server's keys (master key, backup, restore, data keys),
/// node config gating, and the node's FootageStore against a real local web host.</summary>
public class FootageEncryptionTests
{
    private static ApplicationDbContext NewDb(string? name = null) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    // ── Server keys ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DataKeysNeedAMasterKeyAndABackup()
    {
        using var db = NewDb();
        var keys = new FootageKeyService(db);
        var camera = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 14);

        Assert.Null(await keys.GetOrCreateDataKeyAsync(camera, day));
        await keys.CreateMasterKeyAsync();
        Assert.Null(await keys.GetOrCreateDataKeyAsync(camera, day)); // no backup yet
        Assert.False((await keys.GetStatusAsync()).Ready);

        Assert.NotNull(await keys.ExportBackupAsync("correct horse battery"));
        Assert.True((await keys.GetStatusAsync()).Ready);

        var first = await keys.GetOrCreateDataKeyAsync(camera, day);
        var again = await keys.GetOrCreateDataKeyAsync(camera, day);
        var otherDay = await keys.GetOrCreateDataKeyAsync(camera, day.AddDays(1));
        Assert.Equal(first!.Value.KeyId, again!.Value.KeyId);
        Assert.Equal(first.Value.Key, again.Value.Key);
        Assert.NotEqual(first.Value.KeyId, otherDay!.Value.KeyId);

        var byId = await keys.GetDataKeyAsync(first.Value.KeyId);
        Assert.Equal(camera, byId!.Value.CameraId);
        Assert.Equal(first.Value.Key, byId.Value.Key);
        Assert.Throws<ArgumentException>(() => keys.ExportBackupAsync("short").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task ABackupRestoresOntoARebuiltServerAndUnlocksTheOldDataKeys()
    {
        // The original server: master key, backup, and a data key wrapped by it.
        var dbName = Guid.NewGuid().ToString();
        string backup;
        (Guid KeyId, byte[] Key) dataKey;
        using (var db = NewDb(dbName))
        {
            var keys = new FootageKeyService(db);
            await keys.CreateMasterKeyAsync();
            backup = (await keys.ExportBackupAsync("correct horse battery"))!;
            dataKey = (await keys.GetOrCreateDataKeyAsync(Guid.NewGuid(), new DateOnly(2026, 10, 14)))!.Value;
        }

        // "Rebuild": the data keys survive in the database, the master key doesn't.
        using (var db = NewDb(dbName))
        {
            db.FootageMasterKeys.RemoveRange(db.FootageMasterKeys);
            await db.SaveChangesAsync();
            var keys = new FootageKeyService(db);
            Assert.Null(await keys.GetDataKeyAsync(dataKey.KeyId));

            Assert.Equal(FootageKeyRestoreResult.WrongPassphrase, await keys.RestoreBackupAsync(backup, "wrong passphrase!!"));
            Assert.Equal(FootageKeyRestoreResult.NotABackup, await keys.RestoreBackupAsync("{\"x\":1}", "correct horse battery"));
            Assert.Equal(FootageKeyRestoreResult.Restored, await keys.RestoreBackupAsync(backup, "correct horse battery"));
            Assert.Equal(dataKey.Key, (await keys.GetDataKeyAsync(dataKey.KeyId))!.Value.Key);
            Assert.True((await keys.GetStatusAsync()).Ready);
            Assert.Equal(FootageKeyRestoreResult.AlreadyInstalled, await keys.RestoreBackupAsync(backup, "correct horse battery"));
        }

        // A server with a different master key refuses it rather than orphaning its own footage.
        using (var other = NewDb())
        {
            var keys = new FootageKeyService(other);
            await keys.CreateMasterKeyAsync();
            Assert.Equal(FootageKeyRestoreResult.DifferentKeyInstalled, await keys.RestoreBackupAsync(backup, "correct horse battery"));
        }
    }

    [Fact]
    public async Task NodeConfigOnlyCarriesEncryptionOnceTheBackupExists()
    {
        using var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k", StorageRootPath = @"D:\Video" };
        var camera = new Camera { Id = Guid.NewGuid(), Name = "c", Host = "h", DeviceServiceUri = "http://h/onvif/device_service", NodeId = node.Id };
        db.AddRange(node, camera);
        await db.SaveChangesAsync();
        var settings = new SettingsResolver(db);
        await settings.SetGlobalAsync("Storage.Encrypt", "Video", "test");
        var service = new NodeService(db, settings);

        Assert.Equal("Off", (await service.GetConfigAsync(node.Id)).Cameras.Single().EncryptFootage);

        var keys = new FootageKeyService(db);
        await keys.CreateMasterKeyAsync();
        await keys.ExportBackupAsync("correct horse battery");
        await settings.SetOverrideAsync(LarisVMS.Core.Enums.SettingScope.Camera, camera.Id, "Storage.Encrypt", "VideoAndImages", "test");
        Assert.Equal("VideoAndImages", (await service.GetConfigAsync(node.Id)).Cameras.Single().EncryptFootage);
        Assert.True(await service.NodeMayHoldCameraAsync(node.Id, camera.Id));
        Assert.False(await service.NodeMayHoldCameraAsync(Guid.NewGuid(), camera.Id));
    }

    // ── Node FootageStore against a local web host ──────────────────────────

    /// <summary>A real HTTP host with the node-facing key API (backed by FootageKeyService) and the
    /// node's /footage-plain loopback endpoint, so FootageStore runs exactly as on a node.</summary>
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required FootageStore Store { get; init; }
        public required Guid CameraId { get; init; }
        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    private static async Task<Host> StartHostAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = NewDb(dbName))
        {
            var keys = new FootageKeyService(seed);
            await keys.CreateMasterKeyAsync();
            await keys.ExportBackupAsync("correct horse battery");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FootageStore? store = null;
        app.MapPost("/api/nodes/footage-keys", async (FootageKeyRequest request) =>
        {
            using var db = NewDb(dbName);
            var key = await new FootageKeyService(db).GetOrCreateDataKeyAsync(request.CameraId, request.Day);
            return key is { } k ? Results.Json(new FootageKeyResponse(k.KeyId, request.CameraId, Convert.ToBase64String(k.Key))) : Results.Conflict();
        });
        app.MapGet("/api/nodes/footage-keys/{id:guid}", async (Guid id) =>
        {
            using var db = NewDb(dbName);
            var key = await new FootageKeyService(db).GetDataKeyAsync(id);
            return key is { } k ? Results.Json(new FootageKeyResponse(id, k.CameraId, Convert.ToBase64String(k.Key))) : Results.NotFound();
        });
        app.MapGet("/footage-plain/{token}", async (HttpContext ctx, string token) =>
        {
            if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None)) return Results.StatusCode(403);
            var path = store!.ResolveLoopback(token);
            if (path is null) return Results.NotFound();
            return Results.Stream(await store.OpenReadAsync(path, ctx.RequestAborted), "video/mp4", enableRangeProcessing: true);
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        store = new FootageStore(new NodeApiClient($"http://127.0.0.1:{port}", acceptAnyCertificate: false), port, NullLogger<FootageStore>.Instance);
        return new Host { App = app, Store = store, CameraId = Guid.NewGuid() };
    }

    [Fact]
    public async Task EncryptsInPlaceAndReadsBackTransparently()
    {
        await using var host = await StartHostAsync();
        var dir = Directory.CreateTempSubdirectory("laris-enc-");
        try
        {
            var path = Path.Combine(dir.FullName, "seg.mp4");
            var plain = RandomNumberGenerator.GetBytes(200_000);
            await File.WriteAllBytesAsync(path, plain);
            var stamp = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);
            var plainHash = await host.Store.HashAsync(path, default);

            Assert.Equal(path, await host.Store.FfmpegInputAsync(path, default)); // plain: the path itself
            Assert.True(await host.Store.EncryptFileAsync(path, host.CameraId, stamp, default));
            Assert.True(FootageCrypto.IsEncrypted(path));
            Assert.NotEqual(plain, await File.ReadAllBytesAsync(path));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path)); // retention reads this
            Assert.False(File.Exists(path + ".enc-tmp"));
            Assert.True(await host.Store.EncryptFileAsync(path, host.CameraId, stamp, default)); // already done

            // Same hash, same bytes, through the store.
            Assert.Equal(plainHash, await host.Store.HashAsync(path, default));
            Assert.Equal(plain, await host.Store.ReadAllBytesAsync(path, default));

            // ffmpeg's loopback view, with a range request in the middle of a chunk boundary.
            var url = await host.Store.FfmpegInputAsync(path, default);
            Assert.StartsWith("http://127.0.0.1:", url);
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(65_530, 65_545);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(plain.AsSpan(65_530, 16).ToArray(), await response.Content.ReadAsByteArrayAsync());

            // A cached image written for an encrypting camera is sealed, and reads back.
            var thumb = Path.Combine(dir.FullName, "thumb.webp");
            await host.Store.WriteImageAsync(thumb, [1, 2, 3, 4], host.CameraId, default);
            Assert.True(FootageCrypto.IsEncrypted(thumb));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await host.Store.ReadAllBytesAsync(thumb, default));
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>Real ffmpeg reading encrypted footage through the loopback endpoint: a thumbnail with
    /// a seek, and an export-style concat of two encrypted segments. Opt-in (needs ffmpeg on PATH):
    /// set LARIS_FFMPEG_E2E=1.</summary>
    [Fact]
    public async Task FfmpegReadsEncryptedFootageThroughTheLoopback()
    {
        if (Environment.GetEnvironmentVariable("LARIS_FFMPEG_E2E") != "1") return;
        await using var host = await StartHostAsync();
        var dir = Directory.CreateTempSubdirectory("laris-enc-ff-");
        try
        {
            var segments = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                var seg = Path.Combine(dir.FullName, $"seg{i}.mp4");
                await RunFfmpegAsync("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15", "-t", "6", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                    "-g", "15", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", seg);
                Assert.True(await host.Store.EncryptFileAsync(seg, host.CameraId, DateTime.UtcNow, default));
                segments.Add(seg);
            }

            var input = await host.Store.FfmpegInputAsync(segments[0], default);
            var thumb = await LarisVMS.Media.ThumbnailCapture.CaptureAsync("ffmpeg", input, 4, default);
            Assert.NotNull(thumb);
            Assert.True(thumb!.Length > 1000);

            var list = Path.Combine(dir.FullName, "list.txt");
            var inputs = new List<string>();
            foreach (var s in segments) inputs.Add(await host.Store.FfmpegInputAsync(s, default));
            await File.WriteAllTextAsync(list, ExportRunner.BuildConcatList(inputs));
            var output = Path.Combine(dir.FullName, "export.mp4");
            var args = ExportRunner.BuildArgs(list, output, [], segmentsHaveAudio: false, loopbackInputs: true);
            await RunFfmpegAsync(["-y", .. args.Where(a => a != "-y")]);
            Assert.True(new FileInfo(output).Length > 10_000);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static async Task RunFfmpegAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, "ffmpeg failed: " + err);
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Node;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>The standalone export player: the streaming zip, the package's contents and signed list,
/// the H.264 copy, and the export flags.</summary>
public class ExportPlayerTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string Hash2 = "0000000000000000000000000000000000000000000000000000000000000002";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class NoColors : IEventColorService
    {
        public Task<EventPalette> GetAsync(CancellationToken ct = default) => Task.FromResult(EventPalette.Empty);
        public Task SaveAsync(EventPalette palette, string? modifiedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── Streaming zip ───────────────────────────────────────────────────

    private static async Task<byte[]> ZipAsync(params (string Name, string Text)[] entries)
    {
        using var output = new MemoryStream();
        var zip = new StreamingZipWriter(output);
        foreach (var (name, text) in entries) await zip.AddAsync(name, Encoding.UTF8.GetBytes(text), new DateTime(2026, 11, 15, 10, 30, 0));
        await zip.FinishAsync();
        return output.ToArray();
    }

    [Fact]
    public async Task TheZipReadsBackWithDotNetsOwnReader()
    {
        var bytes = await ZipAsync(("pkg/a.txt", "hello"), ("pkg/video/Front door/x.mp4", new string('v', 100_000)), ("pkg/日本語.txt", "名前"));

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(["pkg/a.txt", "pkg/video/Front door/x.mp4", "pkg/日本語.txt"], archive.Entries.Select(e => e.FullName).ToArray());
        using (var reader = new StreamReader(archive.GetEntry("pkg/日本語.txt")!.Open())) Assert.Equal("名前", await reader.ReadToEndAsync());
        using (var reader = new StreamReader(archive.GetEntry("pkg/video/Front door/x.mp4")!.Open())) Assert.Equal(100_000, (await reader.ReadToEndAsync()).Length);
        Assert.Equal(new DateTime(2026, 11, 15, 10, 30, 0), archive.GetEntry("pkg/a.txt")!.LastWriteTime.DateTime);
    }

    [Fact]
    public async Task ManyEntriesUseZip64RecordsAndStillRead()
    {
        using var output = new MemoryStream();
        var zip = new StreamingZipWriter(output);
        const int count = ushort.MaxValue + 5;
        for (var i = 0; i < count; i++) await zip.AddAsync($"f/{i}", "x"u8.ToArray(), DateTime.Now);
        await zip.FinishAsync();

        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(count, archive.Entries.Count);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("/abs")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("")]
    public async Task UnsafeEntryNamesAreRefused(string name)
    {
        var zip = new StreamingZipWriter(new MemoryStream());
        await Assert.ThrowsAsync<ArgumentException>(() => zip.AddAsync(name, "x"u8.ToArray(), DateTime.Now));
    }

    [Fact]
    public async Task ADuplicateEntryIsRefused()
    {
        var zip = new StreamingZipWriter(new MemoryStream());
        await zip.AddAsync("a", "x"u8.ToArray(), DateTime.Now);
        await Assert.ThrowsAsync<ArgumentException>(() => zip.AddAsync("a", "y"u8.ToArray(), DateTime.Now));
    }

    [Fact]
    public void Crc32MatchesTheStandardCheckValue()
    {
        var crc = StreamingZipWriter.Crc32.Update(StreamingZipWriter.Crc32.Initial, "123456789"u8);
        Assert.Equal(0xCBF43926u, StreamingZipWriter.Crc32.Finish(crc));
    }

    // ── Folder names ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Front door", "Front door")]
    [InlineData("Gate: north/south?", "Gate_ north_south_")]
    [InlineData("  trailing dots... ", "trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.cam", "_com1.cam")]
    [InlineData("...", "camera")]
    public void CameraNamesBecomeSafeFolderNames(string name, string expected) =>
        Assert.Equal(expected, ExportPackageBuilder.SafeName(name));

    [Fact]
    public void CamerasWithTheSameNameGetTheirOwnFolders()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var folders = ExportPackageBuilder.CameraFolders([(a, "Lobby"), (b, "lobby"), (c, "Lobby")]);
        Assert.Equal("Lobby", folders[a]);
        Assert.Equal("lobby (2)", folders[b]);
        Assert.Equal("Lobby (3)", folders[c]);
    }

    [Fact]
    public void DataAsAScriptCantEndTheScriptEarly()
    {
        var script = ExportPackageBuilder.ScriptOf("LARIS_EXPORT", "{\"note\":\"</script><b>\"}");
        Assert.DoesNotContain("</script>", script);
        Assert.StartsWith("window.LARIS_EXPORT = ", script);
    }

    // ── The package ─────────────────────────────────────────────────────

    private static (ApplicationDbContext Db, Guid JobId, Guid CameraId, Guid ItemId) SeedFinishedJob(bool converted)
    {
        var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "NVR 1", ApiKeyHash = "h", LastIpAddress = "10.0.0.5", LivePort = 8554, MediaSigningKey = "k" };
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Front: door", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif", NodeId = node.Id };
        var from = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        var job = new ExportJob
        {
            Id = Guid.NewGuid(), RequestedByUserId = "u1", RequestedByUserName = "alice", CreatedUtc = from.AddHours(1),
            FromUtc = from, ToUtc = from.AddMinutes(10), Status = ExportJobStatus.Done, IncludePlayer = true, ConvertH265 = true,
        };
        var item = new ExportJobItem
        {
            Id = Guid.NewGuid(), ExportJobId = job.Id, CameraId = camera.Id, NodeId = node.Id, Status = ExportItemStatus.Done,
            OutputFilePath = @"D:\rec\exports\Frontdoor_NVR1.mp4", OutputSizeBytes = 1000, OutputSha256 = Hash,
            ConvertedFilePath = converted ? @"D:\rec\exports\Frontdoor_NVR1.h264.mp4" : null,
            ConvertedSizeBytes = converted ? 900 : null, ConvertedSha256 = converted ? Hash2 : null,
            StartedUtc = from.AddHours(1), CompletedUtc = from.AddHours(1).AddMinutes(1),
        };
        db.AddRange(node, camera, job, item);
        db.Segments.AddRange(
            new Segment { CameraId = camera.Id, NodeId = node.Id, StartUtc = from, EndUtc = from.AddMinutes(1), FilePath = "a", Codec = "hevc" },
            new Segment { CameraId = camera.Id, NodeId = node.Id, StartUtc = from.AddMinutes(5), EndUtc = from.AddMinutes(6), FilePath = "b", Codec = "hevc" },
            // Outside the range: not listed.
            new Segment { CameraId = camera.Id, NodeId = node.Id, StartUtc = from.AddHours(2), EndUtc = from.AddHours(2).AddMinutes(1), FilePath = "c" });
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = camera.Id, StartUtc = from.AddSeconds(10), EndUtc = from.AddSeconds(20), DetectionKind = DetectionKind.Human,
                BestFrameAtUtc = from.AddSeconds(12), BestBoxX = 0.1, BestBoxY = 0.2, BestBoxW = 0.3, BestBoxH = 0.4 },
            new MotionSpan { CameraId = camera.Id, StartUtc = from.AddHours(3), EndUtc = from.AddHours(3).AddSeconds(5) });
        db.Bookmarks.AddRange(
            new Bookmark { Id = Guid.NewGuid(), CameraId = camera.Id, TimestampUtc = from.AddMinutes(2), Note = "Car arrives", CreatedByUserId = "u1", CreatedByUserName = "alice" },
            new Bookmark { Id = Guid.NewGuid(), CameraId = camera.Id, TimestampUtc = from.AddDays(1), Note = "Later", CreatedByUserId = "u1" });
        db.ExportManifests.Add(new ExportManifest { ExportJobItemId = item.Id, Json = "{\"format\":\"x\"}" });
        db.SaveChanges();
        return (db, job.Id, camera.Id, item.Id);
    }

    [Fact]
    public async Task ThePackageListsEachVideoTheEventsInRangeAndTheLog()
    {
        var (db, jobId, cameraId, _) = SeedFinishedJob(converted: true);
        var plan = await new ExportPackageBuilder(db, new SigningKeyService(db), new NoColors()).PlanAsync(jobId, "bob");

        Assert.NotNull(plan);
        Assert.Equal(["video/Front_ door/Frontdoor_NVR1.mp4", "video/Front_ door/Frontdoor_NVR1.h264.mp4"], plan.Videos.Select(v => v.ZipPath).ToArray());
        Assert.Contains(plan.Files, f => f.ZipPath == "manifests/Frontdoor_NVR1.manifest.json");
        Assert.Contains(plan.Files, f => f.ZipPath == "data/export.js");

        var data = JsonDocument.Parse(plan.Files.Single(f => f.ZipPath == "data/export.json").Content).RootElement;
        Assert.Equal(ExportPackageBuilder.DataFormat, data.GetProperty("format").GetString());
        var file = data.GetProperty("cameras")[0].GetProperty("files")[0];
        Assert.Equal("hevc", file.GetProperty("codec").GetString());
        Assert.Equal("video/Front_ door/Frontdoor_NVR1.h264.mp4", file.GetProperty("h264Path").GetString());
        Assert.Equal(2, file.GetProperty("segments").GetArrayLength());
        var events = data.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal(cameraId, events[0].GetProperty("cameraId").GetGuid());
        Assert.Equal(4, events[0].GetProperty("box").GetArrayLength());
        Assert.Equal(1, data.GetProperty("bookmarks").GetArrayLength());
        var log = data.GetProperty("log").EnumerateArray().Select(l => l.GetProperty("action").GetString()!).ToArray();
        Assert.Equal(["requested", "exported", "packaged"], log);
    }

    [Fact]
    public async Task ThePackageListIsSignedAndCoversEveryFile()
    {
        var (db, jobId, _, _) = SeedFinishedJob(converted: false);
        var signing = new SigningKeyService(db);
        var builder = new ExportPackageBuilder(db, signing, new NoColors());
        var plan = (await builder.PlanAsync(jobId, "bob"))!;

        var files = plan.Files.Select(f => (f.ZipPath, f.Content.LongLength, ExportPackageBuilder.Sha256(f.Content)))
            .Concat(plan.Videos.Select(v => (v.ZipPath, v.SizeBytes, v.Sha256))).ToList();
        var manifest = JsonDocument.Parse(await builder.SignManifestAsync(plan, files, "bob")).RootElement;
        var payloadText = manifest.GetProperty("signedPayload").GetString()!;

        Assert.True(SigningKeyService.Verify(manifest.GetProperty("publicKeyPem").GetString()!, payloadText, manifest.GetProperty("signature").GetString()!));
        Assert.False(SigningKeyService.Verify(manifest.GetProperty("publicKeyPem").GetString()!, payloadText.Replace(Hash, Hash2), manifest.GetProperty("signature").GetString()!));
        var listed = JsonDocument.Parse(payloadText).RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.Equal(files.Count, listed.Count);
        Assert.Contains("video/Front_ door/Frontdoor_NVR1.mp4", listed);
    }

    [Fact]
    public async Task AnUnknownJobHasNoPackage()
    {
        using var db = NewDb();
        Assert.Null(await new ExportPackageBuilder(db, new SigningKeyService(db), new NoColors()).PlanAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public void ThePlayersFilesAreInWwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LarisVMS.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        foreach (var (_, webPath) in ExportPackageStreamer.PlayerFiles)
            Assert.True(File.Exists(Path.Combine(dir.FullName, "src", "LarisVMS.Web", "wwwroot", webPath)), webPath);
    }

    // ── Export flags and the H.264 copy ─────────────────────────────────

    [Fact]
    public async Task ThePlayerIsOffUnlessAskedForAndConversionOnlyGoesWithIt()
    {
        using var db = NewDb();
        var service = new ExportService(db);
        var plain = await service.CreateJobAsync([Guid.NewGuid()], DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, "u", "u");
        var player = await service.CreateJobAsync([Guid.NewGuid()], DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, "u", "u", includePlayer: true);
        var noConvert = await service.CreateJobAsync([Guid.NewGuid()], DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, "u", "u", includePlayer: true, convertH265: false);

        Assert.False(plain.IncludePlayer);
        Assert.False(plain.ConvertH265);
        Assert.True(player.IncludePlayer);
        Assert.True(player.ConvertH265);
        Assert.False(noConvert.ConvertH265);
        Assert.False(new CreateExportRequest([], DateTime.UtcNow, DateTime.UtcNow).IncludePlayer);
    }

    [Fact]
    public async Task TheH264CopyIsRecordedDeletedAndClearedOnRetry()
    {
        var (db, jobId, _, itemId) = SeedFinishedJob(converted: false);
        var item = db.ExportJobItems.Single(i => i.Id == itemId);
        item.Status = ExportItemStatus.Running;
        db.SaveChanges();
        var service = new ExportService(db);

        await service.ApplyCompletionReportAsync(item.NodeId!.Value, [new ExportCompleteReportItem(itemId, true, @"D:\rec\exports\x.mp4", 10, null, Hash, null,
            @"D:\rec\exports\x.h264.mp4", 9, Hash2.ToUpperInvariant())]);
        item = db.ExportJobItems.AsNoTracking().Single(i => i.Id == itemId);
        Assert.Equal(@"D:\rec\exports\x.h264.mp4", item.ConvertedFilePath);
        Assert.Equal(Hash2, item.ConvertedSha256);

        var deletion = await service.GetDeletionInfoAsync(jobId);
        Assert.Equal([@"D:\rec\exports\x.mp4", @"D:\rec\exports\x.h264.mp4"], deletion!.Files.Select(f => f.FilePath).ToArray());

        var tracked = db.ExportJobItems.Single(i => i.Id == itemId);
        tracked.Status = ExportItemStatus.Failed;
        db.SaveChanges();
        Assert.True(await service.RetryItemAsync(itemId));
        Assert.Null(db.ExportJobItems.AsNoTracking().Single(i => i.Id == itemId).ConvertedFilePath);
    }

    [Fact]
    public async Task ACopyWithoutAValidHashIsntKept()
    {
        var (db, _, _, itemId) = SeedFinishedJob(converted: false);
        var item = db.ExportJobItems.Single(i => i.Id == itemId);
        await new ExportService(db).ApplyCompletionReportAsync(item.NodeId!.Value, [new ExportCompleteReportItem(itemId, true, @"D:\x.mp4", 10, null, Hash, null,
            @"D:\x.h264.mp4", 9, "not-a-hash")]);
        Assert.Null(db.ExportJobItems.AsNoTracking().Single(i => i.Id == itemId).ConvertedFilePath);
    }

    [Theory]
    [InlineData("hevc", true)]
    [InlineData("H.265", true)]
    [InlineData("hvc1.1.6.L93.B0", true)]
    [InlineData("h264", false)]
    [InlineData("avc1", false)]
    [InlineData(null, false)]
    public void H265IsRecognised(string? codec, bool expected) => Assert.Equal(expected, ExportJobDispatcher.IsH265(codec));

    [Fact]
    public void TheCopySitsBesideTheExportAndIsReEncodedToH264()
    {
        Assert.Equal(Path.Combine(TestPaths.Native(@"D:\rec\exports"), "cam_x.h264.mp4"), ExportRunner.ConvertedPath(TestPaths.Native(@"D:\rec\exports\cam_x.mp4")));
        var args = ExportRunner.BuildConvertArgs("in.mp4", "out.h264.mp4", null);
        Assert.Contains("libx264", args);
        Assert.Contains("+faststart", args);
        Assert.Equal("out.h264.mp4", args[^1]);
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
    }

    [Theory]
    [InlineData("/api/exports/00000000-0000-0000-0000-000000000001/package", true)]
    [InlineData("/api/v1/exports/00000000-0000-0000-0000-000000000001/package", true)]
    [InlineData("/api/v1/cameras/00000000-0000-0000-0000-000000000001/stream.mp4", true)]
    [InlineData("/api/v1/exports", false)]
    public void PackagesAndStreamsFollowTheMediaAllowList(string path, bool isMedia) =>
        Assert.Equal(isMedia, MediaRoutes.IsMediaPath(new PathString(path)));
}

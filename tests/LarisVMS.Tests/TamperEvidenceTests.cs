using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Tamper evidence: segment hash chains, the signing key, checkpoints and signed export
/// manifests.</summary>
public class TamperEvidenceTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 18, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    // ── Chain formula ───────────────────────────────────────────────────────

    [Fact]
    public void ChainLinkMatchesItsDocumentedByteLayout()
    {
        var camera = Guid.Parse("2e1975e5-d623-4bf7-a4d4-83d7c7096162");
        var start = T0.AddTicks(1234567);
        var end = start.AddSeconds(30);
        var content = Sha("segment");
        var prev = Sha("previous");

        var expected = new List<byte>();
        expected.AddRange(Convert.FromHexString(prev));
        expected.AddRange(Convert.FromHexString(content));
        expected.AddRange(camera.ToByteArray());
        expected.AddRange(BitConverter.GetBytes(start.Ticks));
        expected.AddRange(BitConverter.GetBytes(end.Ticks));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected.ToArray())), SegmentChain.Link(prev, content, camera, start, end));
        // The first link uses 32 zero bytes for "previous".
        Assert.Equal(SegmentChain.Link(new string('0', 64), content, camera, start, end), SegmentChain.Link(null, content, camera, start, end));
        Assert.NotEqual(SegmentChain.Link(prev, content, camera, start, end), SegmentChain.Link(prev, content, camera, start, end.AddTicks(1)));
    }

    [Fact]
    public async Task HashesAFileAsLowercaseHex()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "hello");
            Assert.Equal(Sha("hello"), await SegmentChain.HashFileAsync(path));
        }
        finally { File.Delete(path); }
    }

    // ── Chaining recorded segments ──────────────────────────────────────────

    private static async Task<(ApplicationDbContext Db, NodeService Service, Guid CameraId, Guid NodeId)> SeedAsync()
    {
        var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k" };
        var camera = new Camera { Id = Guid.NewGuid(), Name = "West Sideyard", Host = "h", DeviceServiceUri = "http://h/onvif/device_service", NodeId = node.Id };
        db.AddRange(node, camera);
        await db.SaveChangesAsync();
        return (db, new NodeService(db, new SettingsResolver(db)), camera.Id, node.Id);
    }

    private static SegmentReportItem Seg(Guid cameraId, int i, string? sha = "default") =>
        new(cameraId, "Main", T0.AddSeconds(30 * i), T0.AddSeconds(30 * (i + 1)), $@"D:\Video\cam\{i}.mp4", 1000, "hevc", 4096, 1856, true,
            sha == "default" ? Sha($"content {i}") : sha);

    [Fact]
    public async Task SegmentsAreChainedAcrossBatchesInTheOrderTheyArrive()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        await service.RecordSegmentsAsync(nodeId, [Seg(cameraId, 0), Seg(cameraId, 1)]);
        // (A re-sent duplicate is dropped by SQL Server's unique FilePath index before chaining; the
        // in-memory provider doesn't enforce that index, so it isn't exercised here.)
        await service.RecordSegmentsAsync(nodeId, [Seg(cameraId, 2), Seg(cameraId, 3, sha: "not a hash"), Seg(cameraId, 4, sha: null)]);

        var segments = await db.Segments.OrderBy(s => s.StartUtc).ToListAsync();
        Assert.Equal(5, segments.Count);
        Assert.Null(segments[0].PrevChainHash);
        for (var i = 0; i < 3; i++)
        {
            var s = segments[i];
            Assert.Equal(SegmentChain.Link(s.PrevChainHash, s.ContentSha256!, cameraId, s.StartUtc, s.EndUtc), s.ChainHash);
            if (i > 0) Assert.Equal(segments[i - 1].ChainHash, s.PrevChainHash);
        }
        Assert.Null(segments[3].ChainHash); // bad hash
        Assert.Null(segments[3].ContentSha256);
        Assert.Null(segments[4].ChainHash); // no hash

        var head = await db.SegmentChainHeads.SingleAsync();
        Assert.Equal(segments[2].ChainHash, head.ChainHash);
        Assert.Equal(segments[2].Id, head.SegmentId);
    }

    // ── Signing and checkpoints ─────────────────────────────────────────────

    [Fact]
    public async Task SignsWithOneKeyAndDetectsAChangedPayload()
    {
        using var db = NewDb();
        var signing = new SigningKeyService(db);
        var first = await signing.SignAsync("hello");
        var second = await signing.SignAsync("hello again");

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Single(await db.SigningKeys.ToListAsync());
        Assert.True(await signing.VerifyAsync(first.KeyId, "hello", first.Signature));
        Assert.False(await signing.VerifyAsync(first.KeyId, "hellO", first.Signature));
        Assert.True(SigningKeyService.Verify(first.PublicKeyPem, "hello", first.Signature));

        // The DER form verifies with any standard tool (here .NET's own DER reader).
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(first.PublicKeyPem);
        Assert.True(ecdsa.VerifyData("hello"u8, Convert.FromBase64String(first.SignatureDer), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public async Task CheckpointsOnlyWhenTheChainMovesAndTheirSignaturesVerify()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        await service.RecordSegmentsAsync(nodeId, [Seg(cameraId, 0), Seg(cameraId, 1)]);
        var signing = new SigningKeyService(db);
        var chain = new SegmentChainService(db, signing);

        var cp = await chain.CheckpointAsync(cameraId, onlyIfMoved: true);
        Assert.NotNull(cp);
        Assert.Equal(cp!.Id, (await chain.CheckpointAsync(cameraId, onlyIfMoved: true))!.Id); // nothing new
        await service.RecordSegmentsAsync(nodeId, [Seg(cameraId, 2)]);
        await chain.CheckpointAllAsync();
        Assert.Equal(2, await db.ChainCheckpoints.CountAsync());

        var latest = await db.ChainCheckpoints.OrderByDescending(c => c.Id).FirstAsync();
        var payload = SegmentChain.CheckpointPayload(cameraId, latest.SegmentId, latest.ChainHash, latest.SegmentEndUtc, latest.SignedUtc);
        Assert.True(await signing.VerifyAsync(latest.KeyId, payload, latest.Signature));
    }

    // ── Export manifests ────────────────────────────────────────────────────

    private sealed record ManifestFile(string format, string signedPayload, string signature, string signatureDer, Guid keyId, string algorithm, string publicKeyPem);

    [Fact]
    public async Task ExportManifestIsSignedAndFollowsTheChainToACheckpoint()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        await service.RecordSegmentsAsync(nodeId, Enumerable.Range(0, 6).Select(i => Seg(cameraId, i)).ToList());
        var signing = new SigningKeyService(db);
        await new SegmentChainService(db, signing).CheckpointAsync(cameraId, onlyIfMoved: false); // covers all 6

        var job = new ExportJob { Id = Guid.NewGuid(), FromUtc = T0.AddSeconds(30), ToUtc = T0.AddSeconds(90), Status = ExportJobStatus.Running };
        var item = new ExportJobItem { Id = Guid.NewGuid(), ExportJobId = job.Id, CameraId = cameraId, NodeId = nodeId, Status = ExportItemStatus.Running };
        db.AddRange(job, item);
        await db.SaveChangesAsync();

        var outputSha = Sha("exported video");
        var report = new ExportCompleteReportItem(item.Id, true, @"D:\Video\exports\clip.mp4", "exported video".Length, null, outputSha,
        [
            new(@"D:\Video\cam\1.mp4", Sha("content 1"), true),
            new(@"D:\Video\cam\2.mp4", Sha("tampered"), false),
        ]);
        var exportService = new ExportService(db, new ExportManifestBuilder(db, signing));
        await exportService.ApplyCompletionReportAsync(nodeId, [report]);

        var stored = await db.ExportJobItems.SingleAsync();
        Assert.Equal(outputSha, stored.OutputSha256);
        Assert.NotNull(stored.ManifestSignedUtc);
        var json = await exportService.GetManifestAsync(item.Id);
        Assert.NotNull(json);

        var manifest = JsonSerializer.Deserialize<ManifestFile>(json!)!;
        Assert.Equal(ExportManifestBuilder.Format, manifest.format);
        Assert.True(SigningKeyService.Verify(manifest.publicKeyPem, manifest.signedPayload, manifest.signature));
        Assert.False(SigningKeyService.Verify(manifest.publicKeyPem, manifest.signedPayload.Replace("clip.mp4", "clip2.mp4"), manifest.signature));

        using var payload = JsonDocument.Parse(manifest.signedPayload);
        var root = payload.RootElement;
        Assert.Equal(outputSha, root.GetProperty("file").GetProperty("sha256").GetString());
        var segments = root.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(2, segments.Count);
        Assert.True(segments[0].GetProperty("unchangedAtExport").GetBoolean());
        Assert.False(segments[1].GetProperty("unchangedAtExport").GetBoolean());

        // Segments 3–5 lead from the last exported one to the checkpoint.
        var links = root.GetProperty("chainToCheckpoint").EnumerateArray().ToList();
        Assert.Equal(3, links.Count);
        Assert.Equal(segments[1].GetProperty("chainHash").GetString(), links[0].GetProperty("prevChainHash").GetString());
        var checkpoint = root.GetProperty("checkpoint");
        Assert.Equal(links[^1].GetProperty("chainHash").GetString(), checkpoint.GetProperty("chainHash").GetString());

        // Each link recomputes from the manifest's own text (the "o" times are exact to the tick).
        foreach (var link in segments.Concat(links))
        {
            Assert.Equal(link.GetProperty("chainHash").GetString(), SegmentChain.Link(
                link.GetProperty("prevChainHash").GetString(), link.GetProperty("sha256").GetString()!, cameraId,
                DateTime.Parse(link.GetProperty("startUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(link.GetProperty("endUtc").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        var cpText = $"LarisVMS-chain-v1|{cameraId}|{checkpoint.GetProperty("segmentId").GetInt64()}|{checkpoint.GetProperty("chainHash").GetString()}" +
                     $"|{checkpoint.GetProperty("segmentEndUtc").GetString()}|{checkpoint.GetProperty("signedUtc").GetString()}";
        Assert.True(SigningKeyService.Verify(checkpoint.GetProperty("publicKeyPem").GetString()!, cpText, checkpoint.GetProperty("signature").GetString()!));

        // For the Verify page's browser check: drop a real manifest where the harness can find it.
        if (Environment.GetEnvironmentVariable("LARIS_MANIFEST_OUT") is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, "export.manifest.json"), json);
            await File.WriteAllTextAsync(Path.Combine(outDir, "clip.mp4"), "exported video");
        }
    }

    [Fact]
    public async Task ARecentExportGetsAFreshCheckpoint()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        await service.RecordSegmentsAsync(nodeId, [Seg(cameraId, 0), Seg(cameraId, 1)]);
        var job = new ExportJob { Id = Guid.NewGuid(), FromUtc = T0, ToUtc = T0.AddSeconds(60), Status = ExportJobStatus.Running };
        var item = new ExportJobItem { Id = Guid.NewGuid(), ExportJobId = job.Id, CameraId = cameraId, NodeId = nodeId, Status = ExportItemStatus.Running };
        db.AddRange(job, item);
        await db.SaveChangesAsync();

        var json = await new ExportManifestBuilder(db, new SigningKeyService(db)).BuildAsync(item.Id,
            new ExportCompleteReportItem(item.Id, true, @"D:\x.mp4", 1, null, Sha("x"), []));

        Assert.Single(await db.ChainCheckpoints.ToListAsync());
        using var doc = JsonDocument.Parse(JsonDocument.Parse(json!).RootElement.GetProperty("signedPayload").GetString()!);
        Assert.Empty(doc.RootElement.GetProperty("chainToCheckpoint").EnumerateArray());
        Assert.Equal(2, doc.RootElement.GetProperty("segments").GetArrayLength());
    }
}

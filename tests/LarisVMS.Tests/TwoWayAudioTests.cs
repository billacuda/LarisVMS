using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Media.Talk;
using LarisVMS.Node;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>Two-way audio: G.711, RTSP digest auth, backchannel SDP, a full backchannel session
/// against a scripted fake camera, talk recordings, and their retention.</summary>
public class TwoWayAudioTests
{
    [Theory]
    [InlineData((short)0, 0xFF)]
    [InlineData((short)32767, 0x80)]
    [InlineData((short)-32768, 0x00)]
    public void MuLawMatchesG711(short pcm, int expected) => Assert.Equal((byte)expected, G711.EncodeMuLaw(pcm));

    [Theory]
    [InlineData((short)0, 0xD5)]
    [InlineData((short)32767, 0xAA)]
    [InlineData((short)-32768, 0x2A)]
    public void ALawMatchesG711(short pcm, int expected) => Assert.Equal((byte)expected, G711.EncodeALaw(pcm));

    [Fact]
    public void DigestResponseMatchesRfc2617Example()
    {
        var auth = RtspAuth.FromChallenges(
            ["Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""],
            "Mufasa", "Circle Of Life")!;

        var header = auth.Header("GET", "/dir/index.html", cnonce: "0a4f113b");

        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header);
        Assert.Contains("nc=00000001", header);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header);
    }

    [Fact]
    public void BasicIsUsedOnlyWhenNoDigestIsOffered()
    {
        Assert.StartsWith("Basic ", RtspAuth.FromChallenges(["Basic realm=\"cam\""], "a", "b")!.Header("DESCRIBE", "rtsp://x/"));
        Assert.StartsWith("Digest ", RtspAuth.FromChallenges(["Basic realm=\"cam\"", "Digest realm=\"cam\", nonce=\"n\""], "a", "b")!.Header("DESCRIBE", "rtsp://x/"));
    }

    private const string SampleSdp = """
        v=0
        o=- 0 0 IN IP4 10.0.0.5
        s=Media Presentation
        a=control:*
        m=video 0 RTP/AVP 96
        a=rtpmap:96 H264/90000
        a=control:trackID=1
        a=recvonly
        m=audio 0 RTP/AVP 0
        a=rtpmap:0 PCMU/8000
        a=control:trackID=2
        a=recvonly
        m=audio 0 RTP/AVP 8 0
        a=rtpmap:8 PCMA/8000
        a=rtpmap:0 PCMU/8000
        a=control:trackID=5
        a=sendonly
        """;

    [Fact]
    public void BackchannelIsTheSendOnlyAudioTrackPreferringMuLaw()
    {
        var track = BackchannelSdp.Parse(SampleSdp, new Uri("rtsp://10.0.0.5/stream1/"));
        Assert.Equal(new BackchannelTrack("rtsp://10.0.0.5/stream1/trackID=5", 0, false, 8000), track);
    }

    [Fact]
    public void NoSendOnlyAudioMeansNoBackchannel()
    {
        var sdp = SampleSdp[..SampleSdp.IndexOf("m=audio 0 RTP/AVP 8 0", StringComparison.Ordinal)];
        Assert.Null(BackchannelSdp.Parse(sdp, new Uri("rtsp://10.0.0.5/")));
    }

    [Fact]
    public void ControlUrisResolveAgainstTheBase()
    {
        var b = new Uri("rtsp://cam/live");
        Assert.Equal("rtsp://cam/live/trackID=5", BackchannelSdp.ResolveControl("trackID=5", b));
        Assert.Equal("rtsp://other/x", BackchannelSdp.ResolveControl("rtsp://other/x", b));
        Assert.Equal("rtsp://cam/live", BackchannelSdp.ResolveControl("*", b));
    }

    [Fact]
    public void SessionHeadersParse()
    {
        Assert.Equal(30, OnvifBackchannelClient.ParseSessionTimeout("12345678;timeout=30"));
        Assert.Null(OnvifBackchannelClient.ParseSessionTimeout("12345678"));
        Assert.Equal(2, OnvifBackchannelClient.ParseInterleavedChannel("RTP/AVP/TCP;unicast;interleaved=2-3"));
    }

    /// <summary>A scripted camera: challenges with digest, offers a PCMA backchannel, then collects
    /// whatever arrives after PLAY until the client sends TEARDOWN.</summary>
    [Fact]
    public async Task FullBackchannelSessionAgainstAFakeCamera()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sdp = SampleSdp.Replace("m=audio 0 RTP/AVP 8 0", "m=audio 0 RTP/AVP 8").Replace("a=rtpmap:0 PCMU/8000\na=control:trackID=5", "a=control:trackID=5");
        var requests = new List<string>();
        var rtpPackets = new List<byte[]>();

        var camera = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);

            async Task<(string Method, Dictionary<string, string> Headers)> ReadRequestAsync()
            {
                // After PLAY the client interleaves RTP; peel those frames off first.
                while (true)
                {
                    var first = stream.ReadByte();
                    if (first == '$')
                    {
                        var hdr = new byte[3];
                        await stream.ReadExactlyAsync(hdr);
                        var payload = new byte[(hdr[1] << 8) | hdr[2]];
                        await stream.ReadExactlyAsync(payload);
                        rtpPackets.Add(payload);
                        continue;
                    }
                    var line = new StringBuilder().Append((char)first);
                    int c;
                    while ((c = stream.ReadByte()) != '\n') line.Append((char)c);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (true)
                    {
                        var h = new StringBuilder();
                        while ((c = stream.ReadByte()) != '\n') h.Append((char)c);
                        var text = h.ToString().TrimEnd('\r');
                        if (text.Length == 0) break;
                        var colon = text.IndexOf(':');
                        headers[text[..colon]] = text[(colon + 1)..].Trim();
                    }
                    requests.Add(line.ToString().TrimEnd('\r'));
                    return (line.ToString().Split(' ')[0], headers);
                }
            }

            async Task ReplyAsync(Dictionary<string, string> req, string status, string extra = "", string body = "")
            {
                var text = $"RTSP/1.0 {status}\r\nCSeq: {req["CSeq"]}\r\n{extra}" +
                           (body.Length > 0 ? $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n" : "") + "\r\n" + body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
            }

            var (_, d1) = await ReadRequestAsync();
            await ReplyAsync(d1, "401 Unauthorized", "WWW-Authenticate: Digest realm=\"cam\", nonce=\"abc\"\r\n");
            var (_, d2) = await ReadRequestAsync();
            Assert.StartsWith("Digest ", d2["Authorization"]);
            Assert.Equal(OnvifBackchannelClient.BackchannelRequire, d2["Require"]);
            await ReplyAsync(d2, "200 OK", $"Content-Base: rtsp://127.0.0.1:{port}/stream1/\r\nContent-Type: application/sdp\r\n", sdp.Replace("\n", "\r\n"));
            var (_, setup) = await ReadRequestAsync();
            await ReplyAsync(setup, "200 OK", "Session: ABC123;timeout=60\r\nTransport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n");
            var (_, play) = await ReadRequestAsync();
            Assert.Equal("ABC123", play["Session"]);
            await ReplyAsync(play, "200 OK", "Session: ABC123\r\n");
            while (true)
            {
                var (method, req) = await ReadRequestAsync();
                if (method == "TEARDOWN") { await ReplyAsync(req, "200 OK"); return; }
            }
        });

        await using (var client = await OnvifBackchannelClient.ConnectAsync(new Uri($"rtsp://127.0.0.1:{port}/stream1"), "admin", "pw",
                         NullLogger.Instance, CancellationToken.None))
        {
            Assert.Equal("PCMA", client.Codec);
            // 25 ms of silence: one full 20 ms packet now, the rest held for the next call.
            await client.SendPcmAsync(new byte[400], CancellationToken.None);
            await client.SendPcmAsync(new byte[240], CancellationToken.None);
        }
        await camera.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        Assert.Equal(["DESCRIBE", "DESCRIBE", "SETUP", "PLAY", "TEARDOWN"], requests.Select(r => r.Split(' ')[0]));
        Assert.Equal($"SETUP rtsp://127.0.0.1:{port}/stream1/trackID=5 RTSP/1.0", requests[2]);
        Assert.Equal(2, rtpPackets.Count);
        Assert.All(rtpPackets, p => Assert.Equal(12 + 160, p.Length));
        Assert.Equal(0x80 | 8, rtpPackets[0][1]);  // marker on the first packet, PCMA
        Assert.Equal(8, rtpPackets[1][1]);
        Assert.Equal(0xD5, rtpPackets[0][12]);      // A-law silence
        Assert.Equal(160u, ReadTimestamp(rtpPackets[1]) - ReadTimestamp(rtpPackets[0]));
    }

    private static uint ReadTimestamp(byte[] rtp) => (uint)(rtp[4] << 24 | rtp[5] << 16 | rtp[6] << 8 | rtp[7]);

    [Fact]
    public void WavWriterPatchesSizesOnClose()
    {
        var path = Path.Combine(Path.GetTempPath(), $"talk-{Guid.NewGuid():N}", "a.wav");
        using (var w = new WavWriter(path, 8000)) w.Write(new byte[1600]);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(44 + 1600, bytes.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(36u + 1600, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(1600u, BitConverter.ToUInt32(bytes, 40));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void RelayReadsTheNodesRecordedReport()
    {
        var json = Encoding.UTF8.GetBytes("""{"type":"recorded","startUtc":"2026-10-12T10:00:00Z","endUtc":"2026-10-12T10:00:04.5Z","path":"D:\\v\\a.wav","bytes":72044}""");
        Assert.True(TalkRelay.TryReadRecorded(json, out var r));
        Assert.Equal(new DateTime(2026, 10, 12, 10, 0, 4, 500, DateTimeKind.Utc), r.EndUtc);
        Assert.Equal(@"D:\v\a.wav", r.Path);
        Assert.False(TalkRelay.TryReadRecorded(Encoding.UTF8.GetBytes("""{"type":"ready"}"""), out _));
    }

    [Fact]
    public void OnlyOneOperatorTalksPerCamera()
    {
        var registry = new TalkSessionRegistry();
        var camera = Guid.NewGuid();
        var alice = new string("alice".AsSpan());
        Assert.True(registry.TryAcquire(camera, alice, out _));
        Assert.False(registry.TryAcquire(camera, new string("bob".AsSpan()), out var holder));
        Assert.Equal("alice", holder);
        registry.Release(camera, alice);
        Assert.True(registry.TryAcquire(camera, new string("bob".AsSpan()), out _));
    }

    [Fact]
    public void TalkClipsAgeOutByTheirRecordedStart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"talk-{Guid.NewGuid():N}");
        var camera = Guid.NewGuid();
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var old = TalkSessionHandler.RecordingPath(root, camera, now.AddDays(-31));
        var recent = TalkSessionHandler.RecordingPath(root, camera, now.AddDays(-2));
        foreach (var p in new[] { old, recent }) { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); }

        var dir = TalkSessionHandler.TalkDirectory(root, camera);
        Assert.Equal([old], StorageManager.SelectExpiredTalkClips(dir, now, 30));
        Assert.Empty(StorageManager.SelectExpiredTalkClips(dir, now, 0)); // keep forever
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task TalkRecordingsExpireWithTheFootageAroundThem()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var withFootage = Guid.NewGuid();
        var noFootage = Guid.NewGuid();
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment { CameraId = withFootage, NodeId = Guid.NewGuid(), FilePath = "s", StartUtc = now.AddDays(-1), EndUtc = now.AddDays(-1).AddMinutes(1) });
        db.TalkRecordings.AddRange(
            new TalkRecording { CameraId = withFootage, FilePath = "old", StartUtc = now.AddDays(-2), EndUtc = now.AddDays(-2).AddSeconds(5) },
            new TalkRecording { CameraId = withFootage, FilePath = "kept", StartUtc = now.AddHours(-1), EndUtc = now.AddHours(-1).AddSeconds(5) },
            new TalkRecording { CameraId = noFootage, FilePath = "orphan", StartUtc = now, EndUtc = now.AddSeconds(5) });
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepTalkRecordingsAsync(db, CancellationToken.None);

        Assert.Equal(["kept"], db.TalkRecordings.Select(t => t.FilePath));
    }
}

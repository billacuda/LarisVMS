using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Media;
using LarisVMS.Media.Talk;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Two-way audio follow-ups: non-AAC camera audio re-encoded for browsers, talk audio mixed
/// into exports, and the Dahua HTTP audio fallback.</summary>
public class TwoWayAudioFollowUpTests
{
    [Theory]
    [InlineData("pcm_alaw", true)]
    [InlineData("pcm_mulaw", true)]
    [InlineData("adpcm_g726", true)]
    [InlineData("aac", false)]
    [InlineData("aac (LC)", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyNonAacAudioIsReencoded(string? codec, bool expected) => Assert.Equal(expected, AudioTranscode.NeedsAac(codec));

    [Fact]
    public void RecorderCopiesVideoAndEncodesAudioWhenAsked()
    {
        var plain = new RecordingSessionOptions("ffmpeg", "rtsp://cam/", "out");
        Assert.Equal(["-c", "copy"], RecordingSession.BuildCodecArgs(plain));

        var transcoded = plain with { TranscodeAudioToAac = true };
        Assert.Equal(["-c:v", "copy", "-c:a", "aac", "-b:a", "32k"], RecordingSession.BuildCodecArgs(transcoded));

        // With a privacy mask the video is encoded anyway; audio still follows the flag.
        var masked = transcoded with { PrivacyMaskFilters = ["drawbox=x=0:y=0:w=10:h=10:color=black:t=fill"] };
        Assert.Equal(["-c:a", "aac", "-b:a", "32k"], RecordingSession.BuildCodecArgs(masked).TakeLast(4));
    }

    [Fact]
    public void SubLiveSessionEncodesAudioWhenAsked()
    {
        var args = SubLiveSession.BuildFfmpegArgs(new SubLiveSessionOptions("ffmpeg", "rtsp://cam/sub", TranscodeAudioToAac: true)).ToList();
        Assert.Contains("aac", args);
        Assert.DoesNotContain("copy", args.Where((a, i) => i > 0 && args[i - 1] == "-c"));
    }

    [Fact]
    public void ExportWithoutTalkIsAPlainRemux()
    {
        var args = ExportRunner.BuildArgs("list.txt", "out.mp4", [], segmentsHaveAudio: true);
        Assert.Equal(["-nostdin", "-f", "concat", "-safe", "0", "-i", "list.txt", "-c", "copy", "-y", "out.mp4"], args);
    }

    [Fact]
    public void ExportMixesTalkOverTheCameraAudio()
    {
        var args = ExportRunner.BuildArgs("list.txt", "out.mp4",
            [new ExportRunner.TalkClip("a.wav", 12.5), new ExportRunner.TalkClip("b.wav", -0.75)], segmentsHaveAudio: true);

        Assert.Equal(["-i", "a.wav", "-i", "b.wav"], args.Skip(7).Take(4));
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.StartsWith("[0:a]", filter);
        Assert.Contains("[1:a]", filter);
        Assert.Contains("adelay=12500:all=1[t0]", filter);
        Assert.Contains("atrim=start=0.75,asetpts=PTS-STARTPTS[t1]", filter);
        Assert.EndsWith("[base][t0][t1]amix=inputs=3:duration=first:dropout_transition=0:normalize=0[aout]", filter);
        Assert.Equal(["-map", "0:v", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac"], args.Skip(args.IndexOf("-filter_complex") + 2).Take(8));
        Assert.Contains("-shortest", args);
    }

    [Fact]
    public void ExportMixesTalkOverSilenceWhenTheCameraHasNoAudio()
    {
        var args = ExportRunner.BuildArgs("list.txt", "out.mp4", [new ExportRunner.TalkClip("a.wav", 1)], segmentsHaveAudio: false);
        Assert.StartsWith("anullsrc=r=48000:cl=mono", args[args.IndexOf("-filter_complex") + 1]);
    }

    /// <summary>A scripted Dahua camera: answers the empty challenge POST with a digest 401, then
    /// accepts the streaming POST and records its headers and body until the client closes.</summary>
    [Fact]
    public async Task DahuaFallbackStreamsALawWithDigestAuth()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<(string Line, Dictionary<string, string> Headers, byte[] Body)>();

        var camera = Task.Run(async () =>
        {
            for (var n = 0; n < 3; n++) // ConnectAsync's challenge, then one press = challenge + stream
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var head = new StringBuilder();
                while (!head.ToString().EndsWith("\r\n\r\n")) head.Append((char)stream.ReadByte());
                var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var headers = lines.Skip(1).ToDictionary(l => l[..l.IndexOf(':')], l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                if (headers["Content-Length"] == "0")
                {
                    requests.Add((lines[0], headers, []));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Digest realm=\"Login to cam\", qop=\"auth\", nonce=\"n1\", opaque=\"o1\"\r\nContent-Length: 0\r\n\r\n"));
                    continue;
                }
                var body = new MemoryStream();
                await stream.CopyToAsync(body); // until the client closes the press
                requests.Add((lines[0], headers, body.ToArray()));
            }
        });

        await using (var sink = await DahuaAudioPostClient.ConnectAsync(new Uri($"http://127.0.0.1:{port}"), "admin", "pw", NullLogger.Instance, CancellationToken.None))
        {
            Assert.Equal("PCMA", sink.Codec);
            await sink.SendPcmAsync(new byte[320], CancellationToken.None); // 160 samples of silence
            await sink.EndTalkspurtAsync(CancellationToken.None);
        }
        await camera.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        var streamed = requests[^1];
        Assert.Equal("POST /cgi-bin/audio.cgi?action=postAudio&httptype=singlepart&channel=1 HTTP/1.1", streamed.Line);
        Assert.Equal("Audio/G.711A", streamed.Headers["Content-Type"]);
        Assert.StartsWith("Digest username=\"admin\", realm=\"Login to cam\", nonce=\"n1\"", streamed.Headers["Authorization"]);
        Assert.Equal(160, streamed.Body.Length);
        Assert.All(streamed.Body, b => Assert.Equal(0xD5, b)); // A-law silence
    }

    [Fact]
    public async Task DahuaWithoutAnAudioApiIsReportedAsUnsupported()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var camera = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var head = new StringBuilder();
            while (!head.ToString().EndsWith("\r\n\r\n")) head.Append((char)stream.ReadByte());
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"));
        });

        var ex = await Assert.ThrowsAsync<BackchannelException>(() =>
            DahuaAudioPostClient.ConnectAsync(new Uri($"http://127.0.0.1:{port}"), "admin", "pw", NullLogger.Instance, CancellationToken.None));
        Assert.Contains("doesn't support two-way audio", ex.Message);
        await camera;
        listener.Stop();
    }

    [Fact]
    public void RedactedExportReEncodesTheVideoWithTheMask()
    {
        var filters = ExportRunner.RedactionFilters("[{\"points\":[[0.1,0.1],[0.4,0.1],[0.4,0.4],[0.1,0.4]]}]");
        Assert.NotEmpty(filters);

        var plain = ExportRunner.BuildArgs("list.txt", "out.mp4", [], true, filters, "h264_qsv");
        Assert.Equal(string.Join(',', filters), plain[plain.IndexOf("-vf") + 1]);
        Assert.Equal("h264_qsv", plain[plain.IndexOf("-c:v") + 1]);
        Assert.Equal("copy", plain[plain.IndexOf("-c:a") + 1]);

        // With talk mixing the video goes through the same complex graph.
        var mixed = ExportRunner.BuildArgs("list.txt", "out.mp4", [new ExportRunner.TalkClip("a.wav", 2)], true, filters, "libx264");
        Assert.DoesNotContain("-vf", mixed);
        Assert.EndsWith("[vout]", mixed[mixed.IndexOf("-filter_complex") + 1]);
        Assert.Equal("[vout]", mixed[mixed.IndexOf("-map") + 1]);
    }

    [Fact]
    public void UnreadableOrEmptyRedactionsLeaveAPlainRemux()
    {
        Assert.Empty(ExportRunner.RedactionFilters(null));
        Assert.Empty(ExportRunner.RedactionFilters("not json"));
        Assert.Empty(ExportRunner.RedactionFilters("[]"));
        Assert.Equal(["-nostdin", "-f", "concat", "-safe", "0", "-i", "list.txt", "-c", "copy", "-y", "out.mp4"],
            ExportRunner.BuildArgs("list.txt", "out.mp4", [], true, [], null));
    }
}

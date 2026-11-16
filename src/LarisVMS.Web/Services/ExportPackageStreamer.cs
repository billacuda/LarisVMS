using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LarisVMS.Core;
using LarisVMS.Core.Localization;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Services;

/// <summary>
/// Sends an export package (ExportPackageBuilder) to the browser as one zip, written as it goes: the
/// player and data first, then each video straight from its recorder, then the signed list of every
/// file. Nothing is staged on the server.
///
/// Each video's SHA-256 is worked out as it passes through and must match the hash the recorder
/// reported when it made the export — the hash the package's signed list carries. If a file changed on
/// the recorder since then, the download stops there rather than handing out a package whose list
/// vouches for bytes it doesn't contain.
/// </summary>
public static class ExportPackageStreamer
{
    /// <summary>The player's own files: (path in the package, path under wwwroot).</summary>
    internal static readonly IReadOnlyList<(string ZipPath, string WebPath)> PlayerFiles =
    [
        ("Player.html", "export-player/Player.html"),
        ("player/player.js", "export-player/player.js"),
        ("player/player.css", "export-player/player.css"),
        ("player/i18n.js", "js/i18n.js"),
        ("player/sha256-stream.js", "js/sha256-stream.js"),
        ("player/export-verify-core.js", "js/export-verify-core.js"),
    ];

    /// <summary>The script text the player needs, in the exporting person's language.</summary>
    internal static readonly string[] StringPrefixes = ["js.exportPlayer.", "js.exportsVerify."];

    public static async Task WriteAsync(HttpContext ctx, ExportPackagePlan plan, ExportPackageBuilder builder,
        IHttpClientFactory httpFactory, IWebHostEnvironment env, LocaleCatalog catalog, ILogger logger, CancellationToken ct)
    {
        var lang = catalog.Match(System.Globalization.CultureInfo.CurrentUICulture.Name) ?? LocaleCatalog.English;
        var files = new List<ExportPackageFile>(plan.Files);
        foreach (var (zipPath, webPath) in PlayerFiles)
            files.Add(new ExportPackageFile(zipPath, await ReadWebFileAsync(env, webPath, ct)));
        var strings = new Dictionary<string, string>();
        foreach (var prefix in StringPrefixes)
            foreach (var (key, text) in catalog.Resolve(lang, prefix)) strings[key] = text;
        files.Add(new ExportPackageFile("player/strings.js", Encoding.UTF8.GetBytes(
            ExportPackageBuilder.ScriptOf("larisvmsI18nData", JsonSerializer.Serialize(new { lang, strings })))));
        files.Add(new ExportPackageFile("README.txt", Encoding.UTF8.GetBytes(Readme(plan))));

        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition
        {
            FileName = plan.DownloadName,
            DispositionType = "attachment",
        }.ToString();
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        var zip = new StreamingZipWriter(ctx.Response.Body);
        var listed = new List<(string Path, long Size, string Sha256)>();
        var stamp = DateTime.Now;
        string Entry(string path) => $"{plan.RootFolder}/{path}";

        foreach (var file in files)
        {
            await zip.AddAsync(Entry(file.ZipPath), file.Content, stamp, ct);
            listed.Add((file.ZipPath, file.Content.LongLength, ExportPackageBuilder.Sha256(file.Content)));
        }

        foreach (var video in plan.Videos)
        {
            var node = plan.Nodes[video.NodeId];
            using var response = await OpenVideoAsync(httpFactory, video, node, ct);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var hashing = new HashingStream(source);
            var size = await zip.AddAsync(Entry(video.ZipPath), hashing, stamp, ct);
            var sha256 = hashing.HashHex();
            if ((long)size != video.SizeBytes || !string.Equals(sha256, video.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Export package {JobId}: {File} on its recorder no longer matches the export ({Size} bytes, SHA-256 {Sha256}; expected {ExpectedSize}, {Expected}). Download stopped.",
                    plan.Job.Id, video.ZipPath, size, sha256, video.SizeBytes, video.Sha256);
                throw new InvalidDataException(Loc.T("api.exportFileChanged"));
            }
            listed.Add((video.ZipPath, video.SizeBytes, video.Sha256));
        }

        var manifest = await builder.SignManifestAsync(plan, listed, ctx.User.Identity?.Name, ct);
        await zip.AddAsync(Entry(ExportPackageBuilder.ManifestFileName), manifest, stamp, ct);
        await zip.FinishAsync(ct);
    }

    private static async Task<byte[]> ReadWebFileAsync(IWebHostEnvironment env, string webPath, CancellationToken ct)
    {
        var file = env.WebRootFileProvider.GetFileInfo(webPath);
        if (!file.Exists) throw new FileNotFoundException($"The export player file {webPath} is missing from wwwroot.");
        await using var stream = file.CreateReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static async Task<HttpResponseMessage> OpenVideoAsync(IHttpClientFactory httpFactory, ExportPackageVideo video, ExportPackageNode node, CancellationToken ct)
    {
        var token = MediaToken.IssueForExportDownload(video.ExportItemId, video.NodeFilePath, node.MediaSigningKey!, TimeSpan.FromSeconds(30));
        var nodeUri = $"http://{node.Ip}:{node.Port}/export-file/{video.ExportItemId}" +
            $"?path={Uri.EscapeDataString(video.NodeFilePath)}&token={Uri.EscapeDataString(token)}";
        var client = httpFactory.CreateClient(NodeTlsTargets.HttpClientName);
        // A big file takes a while; the timeout only guards the wait for the first byte.
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token), HttpCompletionOption.ResponseHeadersRead, headers.Token);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The recorder answered {(int)status} for {video.ZipPath}.");
        }
        return response;
    }

    /// <summary>The README in the package, in the exporting person's language.</summary>
    internal static string Readme(ExportPackagePlan plan)
    {
        var from = DateTime.SpecifyKind(plan.Job.FromUtc, DateTimeKind.Utc).ToLocalTime();
        var to = DateTime.SpecifyKind(plan.Job.ToUtc, DateTimeKind.Utc).ToLocalTime();
        return Loc.T("exports.package.readme", new
        {
            from = from.ToString("F"),
            to = to.ToString("F"),
            zone = TimeZoneInfo.Local.DisplayName,
            cameras = plan.Videos.Select(v => v.ZipPath.Split('/')[1]).Distinct().Count(),
        }).Replace("\n", "\r\n");
    }

    /// <summary>Reads through to the stream inside, working out its SHA-256 on the way.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public string HashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0) _hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0) _hash.AppendData(buffer, offset, read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

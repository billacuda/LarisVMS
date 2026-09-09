using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Proxy;

/// <summary>
/// Proxy self-update — the direct analogue of <c>LarisVMS.Node.Update.UpdateService</c>, trimmed to
/// what a relay needs (no Vision Service second binary; that is recorder-node-only). Downloads a new
/// <c>LarisVMS.Proxy.exe</c> over the authenticated control channel (same Bearer <c>{proxyId}:{secret}</c>
/// as <see cref="ProxyApiClient"/>), verifies its SHA-256 against the heartbeat response's checksum,
/// then hands off to <c>LarisVMS.NodeUpdater.exe</c> — the same detached binary-swap helper the node
/// uses, invoked with <c>--service LarisVMSProxy</c> so it waits for this Windows Service to stop,
/// swaps <c>LarisVMS.Proxy.exe</c>, and starts the service again — and calls
/// <see cref="IHostApplicationLifetime.StopApplication"/> so the SCM stops this process cleanly,
/// releasing the file lock the detached updater is waiting on.
///
/// Media relaying is a dumb per-connection pass-through with no recording state to lose, so an update
/// here is a brief drop of in-flight live/playback connections while the service restarts — the same
/// short interruption a node update already causes, and browsers reconnect through the fallback chain
/// (backup proxy → direct-to-node → central) in the meantime.
/// </summary>
public class ProxyUpdateService(ProxyConfig config, bool insecureTls, ILogger log, IHostApplicationLifetime lifetime,
    HttpMessageHandler? httpHandler = null)
{
    private static readonly string UpdateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "update");

    private static readonly string StagedPath = Path.Combine(UpdateDir, "LarisVMS.Proxy.exe");

    // Published alongside LarisVMS.Proxy.exe by build-proxy.ps1 and copied into the same install
    // directory by install-proxy.ps1 — always sits next to the currently-running binary. Reuses the
    // recorder node's updater verbatim: it is already service-name-parameterised (--service), and its
    // wait-for-stopped / swap / start-again / re-apply-failure-recovery steps are all generic sc.exe.
    private static readonly string UpdaterPath = Path.Combine(AppContext.BaseDirectory, "LarisVMS.NodeUpdater.exe");

    private const string ServiceName = "LarisVMSProxy";

    /// <summary>Guards against <c>ProxyWorker</c>'s loop re-triggering an update while one is already
    /// downloading/verifying. Left true after a successful <see cref="ApplyWindows"/> launch
    /// deliberately — this process is about to be stopped by the SCM, so there is no "next cycle" to
    /// protect against. Same reasoning as the node's UpdateService.IsApplying.</summary>
    public bool IsApplying { get; private set; }

    public async Task<bool> TryApplyAsync(ProxyUpdateInfoDto update, CancellationToken ct)
    {
        if (IsApplying)
        {
            log.LogInformation("An update is already in progress — ignoring.");
            return false;
        }

        IsApplying = true;

        try
        {
            log.LogInformation("Proxy update available: {Version}. Downloading...", update.Version);
            Directory.CreateDirectory(UpdateDir);

            // httpHandler is a test-only seam (a fake HttpMessageHandler returning canned bytes) —
            // production callers never pass one. disposeHandler:false when injected, since a test owns
            // and may reuse it; true when this created its own.
            using var http = httpHandler is not null
                ? new HttpClient(httpHandler, disposeHandler: false)
                : new HttpClient(CreateDefaultHandler());
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{config.ProxyId}:{config.Secret}");

            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = File.Create(StagedPath))
                await response.Content.CopyToAsync(fileStream, ct);

            var actualSha256 = await ComputeSha256Async(StagedPath, ct);
            if (!string.Equals(actualSha256, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogError(
                    "Checksum mismatch downloading proxy update {Version}. Expected {Expected}, got {Actual}. Aborting — the running binary was not touched.",
                    update.Version, update.Sha256, actualSha256);
                TryCleanStaged();
                IsApplying = false;
                return false;
            }

            log.LogInformation("Checksum verified. Applying update to {Version}...", update.Version);
            ApplyWindows();
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Proxy update failed");
            TryCleanStaged();
            IsApplying = false;
            return false;
        }
    }

    private void ApplyWindows()
    {
        if (!File.Exists(UpdaterPath))
        {
            log.LogWarning("Updater binary not found at {Path} — skipping update. Re-run install-proxy.ps1 to install it.", UpdaterPath);
            IsApplying = false;
            return;
        }

        var currentBinary = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "LarisVMS.Proxy.exe");

        using var updater = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = $"--new \"{StagedPath}\" --current \"{currentBinary}\" --service {ServiceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        updater.Start();

        log.LogInformation("Updater launched. Stopping service for binary swap...");
        lifetime.StopApplication();
    }

    private HttpClientHandler CreateDefaultHandler()
    {
        var handler = new HttpClientHandler();
        if (insecureTls)
        {
            // Same rationale as ProxyApiClient's own acceptAnyCertificate flag: a self-hosted
            // LarisVMS.Web behind a self-signed cert on a LAN is a normal deployment shape here.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        return handler;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fileStream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryCleanStaged()
    {
        try { if (File.Exists(StagedPath)) File.Delete(StagedPath); }
        catch { /* best effort */ }
    }
}

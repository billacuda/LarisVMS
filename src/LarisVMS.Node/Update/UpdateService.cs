using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node.Update;

/// <summary>
/// Port of dploid.Agent's Update/UpdateService.cs (Windows-only path — LarisVMS.Node has no Linux
/// build; see NodeConfigStore's doc comment). Downloads a new LarisVMS.Node.exe build over an
/// authenticated HttpClient (same Bearer "{nodeId}:{secret}" shape as NodeApiClient), verifies its
/// SHA-256 against the heartbeat response's checksum, then hands off to LarisVMS.NodeUpdater.exe — a
/// detached process that waits for this Windows Service to actually stop before swapping the binary
/// (this process can't safely overwrite its own running .exe file while it's still executing) — and
/// calls IHostApplicationLifetime.StopApplication() so the Service Control Manager stops this process
/// cleanly, releasing the file lock the detached updater is waiting on.
/// </summary>
public class UpdateService(NodeConfig config, bool insecureTls, ILogger log, IHostApplicationLifetime lifetime,
    HttpMessageHandler? httpHandler = null)
{
    private static readonly string UpdateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "update");

    private static readonly string StagedPath = Path.Combine(UpdateDir, "LarisVMS.Node.exe");

    // Published alongside LarisVMS.Node.exe by build-node.ps1 and copied into the same install
    // directory by install-node.ps1 — always sits next to the currently-running binary.
    private static readonly string UpdaterPath = Path.Combine(AppContext.BaseDirectory, "LarisVMS.NodeUpdater.exe");

    /// <summary>Guards against NodeWorker.ReconcileLoopAsync re-triggering an update while one is
    /// already downloading/verifying — in practice the reconcile loop already awaits TryApplyAsync
    /// synchronously before its next 30s tick can start, so there's no real concurrent-call
    /// possibility today, but this makes that guarantee explicit rather than relying on the caller's
    /// loop shape never changing. Left true after a successful ApplyWindows() launch deliberately —
    /// this process is about to be stopped by the SCM, so there is no "next reconcile" to protect
    /// against.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>Restarts this node's Windows Service on request from the Nodes page. Reuses the exact
    /// mechanism ApplyWindows uses for an update — launch the detached updater, then stop ourselves —
    /// with --restart-only so it skips the binary swap and only does the wait-for-stopped/start-again
    /// half. A service can't restart itself from inside its own process (nothing would be left running
    /// to start it again once the SCM stops it), which is why this has to go through the helper.
    ///
    /// Returns false when the updater binary is missing, so the caller can report that honestly rather
    /// than stopping a recorder with nothing able to bring it back — an older node install predating
    /// the updater would otherwise be knocked permanently offline by this button.</summary>
    public bool TryRestartService()
    {
        if (!File.Exists(UpdaterPath))
        {
            log.LogWarning("Restart requested but the updater binary is missing at {Path} — refusing, since " +
                "nothing would be able to start this service again. Re-run install-node.ps1.", UpdaterPath);
            return false;
        }

        using var updater = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = $"--restart-only --service {UpdaterLogic_DefaultServiceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        updater.Start();

        log.LogInformation("Restart requested from the server — stopping service; the updater will start it again.");
        lifetime.StopApplication();
        return true;
    }

    /// <summary>Mirrors LarisVMS.NodeUpdater's own UpdaterLogic.DefaultServiceName. Duplicated rather
    /// than referenced because LarisVMS.Node does not reference the updater project (it launches it as
    /// a separate executable), and it's the same literal ApplyWindows already passes as --service.</summary>
    private const string UpdaterLogic_DefaultServiceName = "LarisVMSNode";

    public async Task<bool> TryApplyAsync(NodeUpdateInfoDto update, CancellationToken ct)
    {
        if (IsApplying)
        {
            log.LogInformation("An update is already in progress — ignoring.");
            return false;
        }

        IsApplying = true;

        try
        {
            log.LogInformation("Node update available: {Version}. Downloading...", update.Version);
            Directory.CreateDirectory(UpdateDir);

            // httpHandler is a test-only seam (a fake HttpMessageHandler returning canned bytes, so
            // checksum-mismatch rejection can be verified without a real HTTP server) — production
            // callers (NodeWorker via Program.cs) never pass one, so this always builds its own
            // HttpClientHandler there. disposeHandler:false when a handler was injected, since a test
            // owns and may reuse that handler across calls; true when this created its own, matching
            // HttpClient's normal default-disposal behavior.
            using var http = httpHandler is not null
                ? new HttpClient(httpHandler, disposeHandler: false)
                : new HttpClient(CreateDefaultHandler());
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{config.NodeId}:{config.Secret}");

            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = File.Create(StagedPath))
                await response.Content.CopyToAsync(fileStream, ct);

            var actualSha256 = await ComputeSha256Async(StagedPath, ct);
            if (!string.Equals(actualSha256, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogError(
                    "Checksum mismatch downloading node update {Version}. Expected {Expected}, got {Actual}. Aborting — the running binary was not touched.",
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
            log.LogError(ex, "Node update failed");
            TryCleanStaged();
            IsApplying = false;
            return false;
        }
    }

    private void ApplyWindows()
    {
        if (!File.Exists(UpdaterPath))
        {
            log.LogWarning("Updater binary not found at {Path} — skipping update. Re-run install-node.ps1 to install it.", UpdaterPath);
            IsApplying = false;
            return;
        }

        var currentBinary = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "LarisVMS.Node.exe");

        using var updater = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = $"--new \"{StagedPath}\" --current \"{currentBinary}\" --service LarisVMSNode",
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
            // Same rationale as NodeApiClient's own acceptAnyCertificate flag: a self-hosted
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

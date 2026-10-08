using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Update;

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

    // Object detection plan follow-up: same UpdateDir, own filename — VisionServiceSupervisor.ExeFileName
    // is the fixed name a Vision Service binary must have on disk regardless of which execution-provider
    // variant it was published as, so staging under that exact name is what makes the eventual
    // TrySwapBinary a same-name overwrite rather than a rename.
    private static readonly string StagedVisionPath = Path.Combine(UpdateDir, VisionServiceSupervisor.ExeFileName);

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

            log.LogInformation("Checksum verified.");

            // Object detection plan follow-up: best-effort, and never gates the Node update itself —
            // AI detection is additive throughout this whole feature, and a Vision Service download
            // hiccup (or this build genuinely having no Vision binary registered) must not block or
            // fail the primary node exe update, the one actually required for recording to keep
            // working. A failure here just means the running Vision Service binary (if any) stays on
            // its previous version until the next successful heartbeat's update offer.
            var visionStaged = false;
            if (update is { VisionDownloadUrl: { } visionUrl, VisionSha256: { } visionSha, VisionSizeBytes: not null })
            {
                visionStaged = await TryStageVisionAsync(http, visionUrl, visionSha, update.Version, ct);
            }

            log.LogInformation("Applying update to {Version}...", update.Version);
            ApplyWindows(visionStaged);
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

    /// <summary>Downloads and verifies the Vision Service binary the same way TryApplyAsync already
    /// does for the Node exe, staged under its own fixed filename (StagedVisionPath). Returns false
    /// (never throws) on any failure — download, non-2xx, or checksum mismatch — logged as a warning
    /// rather than an error, since a missed Vision Service update is a "try again next heartbeat"
    /// situation, not a node-health concern the way a failed Node exe update would be. Internal
    /// (not private) so it's directly testable without going through TryApplyAsync/ApplyWindows —
    /// same "pull out the piece a test can call directly" reasoning UpdaterLogic's own doc comment
    /// describes, since ApplyWindows' own real-process side effects are deliberately excluded from
    /// this test project (see UpdateServiceTests' own doc comment).</summary>
    internal async Task<bool> TryStageVisionAsync(HttpClient http, string downloadUrl, string expectedSha256, string version, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Vision Service update download for {Version} returned {Status} — skipping this component, Node update proceeds regardless.",
                    version, response.StatusCode);
                return false;
            }

            await using (var fileStream = File.Create(StagedVisionPath))
                await response.Content.CopyToAsync(fileStream, ct);

            var actualSha256 = await ComputeSha256Async(StagedVisionPath, ct);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning(
                    "Vision Service update checksum mismatch for {Version}. Expected {Expected}, got {Actual} — skipping this component, Node update proceeds regardless.",
                    version, expectedSha256, actualSha256);
                TryCleanStagedVision();
                return false;
            }

            log.LogInformation("Vision Service update checksum verified.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Vision Service update download failed — skipping this component, Node update proceeds regardless.");
            TryCleanStagedVision();
            return false;
        }
    }

    private void ApplyWindows(bool visionStaged = false)
    {
        if (!File.Exists(UpdaterPath))
        {
            log.LogWarning("Updater binary not found at {Path} — skipping update. Re-run install-node.ps1 to install it.", UpdaterPath);
            IsApplying = false;
            return;
        }

        var currentBinary = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "LarisVMS.Node.exe");
        var currentVisionBinary = Path.Combine(AppContext.BaseDirectory, VisionServiceSupervisor.ExeFileName);

        // Swap while still running (see InPlaceBinarySwap), so the updater only has to restart the
        // service. Falls back to the updater doing the swap after the stop if the rename fails.
        if (InPlaceBinarySwap.TrySwap(StagedPath, currentBinary, out var swapError))
        {
            log.LogInformation("Swapped in the new node binary; restarting the service to run it.");
            if (visionStaged)
            {
                // Same rule as the updater: only keep an already-installed Vision Service current.
                if (!File.Exists(currentVisionBinary))
                {
                    log.LogInformation("Vision Service update skipped: it isn't installed on this node (run install-node.ps1 once to add AI detection).");
                    TryCleanStagedVision();
                }
                else if (!InPlaceBinarySwap.TrySwap(StagedVisionPath, currentVisionBinary, out var visionError))
                {
                    log.LogWarning("Could not swap in the new Vision Service binary ({Error}); AI detection stays on the previous one until the next update.", visionError);
                    TryCleanStagedVision();
                }
            }
            LaunchUpdater($"--restart-only --service {UpdaterLogic_DefaultServiceName}");
            return;
        }
        log.LogWarning("Could not swap the node binary in place ({Error}); handing the swap to the updater after the service stops.", swapError);

        var arguments = $"--new \"{StagedPath}\" --current \"{currentBinary}\" --service LarisVMSNode";
        if (visionStaged)
        {
            // NodeUpdater itself decides whether this swap actually happens (only if
            // currentVisionBinary already exists on disk — see its own doc comment for why): this
            // path only ever keeps an *already-installed* Vision Service exe current, since its
            // native dependencies (onnxruntime.dll etc.) and models\ are placed by install-node.ps1
            // and never delivered by this download/swap path. A node that's never had Vision Service
            // installed still needs install-node.ps1 run once for that, same as this feature's own
            // initial release (see CHANGELOG) — this only closes the gap for keeping it current
            // *after* that.
            arguments += $" --new-vision \"{StagedVisionPath}\" --current-vision \"{currentVisionBinary}\"";
        }

        LaunchUpdater(arguments);
    }

    private void LaunchUpdater(string arguments)
    {
        using var updater = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        updater.Start();

        log.LogInformation("Updater launched. Stopping service so it can be started again on the new build...");
        lifetime.StopApplication();
    }

    /// <summary>Run once at startup. Removes the "*.old" binaries an in-place swap left behind, and
    /// reports an update that was downloaded but never applied — its staged binary is still sitting in
    /// the update folder — in this node's own log, since the updater's reason is only in its separate
    /// updater-*.log file, which nobody looks at when the service just didn't come back.</summary>
    public void CheckLastUpdate()
    {
        InPlaceBinarySwap.CleanUp(AppContext.BaseDirectory);
        if (!File.Exists(StagedPath)) return;

        log.LogWarning("A node update downloaded {When:u} was never applied (the staged binary is still at {Path}). " +
            "See updater-*.log in {LogDir} for why. It will be downloaded again on the next update offer.",
            File.GetLastWriteTimeUtc(StagedPath), StagedPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs"));
        TryCleanStaged();
        TryCleanStagedVision();
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

    private static void TryCleanStagedVision()
    {
        try { if (File.Exists(StagedVisionPath)) File.Delete(StagedVisionPath); }
        catch { /* best effort */ }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Update;

namespace LarisVMS.Node.Update;

/// <summary>
/// Port of dploid.Agent's Update/UpdateService.cs. Downloads a new LarisVMS.Node.exe build over an
/// authenticated HttpClient (same Bearer "{nodeId}:{secret}" shape as NodeApiClient), verifies its
/// SHA-256 against the heartbeat response's checksum, then hands off to LarisVMS.NodeUpdater.exe — a
/// detached process that waits for this Windows Service to actually stop before swapping the binary
/// (this process can't safely overwrite its own running .exe file while it's still executing) — and
/// calls IHostApplicationLifetime.StopApplication() so the Service Control Manager stops this process
/// cleanly, releasing the file lock the detached updater is waiting on.
///
/// On Linux the node runs as the unprivileged larisvms user and can't write its program folder, so
/// it stages the verified files and writes a <see cref="LinuxUpdateRequest"/> instead; a systemd path
/// unit runs <c>LarisVMS.NodeUpdater --linux-apply</c> as root to install them and restart the service.
///
/// Both OSes also keep the Vision Service's native backend libraries current (<see cref="BackendsBundle"/>):
/// when the offer's stamp differs from the installed one, the bundle is downloaded, unpacked, and
/// checked into the update folder, then swapped in while the Vision Service isn't running (Windows:
/// by this service at its next start, before anything loads them; Linux: by the root helper).
/// </summary>
public class UpdateService(NodeConfig config, ILogger log, IHostApplicationLifetime lifetime,
    HttpMessageHandler? httpHandler = null)
{
    private static readonly string UpdateDir = Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "update");

    private static readonly string StagedPath = Path.Combine(UpdateDir, LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Node));

    // Object detection plan follow-up: same UpdateDir, own filename — VisionServiceSupervisor.ExeFileName
    // is the fixed name a Vision Service binary must have on disk regardless of which execution-provider
    // variant it was published as, so staging under that exact name is what makes the eventual
    // TrySwapBinary a same-name overwrite rather than a rename.
    private static readonly string StagedVisionPath = Path.Combine(UpdateDir, VisionServiceSupervisor.ExeFileName);

    private static readonly string StagedBackendsPath = Path.Combine(UpdateDir, BackendsBundle.StagedFolderName);

    private static readonly string StagedBackendsZip = Path.Combine(UpdateDir, BackendsBundle.ZipFileName);

    /// <summary>The systemd unit on Linux (larisvms-node.service).</summary>
    public const string LinuxServiceName = "larisvms-node";

    /// <summary>How long a Linux request may wait for the root helper before it's given up on.</summary>
    private static readonly TimeSpan LinuxApplyTimeout = TimeSpan.FromMinutes(5);

    // Published alongside LarisVMS.Node.exe by build-node.ps1 and copied into the same install
    // directory by install-node.ps1 — always sits next to the currently-running binary.
    private static readonly string UpdaterPath = Path.Combine(AppContext.BaseDirectory, LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Updater));

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
        if (OperatingSystem.IsLinux())
        {
            // The root helper restarts the unit (this user can't); see LinuxUpdateRequest.
            try
            {
                LarisVMS.Core.Security.LocalDataAcl.SecureFolder(UpdateDir);
                new LinuxUpdateRequest { RestartOnly = true }.WriteTo(UpdateDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Restart requested but the request for the update helper couldn't be written to {Path}.", UpdateDir);
                return false;
            }
            log.LogInformation("Restart requested from the server — the update helper (larisvms-node-update) restarts this service.");
            return true;
        }

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
            // Fresh, locked-down folder for each download: a file or folder a local user planted here
            // beforehand (and its owner, who could rewrite its permissions) doesn't survive into the
            // binary that's about to be installed and run as a service.
            LarisVMS.Core.Security.LocalDataAcl.RecreateProtectedDirectory(UpdateDir);

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

            using var response = await http.GetAsync(LarisVMS.Core.Security.UpdateDownload.Resolve(config.ServerUrl, update.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, ct);
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
                // Same host rule as the node download above: this client carries the node's secret,
                // so it only ever goes to the configured server.
                visionStaged = await TryStageVisionAsync(http,
                    LarisVMS.Core.Security.UpdateDownload.Resolve(config.ServerUrl, visionUrl).AbsoluteUri, visionSha, update.Version, ct);
            }

            var backendsStaged = update is { BackendsDownloadUrl: { } backendsUrl, BackendsSha256: { } backendsSha, BackendsStamp: { } backendsStamp }
                && await TryStageBackendsAsync(http, LarisVMS.Core.Security.UpdateDownload.Resolve(config.ServerUrl, backendsUrl).AbsoluteUri,
                    backendsSha, backendsStamp, update.Version, ct);

            log.LogInformation("Applying update to {Version}...", update.Version);
            if (OperatingSystem.IsLinux())
                ApplyLinux(update, visionStaged, backendsStaged);
            else
                ApplyWindows(visionStaged);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Node update failed");
            TryCleanStaged();
            TryCleanStagedVision();
            TryCleanStagedBackends();
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

    /// <summary>Downloads the build's native backend libraries, when the Vision Service is installed
    /// and its libraries' stamp differs from the offer's, and unpacks them into the update folder,
    /// checked against the signed stamp. Best-effort like the Vision Service binary: false leaves the
    /// installed libraries as they are.</summary>
    internal async Task<bool> TryStageBackendsAsync(HttpClient http, string downloadUrl, string expectedSha256, string expectedStamp,
        string version, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, VisionServiceSupervisor.ExeFileName))) return false;
        if (string.Equals(BackendsBundle.InstalledStamp(AppContext.BaseDirectory), expectedStamp, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using (var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("Native backend libraries for {Version} returned {Status}; AI detection keeps its current ones.", version, response.StatusCode);
                    return false;
                }
                await using var fileStream = File.Create(StagedBackendsZip);
                await response.Content.CopyToAsync(fileStream, ct);
            }

            var actualSha256 = await ComputeSha256Async(StagedBackendsZip, ct);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Native backend libraries for {Version}: checksum mismatch (expected {Expected}, got {Actual}); skipped.",
                    version, expectedSha256, actualSha256);
                TryCleanStagedBackends();
                return false;
            }

            if (Directory.Exists(StagedBackendsPath)) Directory.Delete(StagedBackendsPath, recursive: true);
            // Refuses entries that would land outside the folder.
            ZipFile.ExtractToDirectory(StagedBackendsZip, StagedBackendsPath, overwriteFiles: false);
            File.Delete(StagedBackendsZip);

            var stamp = BackendsBundle.ComputeStamp(StagedBackendsPath);
            if (!string.Equals(stamp, expectedStamp, StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Native backend libraries for {Version} don't match their signed stamp; skipped.", version);
                TryCleanStagedBackends();
                return false;
            }
            await File.WriteAllTextAsync(Path.Combine(StagedBackendsPath, BackendsBundle.StampFileName), stamp, ct);
            log.LogInformation("Native backend libraries for {Version} downloaded and verified.", version);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Native backend libraries for {Version} couldn't be downloaded; AI detection keeps its current ones.", version);
            TryCleanStagedBackends();
            return false;
        }
    }

    /// <summary>Linux: the verified files are staged; the root helper installs them (see
    /// <see cref="LinuxUpdateRequest"/>) and restarts this service, so nothing here stops it.</summary>
    private void ApplyLinux(NodeUpdateInfoDto update, bool visionStaged, bool backendsStaged)
    {
        MakeExecutable(StagedPath);
        if (visionStaged) MakeExecutable(StagedVisionPath);
        new LinuxUpdateRequest
        {
            Platform = LarisVMS.Core.PlatformId.Current,
            Version = update.Version,
            Sha256 = update.Sha256,
            // The signed payload covers the offered Vision Service hash whether or not it was staged.
            VisionSha256 = update.VisionSha256,
            PublisherSignature = update.PublisherSignature,
            BackendsStamp = backendsStaged ? update.BackendsStamp : null,
            BackendsPublisherSignature = backendsStaged ? update.BackendsPublisherSignature : null,
        }.WriteTo(UpdateDir);
        log.LogInformation("Update {Version} staged; the update helper (larisvms-node-update) installs it and restarts this service.", update.Version);
        _ = GiveUpIfNotAppliedAsync(update.Version);
    }

    /// <summary>When nothing picks the request up (the helper's units aren't installed, as when running
    /// a build from source), says so and lets the next offer try again.</summary>
    private async Task GiveUpIfNotAppliedAsync(string version)
    {
        await Task.Delay(LinuxApplyTimeout);
        var request = Path.Combine(UpdateDir, LinuxUpdateRequest.FileName);
        if (!File.Exists(request)) return;
        log.LogError("Update {Version} was staged {Minutes} minutes ago but the update helper never installed it. Check that " +
            "larisvms-node-update.path is enabled (systemctl status larisvms-node-update.path) and see updater-*.log in {LogDir}.",
            version, (int)LinuxApplyTimeout.TotalMinutes, LarisVMS.Core.LarisPaths.LogRoot);
        try { File.Delete(request); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* replaced by the next attempt */ }
        TryCleanStaged();
        TryCleanStagedVision();
        TryCleanStagedBackends();
        IsApplying = false;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
            ?? Path.Combine(AppContext.BaseDirectory, LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Node));
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
        BackendsBundle.CleanUp(AppContext.BaseDirectory);
        ApplyStagedBackends();
        if (!File.Exists(StagedPath)) return;

        log.LogWarning("A node update downloaded {When:u} was never applied (the staged binary is still at {Path}). " +
            "See updater-*.log in {LogDir} for why. It will be downloaded again on the next update offer.",
            File.GetLastWriteTimeUtc(StagedPath), StagedPath,
            LarisVMS.Core.LarisPaths.LogRoot);
        TryCleanStaged();
        TryCleanStagedVision();
    }

    /// <summary>Windows: swaps in native backend libraries the last update staged. Runs at startup,
    /// before the Vision Service starts and loads them. On Linux the root helper does this; a staged
    /// folder still here means it didn't run, and it's removed.</summary>
    private void ApplyStagedBackends()
    {
        if (!Directory.Exists(StagedBackendsPath)) return;
        if (!OperatingSystem.IsWindows())
        {
            log.LogWarning("Native backend libraries staged by the last update were never installed; see updater-*.log in {LogDir}.",
                LarisVMS.Core.LarisPaths.LogRoot);
            TryCleanStagedBackends();
            return;
        }

        var stampFile = Path.Combine(StagedBackendsPath, BackendsBundle.StampFileName);
        var expected = File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : null;
        if (expected is null || !string.Equals(BackendsBundle.ComputeStamp(StagedBackendsPath), expected, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("The staged native backend libraries changed since they were verified; not installing them.");
            TryCleanStagedBackends();
            return;
        }
        if (BackendsBundle.TrySwapIn(StagedBackendsPath, AppContext.BaseDirectory, out var error))
            log.LogInformation("Installed the native backend libraries from the last update.");
        else
        {
            log.LogWarning("Could not install the native backend libraries from the last update ({Error}); AI detection keeps its current ones.", error);
            TryCleanStagedBackends();
        }
    }

    private static HttpClientHandler CreateDefaultHandler()
    {
        // The same certificate check as the node's other calls to the server.
        if (NodeConfigStore.Trust is { } trust) return trust.CreateHandler();
        // No live check yet (only before startup creates one): the system's own validation.
        return new HttpClientHandler();
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

    private static void TryCleanStagedBackends()
    {
        try { if (File.Exists(StagedBackendsZip)) File.Delete(StagedBackendsZip); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
        try { if (Directory.Exists(StagedBackendsPath)) Directory.Delete(StagedBackendsPath, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

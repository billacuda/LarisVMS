using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Update;

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
///
/// On Linux the proxy runs as the unprivileged larisvms user, so it stages the verified binary and
/// writes a <see cref="LinuxUpdateRequest"/>; the root helper (larisvms-proxy-update.path, running
/// <c>LarisVMS.NodeUpdater --linux-apply --service larisvms-proxy</c>) installs it and restarts the unit.
/// </summary>
public class ProxyUpdateService(ProxyConfig config, ILogger log, IHostApplicationLifetime lifetime,
    HttpMessageHandler? httpHandler = null)
{
    // Its own folder, not the node's "update": a node and a proxy on one machine each recreate their
    // folder for every download, which would otherwise delete the other's staged files.
    internal static readonly string UpdateDir = Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "update-proxy");

    private static readonly string StagedPath = Path.Combine(UpdateDir, LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Proxy));

    // Published alongside LarisVMS.Proxy.exe by build-proxy.ps1 and copied into the same install
    // directory by install-proxy.ps1 — always sits next to the currently-running binary. Reuses the
    // recorder node's updater verbatim: it is already service-name-parameterised (--service), and its
    // wait-for-stopped / swap / start-again / re-apply-failure-recovery steps are all generic sc.exe.
    private static readonly string UpdaterPath = Path.Combine(AppContext.BaseDirectory, LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Updater));

    private const string ServiceName = "LarisVMSProxy";

    /// <summary>The systemd unit on Linux (larisvms-proxy.service).</summary>
    public const string LinuxServiceName = "larisvms-proxy";

    /// <summary>Guards against <c>ProxyWorker</c>'s loop re-triggering an update while one is already
    /// downloading/verifying. Left true after a successful <see cref="ApplyWindows"/> launch
    /// deliberately — this process is about to be stopped by the SCM, so there is no "next cycle" to
    /// protect against. Same reasoning as the node's UpdateService.IsApplying.</summary>
    public bool IsApplying { get; private set; }

    private bool _loggedContainer;

    public async Task<bool> TryApplyAsync(ProxyUpdateInfoDto update, CancellationToken ct)
    {
        // Same as the node's UpdateService: a container is updated by pulling the new image.
        if (LarisVMS.Core.RunningInContainer.Value)
        {
            if (!_loggedContainer)
                log.LogInformation("Update {Version} is available. This proxy runs in a container: update it by pulling the new image.", update.Version);
            _loggedContainer = true;
            return false;
        }

        if (IsApplying)
        {
            log.LogInformation("An update is already in progress — ignoring.");
            return false;
        }

        IsApplying = true;

        try
        {
            log.LogInformation("Proxy update available: {Version}. Downloading...", update.Version);
            // Fresh, locked-down folder for each download: a file or folder a local user planted here
            // beforehand (and its owner, who could rewrite its permissions) doesn't survive into the
            // binary that's about to be installed and run as a service.
            LarisVMS.Core.Security.LocalDataAcl.RecreateProtectedDirectory(UpdateDir);

            // httpHandler is a test-only seam (a fake HttpMessageHandler returning canned bytes) —
            // production callers never pass one. disposeHandler:false when injected, since a test owns
            // and may reuse it; true when this created its own.
            using var http = httpHandler is not null
                ? new HttpClient(httpHandler, disposeHandler: false)
                : new HttpClient(CreateDefaultHandler());
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{config.ProxyId}:{config.Secret}");

            using var response = await http.GetAsync(LarisVMS.Core.Security.UpdateDownload.Resolve(config.ServerUrl, update.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, ct);
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
            if (OperatingSystem.IsLinux())
                ApplyLinux(update);
            else
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

    /// <summary>Linux: the verified binary is staged; the root helper installs it and restarts this
    /// service (see <see cref="LinuxUpdateRequest"/>).</summary>
    private void ApplyLinux(ProxyUpdateInfoDto update)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(StagedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        new LinuxUpdateRequest
        {
            Platform = LarisVMS.Core.PlatformId.ForProxy(LarisVMS.Core.PlatformId.Current),
            Version = update.Version,
            Sha256 = update.Sha256,
            PublisherSignature = update.PublisherSignature,
        }.WriteTo(UpdateDir);
        log.LogInformation("Update {Version} staged; the update helper (larisvms-proxy-update) installs it and restarts this service.", update.Version);
        _ = GiveUpIfNotAppliedAsync(update.Version);
    }

    /// <summary>When nothing picks the request up (the helper's units aren't installed), says so and
    /// lets the next offer try again.</summary>
    private async Task GiveUpIfNotAppliedAsync(string version)
    {
        await Task.Delay(TimeSpan.FromMinutes(5));
        var request = Path.Combine(UpdateDir, LinuxUpdateRequest.FileName);
        if (!File.Exists(request)) return;
        log.LogError("Update {Version} was staged 5 minutes ago but the update helper never installed it. Check that " +
            "larisvms-proxy-update.path is enabled (systemctl status larisvms-proxy-update.path) and see updater-*.log in {LogDir}.",
            version, LarisVMS.Core.LarisPaths.LogRoot);
        try { File.Delete(request); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* replaced by the next attempt */ }
        TryCleanStaged();
        IsApplying = false;
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
            ?? Path.Combine(AppContext.BaseDirectory, LarisVMS.Core.ExecutableName.For(LarisVMS.Core.ExecutableName.Proxy));

        // Swap while still running (see InPlaceBinarySwap), so the updater only has to restart the
        // service. Falls back to the updater doing the swap after the stop if the rename fails.
        string arguments;
        if (InPlaceBinarySwap.TrySwap(StagedPath, currentBinary, out var swapError))
        {
            log.LogInformation("Swapped in the new proxy binary; restarting the service to run it.");
            arguments = $"--restart-only --service {ServiceName}";
        }
        else
        {
            log.LogWarning("Could not swap the proxy binary in place ({Error}); handing the swap to the updater after the service stops.", swapError);
            arguments = $"--new \"{StagedPath}\" --current \"{currentBinary}\" --service {ServiceName}";
        }

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

    /// <summary>Run once at startup: removes the "*.old" binary an in-place swap left behind, and
    /// reports in this proxy's own log an update that was downloaded but never applied.</summary>
    public void CheckLastUpdate()
    {
        InPlaceBinarySwap.CleanUp(AppContext.BaseDirectory);
        if (!File.Exists(StagedPath)) return;

        log.LogWarning("A proxy update downloaded {When:u} was never applied (the staged binary is still at {Path}). " +
            "See updater-*.log in {LogDir} for why. It will be downloaded again on the next update offer.",
            File.GetLastWriteTimeUtc(StagedPath), StagedPath,
            LarisVMS.Core.LarisPaths.LogRoot);
        try { File.Delete(StagedPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* replaced by the next download */ }
    }

    private static HttpClientHandler CreateDefaultHandler()
    {
        // The same certificate check as the proxy's other calls to the server.
        if (ProxyConfigStore.Trust is { } trust) return trust.CreateHandler();
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
}

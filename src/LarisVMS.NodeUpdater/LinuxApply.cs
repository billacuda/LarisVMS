using System.Diagnostics;
using System.Security.Cryptography;
using LarisVMS.NodeUpdater.Shared;

namespace LarisVMS.NodeUpdater;

/// <summary>
/// <c>LarisVMS.NodeUpdater --linux-apply --service larisvms-node|larisvms-proxy</c>: the root half of a
/// Linux update. The node or proxy runs as the unprivileged larisvms user, verifies an update, stages
/// it in its update folder, and writes a <see cref="LinuxUpdateRequest"/>; a systemd path unit sees the
/// request and runs this as root (larisvms-node-update.service / larisvms-proxy-update.service).
///
/// The staging folder is writable by the larisvms user, so nothing in it is trusted as-is:
/// <list type="bullet">
///   <item>Which unit to restart and which files may be installed come from the unit's command line
///   (<see cref="ServiceFor"/>), never from the request. The updater itself is never replaced.</item>
///   <item>Each file is copied into a root-only folder first, and that copy is what's checked (its
///   SHA-256, or the backend tree's stamp) and installed, so a staged file swapped after the check
///   can't get in. Symbolic links are refused.</item>
///   <item>A build made with the publisher key (PublisherKey) also checks the publisher's signatures
///   here, so a compromised larisvms account can't install code of its own; the binaries then run as
///   root through <c>sudo larisvms-node setup</c>. A build without the key (from source) trusts the
///   request's hashes, as its node already trusts the server's signature alone.</item>
/// </list>
/// </summary>
internal static class LinuxApply
{
    internal sealed record Service(string Unit, string UpdateFolder, string Program, bool HasVision, string Platform);

    /// <summary>The services this may update. Anything else is refused.</summary>
    internal static Service? ServiceFor(string? unit) => unit switch
    {
        "larisvms-node" => new("larisvms-node", "update", ExecutableName.Node, HasVision: true, PlatformId.Current),
        "larisvms-proxy" => new("larisvms-proxy", "update-proxy", ExecutableName.Proxy, HasVision: false, PlatformId.ForProxy(PlatformId.Current)),
        _ => null,
    };

    private const string StagingFolderName = ".update-staging";

    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode RootOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static int Run(string? unit, Action<string, string> log)
    {
        if (OperatingSystem.IsWindows())
        {
            log("ERROR", "--linux-apply is for Linux.");
            return 1;
        }
        var service = ServiceFor(unit);
        return Run(service, Path.Combine(LarisPaths.DataRoot, service?.UpdateFolder ?? "update"), AppContext.BaseDirectory, log, Restart);
    }

    /// <summary>Folders and the restart are parameters so tests can run it against a temp tree.</summary>
    internal static int Run(Service? service, string updateDir, string installDir, Action<string, string> log,
        Func<string, Action<string, string>, bool> restart)
    {
        if (service is null)
        {
            log("ERROR", "--linux-apply needs --service larisvms-node or --service larisvms-proxy.");
            return 1;
        }

        var requestPath = Path.Combine(updateDir, LinuxUpdateRequest.FileName);
        if (!IsRegularFile(requestPath))
        {
            log("INFO", $"No update request at {requestPath}; nothing to do.");
            return 0;
        }

        LinuxUpdateRequest? request;
        try
        {
            request = LinuxUpdateRequest.FromJson(File.ReadAllText(requestPath));
        }
        finally
        {
            // Read once: a request left behind would trigger the path unit again and again.
            TryDeleteFile(requestPath);
        }
        if (request is null)
        {
            log("ERROR", $"The update request at {requestPath} couldn't be read; ignored.");
            return 1;
        }

        if (request.RestartOnly)
        {
            log("INFO", $"Restart requested for {service.Unit}.");
            return restart(service.Unit, log) ? 0 : 1;
        }

        bool installed;
        var staging = Path.Combine(installDir, StagingFolderName);
        try
        {
            RecreateRootOnly(staging);
            installed = Install(service, request, updateDir, installDir, staging, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("ERROR", $"Update failed: {ex.Message}");
            installed = false;
        }
        finally
        {
            TryDeleteDirectory(staging);
            CleanStaged(service, updateDir);
        }

        if (!installed)
        {
            log("ERROR", $"Update {request.Version} was not installed; {service.Unit} keeps running its current build.");
            return 1;
        }
        return restart(service.Unit, log) ? 0 : 1;
    }

    private static bool Install(Service service, LinuxUpdateRequest request, string updateDir, string installDir, string staging,
        Action<string, string> log)
    {
        if (request.Platform != service.Platform || request.Version is null || request.Sha256 is null)
        {
            log("ERROR", $"The update request is for '{request.Platform}' (version {request.Version}), not {service.Platform}; ignored.");
            return false;
        }

        var payload = UpdateSignature.UpdatePayload(service.Platform, request.Version, request.Sha256, request.VisionSha256);
        if (!PublisherKey.Check(payload, request.PublisherSignature, out var publisherError))
        {
            log("ERROR", $"Not installing update {request.Version}: {publisherError}.");
            return false;
        }
        log("INFO", PublisherKey.PublicKeyPem is null
            ? $"Installing update {request.Version} (a build without the publisher key: checked against the request's hashes)."
            : $"Installing update {request.Version} (publisher signature verified).");

        if (!InstallFile(Path.Combine(updateDir, service.Program), Path.Combine(installDir, service.Program), request.Sha256, staging, log))
            return false;

        if (!service.HasVision) return true;

        // Like Windows: only keep an already-installed Vision Service current.
        var visionTarget = Path.Combine(installDir, ExecutableName.VisionService);
        var visionStaged = Path.Combine(updateDir, ExecutableName.VisionService);
        if (request.VisionSha256 is not null && File.Exists(visionStaged) && File.Exists(visionTarget)
            && !InstallFile(visionStaged, visionTarget, request.VisionSha256, staging, log))
        {
            log("WARN", "The Vision Service binary wasn't updated; AI detection keeps its previous one.");
        }

        if (request.BackendsStamp is not null && File.Exists(visionTarget))
            InstallBackends(service, request, request.BackendsStamp, Path.Combine(updateDir, BackendsBundle.StagedFolderName), installDir, staging, log);
        return true;
    }

    /// <summary>Copies <paramref name="staged"/> into the root-only staging folder, checks the copy's
    /// hash, and renames it over <paramref name="target"/> (a running program keeps its old file).</summary>
    private static bool InstallFile(string staged, string target, string sha256, string staging, Action<string, string> log)
    {
        if (!IsRegularFile(staged))
        {
            log("ERROR", $"{staged} is missing or isn't a regular file.");
            return false;
        }
        var copy = Path.Combine(staging, Path.GetFileName(target));
        File.Copy(staged, copy, overwrite: true);
        var actual = Sha256Of(copy);
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            log("ERROR", $"{Path.GetFileName(target)}: checksum mismatch (expected {sha256}, got {actual}).");
            return false;
        }
        SetMode(copy, Executable);
        File.Move(copy, target, overwrite: true);
        log("INFO", $"Installed {target}.");
        return true;
    }

    private static void InstallBackends(Service service, LinuxUpdateRequest request, string stamp, string staged, string installDir,
        string staging, Action<string, string> log)
    {
        var payload = UpdateSignature.BackendsPayload(service.Platform, request.Version!, stamp);
        if (!PublisherKey.Check(payload, request.BackendsPublisherSignature, out var publisherError))
        {
            log("WARN", $"Native backend libraries not installed: {publisherError}.");
            return;
        }
        if (!Directory.Exists(staged) || new DirectoryInfo(staged).LinkTarget is not null
            || Directory.EnumerateFileSystemEntries(staged, "*", SearchOption.AllDirectories).Any(e => new FileInfo(e).LinkTarget is not null))
        {
            log("WARN", "Native backend libraries not installed: the staged folder is missing or contains links.");
            return;
        }

        var copy = Path.Combine(staging, BackendsBundle.FolderName);
        BackendsBundle.CopyTree(staged, copy);
        if (!string.Equals(BackendsBundle.ComputeStamp(copy), stamp, StringComparison.OrdinalIgnoreCase))
        {
            log("WARN", "Native backend libraries not installed: they don't match their stamp.");
            return;
        }
        File.WriteAllText(Path.Combine(copy, BackendsBundle.StampFileName), stamp.ToLowerInvariant());
        SetMode(copy, Executable);
        foreach (var dir in Directory.EnumerateDirectories(copy, "*", SearchOption.AllDirectories)) SetMode(dir, Executable);
        foreach (var file in Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories)) SetMode(file, Readable);

        if (BackendsBundle.TrySwapIn(copy, installDir, out var error))
        {
            log("INFO", "Installed the native backend libraries.");
            HandCudaFolderToServiceUser(Path.Combine(installDir, BackendsBundle.FolderName, "cuda"), log);
        }
        else
            log("WARN", $"Native backend libraries not installed: {error}.");
    }

    /// <summary>The node downloads the ~300 MB CUDA provider into the cuda folder itself (ONNX Runtime
    /// loads it from beside libonnxruntime.so), as the larisvms user, so that one folder belongs to it,
    /// as the package's install script sets it up. Only the Vision Service, which also runs as larisvms,
    /// loads anything from it.</summary>
    private static void HandCudaFolderToServiceUser(string cudaDir, Action<string, string> log)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(cudaDir)) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("chown") { ArgumentList = { "larisvms:larisvms", cudaDir }, UseShellExecute = false })!;
            if (!process.WaitForExit(10_000) || process.ExitCode != 0)
                log("WARN", $"Could not give {cudaDir} to the larisvms user; the node can't download the CUDA provider into it.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log("WARN", $"Could not run chown: {ex.Message}");
        }
    }

    private static bool Restart(string unit, Action<string, string> log)
    {
        try
        {
            // --no-block: the restart runs after this oneshot unit finishes, not inside it.
            using var process = Process.Start(new ProcessStartInfo("systemctl") { ArgumentList = { "--no-block", "restart", unit }, UseShellExecute = false })!;
            if (!process.WaitForExit(60_000) || process.ExitCode != 0)
            {
                log("ERROR", $"systemctl restart {unit} failed; restart it by hand (sudo systemctl restart {unit}).");
                return false;
            }
            log("INFO", $"Restarting {unit}.");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log("ERROR", $"Could not run systemctl: {ex.Message}");
            return false;
        }
    }

    private static void CleanStaged(Service service, string updateDir)
    {
        TryDeleteFile(Path.Combine(updateDir, service.Program));
        if (!service.HasVision) return;
        TryDeleteFile(Path.Combine(updateDir, ExecutableName.VisionService));
        TryDeleteDirectory(Path.Combine(updateDir, BackendsBundle.StagedFolderName));
    }

    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null;
    }

    private static void RecreateRootOnly(string path)
    {
        TryDeleteDirectory(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, RootOnly);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

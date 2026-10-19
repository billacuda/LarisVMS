using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Security;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Node;

/// <summary>
/// Downloads <c>onnxruntime_providers_cuda.dll</c> (~320 MB) from the server the first time this node
/// resolves the CUDA backend, and drops it into <c>onnxruntime-backends\cuda\</c> next to the small
/// CUDA files the package does bundle. Shipping it in every node package would add that much to every
/// CPU/DirectML-only install for a file only NVIDIA nodes ever load — so, like the YOLOX models, it's
/// fetched on demand and cached.
///
/// Only acts on a node that resolved <c>AiAccelerator.Nvidia</c> AND has the CUDA Toolkit installed
/// (no point pulling 320 MB onto a box that will run DirectML anyway). Until the file lands the Vision
/// Service resolves to DirectML; once <see cref="ConsumeJustProvisioned"/> reports a fresh download,
/// NodeWorker restarts the Vision Service so it switches to CUDA. Every failure is non-fatal and
/// retried on the next reconcile.
/// </summary>
public sealed class CudaProviderProvisioner(string installDirectory, NodeApiClient api, ILogger<CudaProviderProvisioner> logger)
{
    private const string ServerName = "cuda-provider";
    private const string FileName = "onnxruntime_providers_cuda.dll";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _present;
    private volatile bool _justProvisioned;
    private bool _warnedNoToolkit;
    private bool _warnedNoServerCopy;

    private string CudaBackendDir => Path.Combine(installDirectory, "onnxruntime-backends", "cuda");
    private string TargetPath => Path.Combine(CudaBackendDir, FileName);

    /// <summary>Reads and clears the "just downloaded it" flag — NodeWorker restarts the Vision
    /// Service when this returns true so it picks up CUDA instead of the DirectML it started on.</summary>
    public bool ConsumeJustProvisioned()
    {
        if (!_justProvisioned) return false;
        _justProvisioned = false;
        return true;
    }

    /// <summary>Idempotent and non-blocking to call every reconcile — a fast no-op once the provider
    /// is in place or a download is already running.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (!_gate.Wait(0)) return;
        try
        {
            EnsureRuntimeLibraries();
            if (_present) return;

            if (File.Exists(TargetPath) && new FileInfo(TargetPath).Length > 0)
            {
                // The download path SHA-verifies before moving into place, so an already-present
                // non-empty file is trusted.
                _present = true;
                return;
            }

            if (!CudaToolkitPresent())
            {
                if (!_warnedNoToolkit)
                {
                    _warnedNoToolkit = true;
                    logger.LogInformation(
                        "This node resolved the NVIDIA accelerator but the CUDA Toolkit runtime (cudart64_12.dll) is " +
                        "not installed — not downloading the ~320 MB CUDA provider library. Install CUDA Toolkit 12.x " +
                        "+ cuDNN 9.x (see install-node.ps1); it downloads automatically once present. Running DirectML meanwhile.");
                }
                return;
            }
            _warnedNoToolkit = false;

            VisionNativeInfo? info;
            try
            {
                info = await api.GetVisionNativeInfoAsync(ServerName, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not ask the server for the CUDA provider library — will retry.");
                return;
            }

            if (info is null)
            {
                if (!_warnedNoServerCopy)
                {
                    _warnedNoServerCopy = true;
                    logger.LogWarning(
                        "The server has no CUDA provider library seeded (deploy.ps1 seeds it from the build's " +
                        "cuda-provider\\ folder). This node runs DirectML until it's seeded.");
                }
                return;
            }
            _warnedNoServerCopy = false;

            // This library is loaded by the Vision Service: only a copy signed with the server's key
            // is installed, once the node has that key (registration, or its first signed update).
            if (NodeConfigStore.Load()?.UpdateSigningKeyPem is { } pinnedKey
                && !UpdateSignature.Check(pinnedKey, info.SigningKeyPem, info.Signature,
                    UpdateSignature.NativePayload(info.Name, info.Sha256), out _, out var signatureError))
            {
                logger.LogError("Not installing the CUDA provider library: {Reason}.", signatureError);
                return;
            }

            Directory.CreateDirectory(CudaBackendDir);
            var tmp = TargetPath + ".download";
            logger.LogInformation("Downloading the CUDA provider library ({Mb} MB) from the server…", info.SizeBytes / (1024 * 1024));
            try
            {
                await using (var src = await api.OpenVisionNativeStreamAsync(ServerName, ct))
                await using (var dst = File.Create(tmp))
                {
                    await src.CopyToAsync(dst, ct);
                }

                var sha = await ComputeSha256Async(tmp, ct);
                if (!string.Equals(sha, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"SHA-256 mismatch (expected {info.Sha256}, got {sha}).");

                File.Move(tmp, TargetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { /* best effort */ }
                }
            }

            _present = true;
            _justProvisioned = true;
            logger.LogInformation("CUDA provider library in place ({Path}) — the Vision Service will use CUDA.", TargetPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not download the CUDA provider library — will retry on the next reconcile.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // The CUDA Toolkit / cuDNN libraries the CUDA backend loads, as install-node.ps1 and the MSI copy
    // them next to it. cudnn64_9 is a dispatcher for the other cudnn_*64_9 files beside it, so all of
    // them come along.
    internal static readonly string[] RuntimeLibraries =
        ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll", "cudnn64_9.dll"];

    private DateTime _nextRuntimeCheckUtc = DateTime.MinValue;
    private string? _lastRuntimeMissing;

    /// <summary>Puts back the CUDA/cuDNN runtime libraries next to the CUDA backend when they've gone
    /// missing (a reinstall or folder replacement clears them, which silently drops AI detection to
    /// DirectML) and they're on this machine somewhere: the CUDA Toolkit, a cuDNN install, or a pip
    /// package. Checked at most every 10 minutes; restarts the Vision Service when it copied anything.</summary>
    private void EnsureRuntimeLibraries()
    {
        if (DateTime.UtcNow < _nextRuntimeCheckUtc) return;
        _nextRuntimeCheckUtc = DateTime.UtcNow.AddMinutes(10);
        try
        {
            if (!Directory.Exists(CudaBackendDir)) return;
            var copied = CopyMissingRuntime(CudaBackendDir, RuntimeSourceDirectories(), Loadable, out var stillMissing);
            if (copied.Count > 0)
            {
                logger.LogInformation("Copied the CUDA runtime libraries the AI backend needs from this machine's install: {Files}.",
                    string.Join(", ", copied));
                _justProvisioned = true;
            }
            var missing = string.Join(", ", stillMissing);
            if (stillMissing.Count > 0 && missing != _lastRuntimeMissing)
                logger.LogWarning(
                    "AI detection can't use CUDA: {Missing} isn't on this node. Install the CUDA Toolkit 12.x and cuDNN 9.x for CUDA 12 " +
                    "(or pip install nvidia-cudnn-cu12); it's picked up automatically within 10 minutes. Running DirectML meanwhile.", missing);
            _lastRuntimeMissing = stillMissing.Count > 0 ? missing : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not check the CUDA runtime libraries — will retry.");
        }
    }

    private static bool Loadable(string dll)
    {
        try
        {
            if (!NativeLibrary.TryLoad(Path.GetFileNameWithoutExtension(dll), out var handle)) return false;
            NativeLibrary.Free(handle);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Copies each library in <see cref="RuntimeLibraries"/> that is neither in
    /// <paramref name="cudaDir"/> nor loadable from PATH from the first source folder that has it
    /// (every cudnn*.dll alongside, for cuDNN). Returns the files copied; <paramref name="stillMissing"/>
    /// lists those found nowhere.</summary>
    internal static List<string> CopyMissingRuntime(string cudaDir, IEnumerable<string> sources, Func<string, bool> loadable,
        out List<string> stillMissing)
    {
        var sourceList = sources.ToList();
        var copied = new List<string>();
        stillMissing = [];
        foreach (var dll in RuntimeLibraries)
        {
            if (File.Exists(Path.Combine(cudaDir, dll)) || loadable(dll)) continue;
            var source = sourceList.FirstOrDefault(d => File.Exists(Path.Combine(d, dll)));
            if (source is null) { stillMissing.Add(dll); continue; }
            var pattern = dll.StartsWith("cudnn", StringComparison.OrdinalIgnoreCase) ? "cudnn*.dll" : dll;
            foreach (var file in Directory.EnumerateFiles(source, pattern))
            {
                File.Copy(file, Path.Combine(cudaDir, Path.GetFileName(file)), overwrite: true);
                copied.Add(Path.GetFileName(file));
            }
        }
        return copied;
    }

    /// <summary>Where the CUDA Toolkit and cuDNN install themselves, newest first: the toolkit, NVIDIA's
    /// standalone cuDNN, and pip packages in a machine-wide Python.</summary>
    private static IEnumerable<string> RuntimeSourceDirectories()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        static IEnumerable<string> Sub(string dir, string pattern) =>
            Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, pattern).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase) : [];

        foreach (var v in Sub(Path.Combine(programFiles, @"NVIDIA GPU Computing Toolkit\CUDA"), "v*"))
            yield return Path.Combine(v, "bin");
        foreach (var v in Sub(Path.Combine(programFiles, @"NVIDIA\CUDNN"), "v*"))
        {
            foreach (var sub in Sub(Path.Combine(v, "bin"), "*")) yield return sub;
            yield return Path.Combine(v, "bin");
        }
        foreach (var python in Sub(programFiles, "Python*"))
            foreach (var package in Sub(Path.Combine(python, @"Lib\site-packages\nvidia"), "*"))
                yield return Path.Combine(package, "bin");
    }

    private bool CudaToolkitPresent()
    {
        // install-node.ps1 copies the toolkit's runtime DLLs here when it finds them.
        if (File.Exists(Path.Combine(CudaBackendDir, "cudart64_12.dll"))) return true;

        try
        {
            if (NativeLibrary.TryLoad("cudart64_12", out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }
        catch
        {
            // Treat any load failure as "not available".
        }
        return false;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }
}

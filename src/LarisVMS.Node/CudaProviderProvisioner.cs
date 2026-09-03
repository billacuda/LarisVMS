using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LarisVMS.Core.Dtos;
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
        if (_present) return;
        if (!_gate.Wait(0)) return;
        try
        {
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

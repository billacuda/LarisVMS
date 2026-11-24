using System.Security.Cryptography;
using System.Text;

// LarisVMS.NodeUpdater compiles this same file (it references no other project) under its own
// namespace, so a project that references both (the tests) doesn't see the type twice.
#if LARISVMS_NODEUPDATER
namespace LarisVMS.NodeUpdater.Shared;
#else
namespace LarisVMS.Core.Update;
#endif

/// <summary>
/// The Vision Service's native backend libraries (<c>onnxruntime-backends/</c>: the CUDA, DirectML,
/// and OpenVINO builds of ONNX Runtime), shipped with node updates so a pushed update keeps them in
/// step with the Vision Service binary instead of only an install doing so.
///
/// A build stamps the tree: <see cref="ComputeStamp"/> hashes every file in it (path and content),
/// written to <see cref="StampFileName"/> inside the folder. A node downloads the bundle only when the
/// offered stamp differs from its own, and checks the unpacked tree against the signed stamp before
/// using it. <c>build-node.ps1</c> computes the same stamp and must stay byte-for-byte in step with
/// <see cref="ComputeStamp"/>.
/// </summary>
public static class BackendsBundle
{
    public const string FolderName = "onnxruntime-backends";
    public const string StampFileName = ".stamp";

    /// <summary>The unpacked, verified tree waiting to be swapped in, in the update folder.</summary>
    public const string StagedFolderName = FolderName + ".new";

    public const string OldFolderName = FolderName + ".old";

    /// <summary>The bundle in a node package and on the server.</summary>
    public const string ZipFileName = FolderName + ".zip";

    /// <summary>Beside the zip in a node package: the stamp, so the server needn't unpack it.</summary>
    public const string StampTextFileName = "backends-stamp.txt";

    /// <summary>Beside the zip in a node package: the publisher's signature over
    /// <c>UpdateSignature.BackendsPayload</c>.</summary>
    public const string PublisherSignatureFileName = "backends.publisher.sig";

    /// <summary>Lowercase hex SHA-256 over one "relative/path\tsha256\n" line per file, sorted
    /// ordinally by path, with '/' separators on every OS; the stamp file itself is left out.</summary>
    public static string ComputeStamp(string directory)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative == StampFileName) continue;
            using var stream = File.OpenRead(file);
            lines.Add(relative + "\t" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() + "\n");
        }
        lines.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(lines)))).ToLowerInvariant();
    }

    /// <summary>The stamp of the tree installed in <paramref name="installDirectory"/>, or null when it
    /// has none (an install made before stamps, or no Vision Service).</summary>
    public static string? InstalledStamp(string installDirectory)
    {
        try
        {
            // installDirectory is the program's own folder (AppContext.BaseDirectory), never user input.
            var path = Path.Combine(installDirectory, FolderName, StampFileName); // nosemgrep: csharp.lang.security.filesystem.unsafe-path-combine.unsafe-path-combine
            return File.Exists(path) ? File.ReadAllText(path).Trim().ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Replaces <paramref name="installDirectory"/>'s backend folder with
    /// <paramref name="staged"/>: the current one is moved aside, the staged one moved in (copied when
    /// it's on another volume), and the old one deleted. If moving the staged one in fails, the old
    /// one is put back. The Vision Service must not be running, since it holds these libraries open.</summary>
    public static bool TrySwapIn(string staged, string installDirectory, out string? error)
    {
        var current = Path.Combine(installDirectory, FolderName);
        var old = Path.Combine(installDirectory, OldFolderName);
        try
        {
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            if (Directory.Exists(current)) Directory.Move(current, old);
            try
            {
                MoveOrCopy(staged, current);
            }
            catch
            {
                if (Directory.Exists(current)) Directory.Delete(current, recursive: true);
                if (Directory.Exists(old)) Directory.Move(old, current);
                throw;
            }
            TryDelete(old);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes a backend folder left by an earlier swap whose delete failed.</summary>
    public static void CleanUp(string installDirectory) => TryDelete(Path.Combine(installDirectory, OldFolderName));

    private static void MoveOrCopy(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (Directory.Exists(source) && !Directory.Exists(destination))
        {
            // Across volumes (the update folder on C:, the program on D:): copy, then remove the source.
            CopyTree(source, destination);
            TryDelete(source);
        }
    }

    public static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* retried by CleanUp */ }
    }
}

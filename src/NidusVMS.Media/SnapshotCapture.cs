using System.Diagnostics;

namespace NidusVMS.Media;

/// <summary>
/// One-shot still-frame grab from a camera's own RTSP stream — no relation to RecordingSession's
/// long-lived tee (a separate, short RTSP session opened on demand and closed immediately after).
/// Built as an M8 prerequisite (the zone editor needs a background image to draw polygons on) but
/// also closes an M5 backlog item ("snapshot/still capture") that was never implemented — one
/// capture path serves both, so there's no reason to build it twice later.
/// </summary>
public static class SnapshotCapture
{
    /// <summary>Returns JPEG bytes, or null if ffmpeg produced nothing (camera unreachable, RTSP
    /// auth failure, timeout) — callers turn that into a 502/504 rather than this class deciding
    /// what an HTTP failure should look like.</summary>
    public static async Task<byte[]?> CaptureAsync(string ffmpegPath, string rtspUri, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        string[] args =
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "5000000",
            "-i", rtspUri,
            "-frames:v", "1",
            "-q:v", "3",
            "-f", "image2",
            "pipe:1"
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        // A single-frame grab that never terminates (camera accepts the connection but never sends
        // a keyframe, or hangs) would otherwise hold this process — and the browser request behind
        // it — open indefinitely. Bounded independently of the RTSP -timeout flag above, which only
        // covers the *connection* attempt, not a stall after it succeeds.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        // Drain stderr concurrently — StandardError has its own OS pipe buffer, and ffmpeg logs
        // enough there that leaving it unread while waiting on stdout risks a deadlock the same way
        // RecordingSession's stderr drain exists to avoid.
        var stderrDrain = process.StandardError.ReadToEndAsync(CancellationToken.None);

        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeoutCts.Token);
            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired, not the caller's token — a stuck grab is killed and reported
            // as "nothing captured" rather than left running.
            TryKill(process);
            return null;
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await stderrDrain;
        }

        return bytes.Length > 0 ? bytes : null;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }
}

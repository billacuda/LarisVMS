using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the fix for a real, confirmed data-loss bug: RecordingSession.PollForCompletedSegments
/// rescans a camera's *entire* on-disk history every time RunAsync starts, with no memory of what
/// was reported in a previous process's lifetime. A node process restart (not just an ffmpeg
/// reconnect, which the in-session reportedPaths set already survives) used to re-fire
/// SegmentCompleted for every old file all over again — and for a Motion-mode camera, a
/// freshly-restarted MotionSession/CameraEventSession has observed no motion yet at that instant, so
/// nearly all of that re-fired history looked like "no motion" and was wrongly discarded, deleting
/// files that already had valid Segments rows from a previous run. RunAsync's knownPaths parameter
/// (pre-seeding reportedPaths from GET /api/nodes/segments/paths, fetched once by NodeWorker before
/// any session starts) is the fix; this exercises the actual skip behavior against a real directory.
/// </summary>
public class RecordingSessionKnownPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LarisVMSRecordingSessionKnownPathsTests_" + Guid.NewGuid());

    public RecordingSessionKnownPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteSegmentFile(string name, DateTime creationTimeUtc)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3, 4]); // non-empty — a 0-byte file is a known failure artifact, not a segment
        File.SetCreationTimeUtc(path, creationTimeUtc);
        return path;
    }

    private string WriteSegmentFileWithSize(string name, DateTime creationTimeUtc, int sizeBytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetCreationTimeUtc(path, creationTimeUtc);
        return path;
    }

    private static void GrowFile(string path, int totalSizeBytes) => File.WriteAllBytes(path, new byte[totalSizeBytes]);

    private static RecordingSession NewSession(string outputDirectory) =>
        new(new RecordingSessionOptions("ffmpeg-unused", "rtsp://unused", outputDirectory), NullLogger.Instance);

    [Fact]
    public void RescanSkipsAFilePreSeededAsAlreadyKnownButFiresForANewOne()
    {
        var t0 = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var known = WriteSegmentFile("a-known.mp4", t0);
        var unknown = WriteSegmentFile("b-unknown.mp4", t0.AddMinutes(1));
        // A third, newer file so both a/b are "closed" per PollForCompletedSegments' own rule (every
        // file except the last is guaranteed closed) — this one is never itself reported by this call.
        WriteSegmentFile("c-still-open.mp4", t0.AddMinutes(2));

        var session = NewSession(_root);
        var fired = new List<string>();
        session.SegmentCompleted += seg => fired.Add(seg.FilePath);

        // Pre-seeded exactly the way NodeWorker seeds RunAsync's reportedPaths from the server's
        // already-known-paths response — "known" must never fire SegmentCompleted again.
        var reportedPaths = new HashSet<string>([known], StringComparer.OrdinalIgnoreCase);
        session.PollForCompletedSegments(reportedPaths, []);

        Assert.DoesNotContain(known, fired);
        Assert.Contains(unknown, fired);
    }

    [Fact]
    public void WithNoPreSeedingEveryClosedFileFiresLikeBeforeThisFix()
    {
        // Confirms the fix is additive, not a behavior change for a genuinely fresh camera directory
        // (nothing pre-known) — every closed file still gets reported exactly as before.
        var t0 = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var a = WriteSegmentFile("a.mp4", t0);
        var b = WriteSegmentFile("b.mp4", t0.AddMinutes(1));
        WriteSegmentFile("c-still-open.mp4", t0.AddMinutes(2));

        var session = NewSession(_root);
        var fired = new List<string>();
        session.SegmentCompleted += seg => fired.Add(seg.FilePath);

        session.PollForCompletedSegments([], []);

        Assert.Contains(a, fired);
        Assert.Contains(b, fired);
    }

    [Fact]
    public void ASecondPollNeverRefiresAFileAlreadyReportedInThisSameRun()
    {
        // The pre-existing in-session protection (reportedPaths persists across ffmpeg reconnects
        // within one RunAsync) must still work unchanged — this fix only adds pre-seeding, it
        // doesn't touch the existing "don't re-add" behavior of the set itself.
        var t0 = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var a = WriteSegmentFile("a.mp4", t0);
        var b = WriteSegmentFile("b-still-open.mp4", t0.AddMinutes(1));

        var session = NewSession(_root);
        var fired = new List<string>();
        session.SegmentCompleted += seg => fired.Add(seg.FilePath);
        var reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRealDataObservedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc); // a.mp4 fires, b is still "open" (last file)
        WriteSegmentFile("c-now-open.mp4", t0.AddMinutes(2)); // b.mp4 is now closed too
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc); // b.mp4 fires; a.mp4 must not fire again

        Assert.Equal([a, b], fired);
    }

    // ── First-real-data start time (timeline/video mismatch fix) ────────────
    // FileInfo.CreationTimeUtc is the instant ffmpeg opens/truncates the output file — which can be
    // well before the first real frame lands, since a full RTSP handshake/negotiation happens on the
    // first segment after any (re)connect. Every timeline position computed downstream from that
    // timestamp then reads as earlier than the true content by exactly that gap. These cover
    // PollForCompletedSegments' fix: prefer the wall-clock moment a still-open file is first observed
    // past the header-only threshold, falling back to CreationTimeUtc only when a segment was never
    // caught mid-growth.

    [Fact]
    public void OpenFileGrowingPastThresholdUsesFirstObservedInstantNotCreationTime()
    {
        var creationTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var path = WriteSegmentFileWithSize("a.mp4", creationTime, sizeBytes: 500); // header-only, below threshold

        var session = NewSession(_root);
        var fired = new List<RecordingSegment>();
        session.SegmentCompleted += fired.Add;
        var reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRealDataObservedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Still header-only and still the only (open/last) file — nothing reported, and not yet
        // recorded as "seen with real data" since it hasn't crossed the threshold.
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);
        Assert.Empty(fired);
        Assert.DoesNotContain(path, firstRealDataObservedUtc.Keys);

        // Real content lands — grow past the threshold. Still the only file, so still "open"/last.
        GrowFile(path, totalSizeBytes: 8192);
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);
        Assert.Contains(path, firstRealDataObservedUtc.Keys);
        var observedAt = firstRealDataObservedUtc[path];

        // A newer file supersedes it — now it's reported as completed.
        WriteSegmentFileWithSize("b-now-open.mp4", creationTime.AddMinutes(1), sizeBytes: 8192);
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);

        var segment = Assert.Single(fired);
        Assert.Equal(path, segment.FilePath);
        Assert.Equal(observedAt, segment.StartUtc);
        Assert.NotEqual(creationTime, segment.StartUtc); // must not have fallen back to CreationTimeUtc
    }

    [Fact]
    public void FileNeverObservedMidGrowthFallsBackToCreationTime()
    {
        // Simulates a node restart landing between polls — a-still-open on the previous process's
        // last poll, already closed (superseded by b) by the time the new process's first poll runs,
        // so this fix's dictionary (freshly empty for the new process) never had a chance to observe
        // it mid-growth.
        var creationTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var path = WriteSegmentFileWithSize("a.mp4", creationTime, sizeBytes: 8192);
        WriteSegmentFileWithSize("b-still-open.mp4", creationTime.AddMinutes(1), sizeBytes: 8192);

        var session = NewSession(_root);
        var fired = new List<RecordingSegment>();
        session.SegmentCompleted += fired.Add;
        var reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRealDataObservedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);

        var segment = Assert.Single(fired);
        Assert.Equal(path, segment.FilePath);
        Assert.Equal(creationTime, segment.StartUtc);
    }

    [Fact]
    public void EndOfOneSegmentAlwaysExactlyEqualsStartOfTheNextEvenAcrossPollsThatRaceEachOther()
    {
        // Reproduces the exact real-world race, confirmed live against a production Segments table:
        // ~41% of all segment transitions in one night's recording had EndUtc(N) != StartUtc(N+1) —
        // a false "gap" of anywhere from a couple of seconds to nearly a minute, even though the
        // underlying video files were continuous the whole time and Playback simply couldn't find a
        // segment covering the (nonexistent) hole between them.
        //
        // The race: the file that becomes segment N+1 is still header-only (below MinRealDataBytes)
        // on the SAME poll where segment N is first noticed as closed — so segment N's end falls back
        // to that file's raw CreationTimeUtc. Only on a LATER poll, once real data has actually grown
        // the file past the threshold, does that same file get its own "first real data" timestamp
        // recorded — a distinctly later moment. Without ResolveSegmentBoundary's write-through, these
        // two independently-computed values for the exact same physical instant can — and, per the
        // production data, routinely do — disagree.
        var t0 = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        var a = WriteSegmentFileWithSize("a.mp4", t0, sizeBytes: 8192); // already closed-worthy, past threshold
        var b = WriteSegmentFileWithSize("b.mp4", t0.AddSeconds(30), sizeBytes: 500); // header-only, below threshold

        var session = NewSession(_root);
        var fired = new List<RecordingSegment>();
        session.SegmentCompleted += fired.Add;
        var reportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstRealDataObservedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Poll 1: b.mp4's mere existence already proves a.mp4 closed (file existence, not size, is
        // what closes a segment — see the loop's own comment) — a.mp4 fires now. Its end resolves
        // via the fallback path, since b.mp4 (still header-only) has no real-data entry yet; under
        // the fix that fallback is written into the dictionary as b.mp4's own boundary too.
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);
        var segmentA = Assert.Single(fired);
        Assert.Equal(a, segmentA.FilePath);

        // b.mp4 grows past the threshold while still the open/last file. Under the fix, the "check
        // last" block must decline to overwrite b.mp4's already-resolved boundary with a fresh
        // DateTime.UtcNow — that's the whole point: once resolved (as a.mp4's end, above), it must
        // stay resolved to that exact same value.
        GrowFile(b, totalSizeBytes: 8192);
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);
        Assert.Single(fired); // still just a.mp4 — b.mp4 isn't closeable until a 3rd file exists

        // c.mp4 arrives, finally proving b.mp4 is closed.
        WriteSegmentFileWithSize("c.mp4", t0.AddSeconds(60), sizeBytes: 500);
        session.PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);

        Assert.Equal(2, fired.Count);
        var segmentB = fired.Single(s => s.FilePath == b);
        Assert.Equal(segmentA.EndUtc, segmentB.StartUtc); // the actual bug: these must never diverge
    }
}

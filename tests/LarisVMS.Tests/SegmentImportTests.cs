using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// Recovery of footage on disk with no Segment row — the repair path for the 0.150.0 reporting outage,
/// where a node kept recording perfectly while the server was never told, leaving hours of real
/// footage invisible to Playback.
/// </summary>
public class SegmentImportTests
{
    private static FileInfo WriteFile(string dir, string name, string content, DateTime? created = null, DateTime? written = null)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        var info = new FileInfo(path);
        if (created is { } c) info.CreationTimeUtc = c;
        if (written is { } w) info.LastWriteTimeUtc = w;
        info.Refresh();
        return info;
    }

    [Fact]
    public void AFileWithNoRowIsSelectedForImport()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var cameraId = Guid.NewGuid();
            var start = new DateTime(2026, 8, 21, 17, 0, 0, DateTimeKind.Utc);
            var file = WriteFile(dir, "a.mp4", "data", created: start, written: start.AddMinutes(1));

            var result = StorageManager.SelectImportableSegments(cameraId, [file], []);

            var item = Assert.Single(result);
            Assert.Equal(cameraId, item.CameraId);
            Assert.Equal("Main", item.StreamRole);
            Assert.Equal(start, item.StartUtc);
            Assert.Equal(start.AddMinutes(1), item.EndUtc);
            Assert.Equal(file.FullName, item.FilePath);
            Assert.Equal(4, item.SizeBytes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AFileTheServerAlreadyKnowsAboutIsSkipped()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var start = new DateTime(2026, 8, 21, 17, 0, 0, DateTimeKind.Utc);
            var file = WriteFile(dir, "a.mp4", "data", created: start, written: start.AddMinutes(1));

            var result = StorageManager.SelectImportableSegments(Guid.NewGuid(), [file],
                new HashSet<string>([file.FullName], StringComparer.OrdinalIgnoreCase));

            Assert.Empty(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void KnownPathMatchingIsCaseInsensitiveSoNothingReimportsEverySweep()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var start = new DateTime(2026, 8, 21, 17, 0, 0, DateTimeKind.Utc);
            var file = WriteFile(dir, "a.mp4", "data", created: start, written: start.AddMinutes(1));

            var result = StorageManager.SelectImportableSegments(Guid.NewGuid(), [file],
                new HashSet<string>([file.FullName.ToUpperInvariant()], StringComparer.OrdinalIgnoreCase));

            Assert.Empty(result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AZeroByteFailedConnectionArtifactIsNeverImported()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var start = new DateTime(2026, 8, 21, 17, 0, 0, DateTimeKind.Utc);
            var file = WriteFile(dir, "empty.mp4", "", created: start, written: start.AddMinutes(1));

            Assert.Empty(StorageManager.SelectImportableSegments(Guid.NewGuid(), [file], []));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AFileWithNonsensicalTimestampsIsSkippedRatherThanImportedWithABadDuration()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var start = new DateTime(2026, 8, 21, 17, 0, 0, DateTimeKind.Utc);
            // Written *before* it was created — a clock change mid-write; importing this would produce
            // a zero/negative-duration row the timeline can't place.
            var file = WriteFile(dir, "backwards.mp4", "data", created: start, written: start.AddMinutes(-5));

            Assert.Empty(StorageManager.SelectImportableSegments(Guid.NewGuid(), [file], []));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

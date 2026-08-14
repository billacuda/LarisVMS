using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

public class MediaTokenTests
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";

    [Fact]
    public void RoundTripsForTheCameraItWasIssuedFor()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(60));

        Assert.True(MediaToken.TryValidate(token, cameraId, Key, out _));
    }

    [Fact]
    public void RejectsATokenIssuedForADifferentCamera()
    {
        var token = MediaToken.Issue(Guid.NewGuid(), Key, TimeSpan.FromSeconds(60));

        Assert.False(MediaToken.TryValidate(token, Guid.NewGuid(), Key, out var error));
        Assert.Equal("camera mismatch", error);
    }

    [Fact]
    public void RejectsAnExpiredToken()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(-1));

        Assert.False(MediaToken.TryValidate(token, cameraId, Key, out var error));
        Assert.Equal("expired", error);
    }

    [Fact]
    public void RejectsATokenSignedWithADifferentKey()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(60));
        var wrongKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba98765432";

        Assert.False(MediaToken.TryValidate(token, cameraId, wrongKey, out var error));
        Assert.Equal("signature mismatch", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    [InlineData("payload-with-no-dot-separator")]
    [InlineData("cameraid:123.not-hex-!!")]
    public void RejectsMalformedTokens(string token)
    {
        Assert.False(MediaToken.TryValidate(token, Guid.NewGuid(), Key, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RejectsANullToken()
    {
        Assert.False(MediaToken.TryValidate(null, Guid.NewGuid(), Key, out var error));
        Assert.Equal("missing token", error);
    }

    // ── IssueForSegment / TryValidateSegment (M7) ───────────────────────────

    [Fact]
    public void SegmentTokenRoundTripsForTheCameraAndPathItWasIssuedFor()
    {
        var cameraId = Guid.NewGuid();
        const string path = @"E:\LarisVMS\recordings\cam-1\main\2026\08\09\14\20260809T140000Z.mp4";
        var token = MediaToken.IssueForSegment(cameraId, path, Key, TimeSpan.FromSeconds(30));

        Assert.True(MediaToken.TryValidateSegment(token, cameraId, path, Key, out _));
    }

    [Fact]
    public void SegmentTokenSurvivesAWindowsDriveLetterColonInThePath()
    {
        // The payload's own field separator is ':' — a Windows path's drive-letter colon must not
        // be mistaken for one, or the path silently gets truncated/misparsed on validation.
        var cameraId = Guid.NewGuid();
        const string path = @"C:\ProgramData\LarisVMS\recordings\cam-1\main\file.mp4";
        var token = MediaToken.IssueForSegment(cameraId, path, Key, TimeSpan.FromSeconds(30));

        Assert.True(MediaToken.TryValidateSegment(token, cameraId, path, Key, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void SegmentTokenRejectsAMismatchedPath()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.IssueForSegment(cameraId, @"C:\a\segment1.mp4", Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateSegment(token, cameraId, @"C:\a\segment2.mp4", Key, out var error));
        Assert.Equal("path mismatch", error);
    }

    [Fact]
    public void SegmentTokenRejectsAMismatchedCamera()
    {
        const string path = @"C:\a\segment1.mp4";
        var token = MediaToken.IssueForSegment(Guid.NewGuid(), path, Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateSegment(token, Guid.NewGuid(), path, Key, out var error));
        Assert.Equal("camera mismatch", error);
    }

    [Fact]
    public void SegmentTokenRejectsAnExpiredToken()
    {
        var cameraId = Guid.NewGuid();
        const string path = @"C:\a\segment1.mp4";
        var token = MediaToken.IssueForSegment(cameraId, path, Key, TimeSpan.FromSeconds(-1));

        Assert.False(MediaToken.TryValidateSegment(token, cameraId, path, Key, out var error));
        Assert.Equal("expired", error);
    }

    [Fact]
    public void ALiveViewTokenDoesNotValidateAsASegmentToken()
    {
        // The two token families are deliberately separate (see MediaToken's doc comment on
        // IssueForSegment) — confirms a live-view token can't be replayed against the segment
        // proxy just because they're signed with the same key.
        var cameraId = Guid.NewGuid();
        var liveToken = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateSegment(liveToken, cameraId, @"C:\a\segment1.mp4", Key, out _));
    }

    // ── IssueForExport / TryValidateExport ──────────────────────────────────

    [Fact]
    public void ExportTokenRoundTripsForTheCameraAndItemItWasIssuedFor()
    {
        var cameraId = Guid.NewGuid();
        var exportItemId = Guid.NewGuid();
        var token = MediaToken.IssueForExport(cameraId, exportItemId, Key, TimeSpan.FromSeconds(30));

        Assert.True(MediaToken.TryValidateExport(token, cameraId, exportItemId, Key, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void ExportTokenRejectsAMismatchedExportItem()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.IssueForExport(cameraId, Guid.NewGuid(), Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateExport(token, cameraId, Guid.NewGuid(), Key, out var error));
        Assert.Equal("export item mismatch", error);
    }

    [Fact]
    public void ExportTokenRejectsAMismatchedCamera()
    {
        var exportItemId = Guid.NewGuid();
        var token = MediaToken.IssueForExport(Guid.NewGuid(), exportItemId, Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateExport(token, Guid.NewGuid(), exportItemId, Key, out var error));
        Assert.Equal("camera mismatch", error);
    }

    [Fact]
    public void ExportTokenRejectsAnExpiredToken()
    {
        var cameraId = Guid.NewGuid();
        var exportItemId = Guid.NewGuid();
        var token = MediaToken.IssueForExport(cameraId, exportItemId, Key, TimeSpan.FromSeconds(-1));

        Assert.False(MediaToken.TryValidateExport(token, cameraId, exportItemId, Key, out var error));
        Assert.Equal("expired", error);
    }

    [Fact]
    public void ASegmentTokenDoesNotValidateAsAnExportToken()
    {
        // Separate token families, same reasoning as ALiveViewTokenDoesNotValidateAsASegmentToken —
        // confirms a segment token can't be replayed against the export-trigger endpoint.
        var cameraId = Guid.NewGuid();
        var segmentToken = MediaToken.IssueForSegment(cameraId, @"C:\a\segment1.mp4", Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateExport(segmentToken, cameraId, Guid.NewGuid(), Key, out _));
    }

    // ── IssueForExportDownload / TryValidateExportDownload ──────────────────

    [Fact]
    public void ExportDownloadTokenRoundTripsForTheItemAndPathItWasIssuedFor()
    {
        var exportItemId = Guid.NewGuid();
        const string path = @"E:\LarisVMS\recordings\exports\cam1_20260813T000000_20260813T010000.mp4";
        var token = MediaToken.IssueForExportDownload(exportItemId, path, Key, TimeSpan.FromSeconds(30));

        Assert.True(MediaToken.TryValidateExportDownload(token, exportItemId, path, Key, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void ExportDownloadTokenSurvivesAWindowsDriveLetterColonInThePath()
    {
        var exportItemId = Guid.NewGuid();
        const string path = @"C:\ProgramData\LarisVMS\recordings\exports\file.mp4";
        var token = MediaToken.IssueForExportDownload(exportItemId, path, Key, TimeSpan.FromSeconds(30));

        Assert.True(MediaToken.TryValidateExportDownload(token, exportItemId, path, Key, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void ExportDownloadTokenRejectsAMismatchedPath()
    {
        var exportItemId = Guid.NewGuid();
        var token = MediaToken.IssueForExportDownload(exportItemId, @"C:\a\export1.mp4", Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateExportDownload(token, exportItemId, @"C:\a\export2.mp4", Key, out var error));
        Assert.Equal("path mismatch", error);
    }

    [Fact]
    public void ExportDownloadTokenRejectsAMismatchedExportItem()
    {
        const string path = @"C:\a\export1.mp4";
        var token = MediaToken.IssueForExportDownload(Guid.NewGuid(), path, Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateExportDownload(token, Guid.NewGuid(), path, Key, out var error));
        Assert.Equal("export item mismatch", error);
    }

    [Fact]
    public void AnExportTriggerTokenDoesNotValidateAsAnExportDownloadToken()
    {
        var cameraId = Guid.NewGuid();
        var exportItemId = Guid.NewGuid();
        var triggerToken = MediaToken.IssueForExport(cameraId, exportItemId, Key, TimeSpan.FromSeconds(30));

        Assert.False(MediaToken.TryValidateExportDownload(triggerToken, exportItemId, @"C:\a\export1.mp4", Key, out _));
    }
}

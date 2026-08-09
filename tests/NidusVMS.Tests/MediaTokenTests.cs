using NidusVMS.Core.Security;

namespace NidusVMS.Tests;

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
        const string path = @"E:\NidusVMS\recordings\cam-1\main\2026\08\09\14\20260809T140000Z.mp4";
        var token = MediaToken.IssueForSegment(cameraId, path, Key, TimeSpan.FromSeconds(30));

        Assert.True(MediaToken.TryValidateSegment(token, cameraId, path, Key, out _));
    }

    [Fact]
    public void SegmentTokenSurvivesAWindowsDriveLetterColonInThePath()
    {
        // The payload's own field separator is ':' — a Windows path's drive-letter colon must not
        // be mistaken for one, or the path silently gets truncated/misparsed on validation.
        var cameraId = Guid.NewGuid();
        const string path = @"C:\ProgramData\NidusVMS\recordings\cam-1\main\file.mp4";
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
}

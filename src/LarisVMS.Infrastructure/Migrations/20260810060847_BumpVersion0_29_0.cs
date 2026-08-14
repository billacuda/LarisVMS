using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_29_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 29, 0, GETUTCDATE(), 'Found while double-checking the v0.28.0 sensitivity investigation: three Motion-mode cameras had 35+ hours of unbroken, gap-free segment retention, which is impossible if motion gating were actually running against them. Root cause: RecordingSession.SegmentCompleted was wired up once, at camera-recording-start time, capturing that moment''s camera config by reference in the closure. Already-recording cameras never got this handler re-wired on later reconciles, so any change to Recording.Mode, MotionPreRollSeconds, or MotionPostRollSeconds on an already-running camera had zero effect until its session happened to restart for an unrelated reason. NodeWorker now keeps a per-camera config cache refreshed on every reconcile, the closure captures only the immutable CameraId, and the segment-completed handler looks up fresh config on every invocation - no node or session restart required for this fix to take effect. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 29 AND Patch = 0");
        }
    }
}

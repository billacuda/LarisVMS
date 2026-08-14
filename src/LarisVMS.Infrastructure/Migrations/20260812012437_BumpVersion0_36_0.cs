using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_36_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 36, 0, GETUTCDATE(), 'Fixed a gap in v0.35.0''s ONVIF event gating: many cameras send exactly one notification per motion edge (rising, then nothing until falling), so the recency-based HasMotionSince check could go stale mid-event and start discarding segments before the falling event ever arrived. CameraEventSession now also exposes IsMotionActive (true for as long as a span is open, no timeout of its own) and DecideMotionSegment keeps a segment if either check passes - recording now stays retained continuously from rising to falling regardless of how sparse the camera notifications are in between. Same fix applied to timeline checkpointing. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 36 AND Patch = 0");
        }
    }
}

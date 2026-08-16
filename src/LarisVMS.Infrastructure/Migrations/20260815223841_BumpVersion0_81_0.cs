using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_81_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 81, 0, GETUTCDATE(), 'Fixed: live tiles could repeatedly stutter/reconnect (""3-second loop"") -- 0.78''s drift fix still corrected drift via a hard seek, which is not guaranteed to land on a keyframe; a large drift (confirmed cause: setInterval throttling on a backgrounded tab letting drift build up undetected) could seek off-keyframe and throw a real decode error, tearing the session down. Catch-up now speeds up playback instead of seeking -- zero discontinuity risk -- falling back to a seek only past 15s of drift. Fixed: a View cell''s playback-mode mini-timeline (and any other timeline created without bucket-coverage data) never painted at all -- draw() was only ever triggered via reload(), which no-ops without a getBuckets option; the canvas existed, correctly sized, just never had its first frame drawn. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 81 AND Patch = 0");
        }
    }
}

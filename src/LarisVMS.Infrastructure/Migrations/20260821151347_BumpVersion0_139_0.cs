using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_139_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 139, 0, GETUTCDATE(), 'Fixed: classified-detection snapshots (Person/Vehicle/Face/Animal/Object) regressed to wrong, sometimes empty-scene thumbnails, plus a spike of /playback-thumbnail 502s. v0.135.0 moved every snapshot''s sample point into the recording''s pre-roll buffer -- correct for plain motion, but wrong for a classified detection: the camera''s classifier only confirms what it saw at StartUtc itself, so reaching into the pre-roll buffer risked sampling before the subject entered frame. The same shift explains the 502 spike too -- landing further from StartUtc made an exact-instant lookup more likely to hit the wrong segment or a segment''s drift-prone tail. Classified detections are back to StartUtc + 1s (v0.128.0''s original behavior); plain motion and custom event-tag spans keep the pre-roll-based sample point, which was correct for them. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 139 AND Patch = 0");
        }
    }
}

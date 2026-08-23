using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 3, GETUTCDATE(), 'Security: closed a gap where a CameraAccess-restricted user could open/scrub any camera''s live or recorded video by GUID via /live, /playback-segment, per-camera timeline/segments, thumbnails, and zone-editor snapshot capture, none of which enforced the per-camera check the list pages already had; also filtered three endpoints (motion-state, detection-state, overview timeline) that previously handed back every camera''s id unfiltered. Media tokens now travel as an Authorization header (query param kept alongside for rollout compatibility). Node registration key comparison is now constant-time. Node change; install-node.ps1 re-run not needed, ordinary auto-update covers it.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 3");
        }
    }
}

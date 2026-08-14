using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_47_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 47, 0, GETUTCDATE(), 'Multi-camera video export via a new async job + Exports page. Also: double-click focused/fullscreen view on Live and Playback tiles with wheel zoom/drag-pan, a single-camera playback toggle on Live tiles, exact-time click-to-edit and arrow-key nudge on Playback''s timeline, and pagination for the Cameras and Nodes tables. See CHANGELOG for full details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 47 AND Patch = 0");
        }
    }
}
